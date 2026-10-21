using System.IO.Pipes;
using System.Text;
using System.Runtime.InteropServices;

namespace AskKey.Broker.Transport;

/// <summary>
/// Client-side authenticity gate for the Broker pipe. The pipe DACL only proves the peer
/// runs as the same user, which every local process does, so a same-user process can squat
/// the pipe name and impersonate the Broker. Before shipping a request the client resolves
/// the server process and requires the AskKey.exe installed beside this helper.
/// </summary>
public sealed class BrokerServerIdentity
{
    private readonly Func<uint, string?>? _imagePath;
    private readonly Func<string, Runtime.HelperHostSigningFacts> _signingFacts;
    private readonly bool _allowUnsigned;
    private readonly string? _helperPath;

    internal BrokerServerIdentity(
        Func<uint, string?>? imagePath,
        Func<string, Runtime.HelperHostSigningFacts> signingFacts,
        bool allowUnsigned,
        string? helperPath = null)
    {
        _imagePath = imagePath;
        _signingFacts = signingFacts;
        _allowUnsigned = allowUnsigned;
        _helperPath = helperPath;
    }

    /// <summary>
    /// Production gate: the server process must be the AskKey.exe installed in the very
    /// install root this helper runs from. Root equality is the part the old path
    /// whitelist could not express - every official root looked equally valid, so a
    /// same-user process could plant an AskKey.exe under %LOCALAPPDATA%\AskKey and
    /// impersonate a per-machine host (or the other way round).
    /// A signed release additionally requires the host to carry this helper's publisher,
    /// so any valid signature no longer passes. An unsigned release
    /// (<see cref="Runtime.UnsignedBuild"/>) has no certificate to compare and therefore
    /// degrades to the same-install-root path check alone; development builds skip the
    /// gate entirely.
    /// </summary>
    public static BrokerServerIdentity Production { get; } = new(
        ProcessImagePath,
        Runtime.HelperHostApplication.SigningFacts,
        Runtime.UnsignedBuild.Allowed,
        Environment.ProcessPath);

    /// <summary>Test seam: a testhost-owned pipe is not AskKey.exe, so tests skip the gate.</summary>
    public static BrokerServerIdentity AlwaysTrusted { get; } =
        new(imagePath: null, signingFacts: Runtime.HelperHostApplication.SigningFacts, allowUnsigned: true);

    /// <summary>
    /// Test seam: the identity that speaks for a helper at an explicit install path, so the
    /// policy can be driven for roots a development build cannot occupy.
    /// </summary>
    internal static BrokerServerIdentity ForHelper(
        string helperPath,
        Func<string, Runtime.HelperHostSigningFacts> signingFacts,
        bool allowUnsigned)
        => new(ProcessImagePath, signingFacts, allowUnsigned, helperPath);

    /// <summary>False when the peer is not the official Ask Key Broker.</summary>
    public bool IsTrusted(NamedPipeClientStream pipe)
    {
        if (_imagePath is null) return true;
#if DEBUG
        // A development build never speaks to an installed host; the gate exists to
        // protect released clients, so it is compiled out of development builds entirely.
        return true;
#else
        // A helper outside the official install is not a shipped client: it has no
        // credentials worth stealing, and the test harness runs its copies from a temp
        // directory. Only a helper beside AskKey.exe submits requests.
        if (!IsOfficialHelperProcess()) return true;
        if (!TryGetServerProcessId(pipe, out var processId)) return false;
        return IsOfficialHostImage(_imagePath(processId));
#endif
    }

    /// <summary>True when this process is the helper from an official install.</summary>
    private bool IsOfficialHelperProcess()
        => _helperPath is not null && IsOfficialHelperPath(_helperPath);

    /// <summary>
    /// The helper must sit in <c>Helpers\askkey.exe</c> beside an installed AskKey.exe.
    /// Exposed so the rule is testable without moving this assembly.
    /// </summary>
    public static bool IsOfficialHelperPath(string helperPath)
    {
        if (string.IsNullOrEmpty(helperPath)) return false;
        var full = Path.GetFullPath(helperPath);
        if (!string.Equals(
                Path.GetFileName(full),
                Runtime.OfficialInstallTopology.HelperFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var helpersDirectory = Path.GetDirectoryName(full);
        var hostDirectory = helpersDirectory is null ? null : Path.GetDirectoryName(helpersDirectory);
        if (helpersDirectory is null || hostDirectory is null) return false;
        if (!string.Equals(
                Path.GetFileName(helpersDirectory),
                Runtime.OfficialInstallTopology.HelperDirectoryName,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return Runtime.OfficialInstallTopology.AllowsOfficialRuntime(
            Runtime.OfficialInstallTopology.Decide(
                hostDirectory,
                full,
                isDevelopmentBuild: false,
                resolvePath: path => path));
    }

    /// <summary>
    /// The identity policy on its own, split out so it stays testable in development
    /// builds where the pipe-time gate above is compiled out. The host must be the
    /// AskKey.exe that structurally belongs to this identity's helper, and that pair must
    /// sit in an official install root: same root as the caller, not merely any root.
    /// </summary>
    public bool IsOfficialHostImage(string? imagePath)
    {
        if (string.IsNullOrEmpty(imagePath) || _helperPath is null) return false;
        var image = Path.GetFullPath(imagePath);
        if (!string.Equals(
                Path.GetFileName(image),
                Runtime.OfficialInstallTopology.HostFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var directory = Path.GetDirectoryName(image);
        if (directory is null) return false;
        if (!Runtime.OfficialInstallTopology.IsHostOfHelper(directory, _helperPath)) return false;
        return Runtime.HelperHostSignatureTrust.Allows(
            _signingFacts(_helperPath), _signingFacts(image),
            isDevelopmentBuild: false,
            allowUnsigned: _allowUnsigned);
    }

    private static string? ProcessImagePath(uint id)
    {
        if (id == 0) return null;
        var process = OpenProcess(ProcessQueryLimitedInformation, false, id);
        if (process == IntPtr.Zero) return null;
        try
        {
            var capacity = 1024u;
            var buffer = new StringBuilder((int)capacity);
            while (true)
            {
                if (QueryFullProcessImageNameW(process, 0, buffer, ref capacity)) return buffer.ToString();
                if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer) return null;
                capacity *= 2;
                if (capacity > 32768u) return null;
                buffer = new StringBuilder((int)capacity);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorInsufficientBuffer = 122;

    internal static bool TryGetServerProcessId(NamedPipeClientStream pipe, out uint processId)
    {
        try
        {
            return GetNamedPipeServerProcessId(pipe.SafePipeHandle, out processId);
        }
        catch (ObjectDisposedException)
        {
            processId = 0;
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(
        Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(
        IntPtr hProcess, uint dwFlags, StringBuilder exeName, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}