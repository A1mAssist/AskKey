using AskKey.Broker.Runtime;

namespace AskKey.Integrations;

/// <summary>
/// Windows replacement for the macOS code-signing check. A signed release helper
/// must share the host's publisher; an unsigned build may instead pin the helper
/// payload manifest. Development builds may run an unsigned helper beside the host.
/// </summary>
public sealed class HelperTrust
{
    public const string DevelopmentVariable = "ASKKEY_DEVELOPMENT";

    private readonly IAuthenticodeVerifier _verifier;
    private readonly Func<string, string?> _environment;
    private readonly HelperIntegrity _integrity;
    private readonly bool _allowUnsignedIntegrity;

    public HelperTrust(
        IAuthenticodeVerifier? verifier = null,
        Func<string, string?>? environment = null,
        HelperIntegrity? integrity = null,
        bool? allowUnsignedIntegrity = null)
    {
        _verifier = verifier ?? AuthenticodeVerifier.Shared;
        _environment = environment ?? Environment.GetEnvironmentVariable;
        _integrity = integrity ?? HelperIntegrity.FromAssembly();
        _allowUnsignedIntegrity = allowUnsignedIntegrity ?? UnsignedBuild.Allowed;
    }

    public bool IsDevelopment =>
        _environment(DevelopmentVariable) is "1" or "true" or "TRUE" or "True";

    /// <summary>Host is the running app when omitted; only the host's subject is trusted.</summary>
    public bool IsTrusted(string helperPath, string? hostExecutablePath = null)
    {
        if (!IsExecutableRegularFile(helperPath)) return false;
        hostExecutablePath ??= Environment.ProcessPath;
        if (hostExecutablePath is null || !File.Exists(hostExecutablePath)) return false;

        var helperSubject = _verifier.SignerSubject(helperPath);
        if (IsDevelopment && IsDevelopmentHelperLocation(helperPath, hostExecutablePath))
        {
            return true;
        }
        if (helperSubject is not null)
        {
            var hostSubject = _verifier.SignerSubject(hostExecutablePath);
            if (hostSubject is not null
                && string.Equals(Normalize(hostSubject), Normalize(helperSubject), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return _allowUnsignedIntegrity && _integrity.IsTrusted(helperPath);
    }

    public static bool IsExecutableRegularFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            if (WindowsFileSecurity.IsLinkOrReparse(path)) return false;
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0) return false;
            // PE images start with MZ; a script or text file cannot be a helper.
            Span<byte> header = stackalloc byte[2];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return stream.Read(header) == 2 && header[0] == (byte)'M' && header[1] == (byte)'Z';
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsDevelopmentHelperLocation(string helperPath, string hostPath)
    {
        var hostDirectory = Path.GetDirectoryName(Path.GetFullPath(hostPath));
        var helperDirectory = Path.GetDirectoryName(Path.GetFullPath(helperPath));
        if (hostDirectory is null || helperDirectory is null) return false;
        return string.Equals(helperDirectory, hostDirectory, StringComparison.OrdinalIgnoreCase)
            || string.Equals(helperDirectory, Path.Combine(hostDirectory, "Helpers"),
                StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string subject) => subject.Trim();
}

/// <summary>Adapter hook kept from Swift: <see cref="Executable"/> trusts only a signed
/// helper that matches the host; <see cref="Development"/> trusts any executable.</summary>
public sealed class CodexHelperSigning
{
    public CodexHelperSigning(Func<string, bool> isTrusted) => IsTrusted = isTrusted;

    public Func<string, bool> IsTrusted { get; }

    public static CodexHelperSigning Executable { get; } = new(path => new HelperTrust().IsTrusted(path));

    public static CodexHelperSigning Development { get; } = new(HelperTrust.IsExecutableRegularFile);
}
