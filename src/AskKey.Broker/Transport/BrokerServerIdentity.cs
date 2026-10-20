using System.IO.Pipes;
using System.Text;
using System.Runtime.InteropServices;

namespace AskKey.Broker.Transport;

/// <summary>
/// Client-side authenticity gate for the Broker pipe. The pipe DACL only proves the peer
/// runs as the same user, which every local process does, so a same-user process can squat
/// the pipe name and impersonate the Broker. Before shipping a request the client resolves
/// the server process and requires the official host executable.
/// </summary>
public sealed class BrokerServerIdentity
{
    private readonly Func<uint, string?>? _imagePath;
    private readonly Func<string, string?>? _signerSubject;
    private readonly bool _allowUnsigned;

    private BrokerServerIdentity(
        Func<uint, string?>? imagePath,
        Func<string, string?>? signerSubject,
        bool allowUnsigned)
    {
        _imagePath = imagePath;
        _signerSubject = signerSubject;
        _allowUnsigned = allowUnsigned;
    }

    /// <summary>
    /// Production gate: the server process must be the installed AskKey.exe under a known
    /// install root. An unsigned release (<see cref="Runtime.UnsignedBuild"/>) cannot check
    /// the signer, so it gets the path check only until the build is signed; a signed release
    /// additionally requires the Authenticode pass. Development builds skip the gate entirely.
    /// </summary>
    public static BrokerServerIdentity Production { get; } = new(
        ProcessImagePath,
        Runtime.AuthenticodeVerifier.Shared.SignerSubject,
        Runtime.UnsignedBuild.Allowed);

    /// <summary>Test seam: a testhost-owned pipe is not AskKey.exe, so tests skip the gate.</summary>
    public static BrokerServerIdentity AlwaysTrusted { get; } =
        new(imagePath: null, signerSubject: null, allowUnsigned: true);



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
        var imagePath = _imagePath(processId);
        if (!IsOfficialHostImage(imagePath)) return false;
        return _allowUnsigned || _signerSubject?.Invoke(imagePath!) is not null;
#endif
    }

    /// <summary>True when this process is the helper from an official install.</summary>
    private static bool IsOfficialHelperProcess()
    {
        var self = Environment.ProcessPath;
        if (string.IsNullOrEmpty(self)) return false;
        return IsOfficialHelperPath(self);
    }

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
    /// builds where the pipe-time gate above is compiled out.
    /// </summary>
    public static bool IsOfficialHostImage(string? imagePath)
    {
        if (string.IsNullOrEmpty(imagePath)) return false;
        if (!string.Equals(
                Path.GetFileName(imagePath),
                Runtime.OfficialInstallTopology.HostFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var directory = Path.GetDirectoryName(Path.GetFullPath(imagePath));
        if (directory is null) return false;
        return Runtime.OfficialInstallTopology.AllowsOfficialRuntime(
            Runtime.OfficialInstallTopology.Decide(directory));
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
