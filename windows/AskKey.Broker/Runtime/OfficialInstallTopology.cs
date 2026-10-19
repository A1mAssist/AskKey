using System.Security.Cryptography.X509Certificates;

namespace AskKey.Broker.Runtime;

public enum OfficialInstallDecisionKind
{
    Accepted,
    DevelopmentAccepted,
    RelocatedOrRenamed,
    HelperMismatch,
}

public sealed class OfficialInstallDecision
{
    public OfficialInstallDecisionKind Kind { get; }
    public string? HelperPath { get; }

    private OfficialInstallDecision(OfficialInstallDecisionKind kind, string? helperPath)
    {
        Kind = kind;
        HelperPath = helperPath;
    }

    public static OfficialInstallDecision Accepted(string helperPath)
        => new(OfficialInstallDecisionKind.Accepted, helperPath);

    public static OfficialInstallDecision DevelopmentAccepted(string helperPath)
        => new(OfficialInstallDecisionKind.DevelopmentAccepted, helperPath);

    public static OfficialInstallDecision RelocatedOrRenamed { get; }
        = new(OfficialInstallDecisionKind.RelocatedOrRenamed, null);

    public static OfficialInstallDecision HelperMismatch { get; }
        = new(OfficialInstallDecisionKind.HelperMismatch, null);
}

public sealed class OfficialInstallTopologyException : Exception
{
    public OfficialInstallTopologyException(OfficialInstallDecision decision)
        : base(OfficialInstallTopology.NextStep(decision)) => Decision = decision;

    public OfficialInstallDecision Decision { get; }
}

/// <summary>
/// Windows counterpart of the macOS bundle/codesign topology: the official install
/// lives under %LOCALAPPDATA%\AskKey (Velopack Setup.exe) or Program Files (per-machine
/// MSI), and the helper must sit beside its host.
/// </summary>
public static class OfficialInstallTopology
{
    public const string HelperFileName = "askkey.exe";
    public const string HostFileName = "AskKey.exe";
    // NTFS treats AskKey.exe and askkey.exe as the same name, so the helper lives
    // in a subdirectory while keeping the command name used by MCP configuration.
    public const string HelperDirectoryName = "Helpers";
    public const string HelperRelativePath = HelperDirectoryName + "\\" + HelperFileName;

    // Setup.exe installs per-user; the MSI with --instLocation PerMachine installs
    // machine-wide. Both are official layouts, so both pass the topology gate.
    private const string PerUserFolderName = "AskKey";
    private const string PerMachineFolderName = "Ask Key";

    public static string CanonicalAppPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), PerUserFolderName);

    public static string CanonicalMachineAppPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), PerMachineFolderName);

    public static IReadOnlyList<string> OfficialAppPaths { get; } =
        new[] { CanonicalAppPath, CanonicalMachineAppPath };

    /// <summary>
    /// Velopack extracts the payload into <c>current\</c> behind a root execution stub,
    /// so the folder the host actually runs from is one level below the install root.
    /// </summary>
    public const string PayloadDirectoryName = "current";

    /// <summary>Maps an install root to the directory the host executable runs from.</summary>
    public static string PayloadPath(string installRoot)
        => Path.Combine(Normalize(installRoot), PayloadDirectoryName);

    /// <summary>
    /// The install roots the official packages create: per-user from Setup.exe, and
    /// per-machine from the MSI. Each root maps to the payload directory the host runs from.
    /// </summary>
    public static IEnumerable<string> OfficialPayloadPaths => OfficialAppPaths.Select(PayloadPath);

    public static string CanonicalHelperPath => Path.Combine(
        PayloadPath(CanonicalAppPath), HelperDirectoryName, HelperFileName);

    public static string CanonicalHostPath => Path.Combine(PayloadPath(CanonicalAppPath), HostFileName);

    public static OfficialInstallDecision Decide(
        string appPath,
        string? helperPath = null,
        bool isDevelopmentBuild = false,
        Func<string, string>? resolvePath = null)
    {
        resolvePath ??= path => Path.GetFullPath(path);
        var normalizedApp = Normalize(appPath);
        var expectedHelper = Normalize(Path.Combine(normalizedApp, HelperRelativePath));
        var helper = Normalize(helperPath ?? expectedHelper);
        if (!string.Equals(helper, expectedHelper, StringComparison.OrdinalIgnoreCase))
        {
            return OfficialInstallDecision.HelperMismatch;
        }
        if (isDevelopmentBuild)
        {
            return OfficialInstallDecision.DevelopmentAccepted(helper);
        }
        var resolvedApp = Normalize(resolvePath(normalizedApp));
        // Accept the install root and the payload directory inside it: Velopack launches the
        // host from <root>\current, while Setup.exe --installto callers pass the root.
        // Exact equality already pins the folder name, so "Ask Key" (MSI) and "AskKey"
        // (Setup.exe) are both covered by their own entry.
        var isOfficialPath = OfficialAppPaths.Any(canonical =>
            new[] { Normalize(canonical), PayloadPath(canonical) }.Any(candidate =>
                string.Equals(normalizedApp, candidate, StringComparison.OrdinalIgnoreCase)
                && string.Equals(resolvedApp, candidate, StringComparison.OrdinalIgnoreCase)));
        if (!isOfficialPath)
        {
            return OfficialInstallDecision.RelocatedOrRenamed;
        }
        return OfficialInstallDecision.Accepted(helper);
    }

    public static bool AllowsOfficialRuntime(OfficialInstallDecision decision)
        => decision.Kind is OfficialInstallDecisionKind.Accepted
            or OfficialInstallDecisionKind.DevelopmentAccepted;

    public static string NextStep(OfficialInstallDecision decision) => decision.Kind switch
    {
        OfficialInstallDecisionKind.Accepted or OfficialInstallDecisionKind.DevelopmentAccepted => string.Empty,
        OfficialInstallDecisionKind.RelocatedOrRenamed =>
            $"Ask Key must stay at {CanonicalAppPath} or {CanonicalMachineAppPath}. Reinstall the official package, then open that copy.",
        _ => "This Ask Key helper does not match the official app. Reinstall the official package, then try again.",
    };

    public static string ResolvedHelperPath(
        string appPath,
        string? helperPath = null,
        bool isDevelopmentBuild = false,
        Func<string, string>? resolvePath = null)
    {
        var decision = Decide(appPath, helperPath, isDevelopmentBuild, resolvePath);
        return decision.HelperPath
            ?? throw new OfficialInstallTopologyException(decision);
    }

    private static string Normalize(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}

/// <summary>Authenticode facts for one executable. Unsigned files yield IsValid=false.</summary>
public sealed class HelperHostSigningFacts
{
    public string? Publisher { get; init; }
    public string? Thumbprint { get; init; }
    public bool IsValid { get; init; }

    public static HelperHostSigningFacts Unsigned { get; } = new();
}

public enum HelperHostSignatureKind
{
    Signed,
    Unsigned,
}

public static class HelperHostSignatureTrust
{
    public static HelperHostSignatureKind Classify(HelperHostSigningFacts facts)
        => facts.IsValid && !string.IsNullOrWhiteSpace(facts.Publisher)
            ? HelperHostSignatureKind.Signed
            : HelperHostSignatureKind.Unsigned;

    /// <summary>
    /// Production requires both binaries to carry the same publisher identity.
    /// Development and test builds skip signature trust, matching the macOS ad-hoc
    /// development gate. Unsigned builds opt into the embedded helper manifest.
    /// </summary>
    public static bool Allows(
        HelperHostSigningFacts helper,
        HelperHostSigningFacts host,
        bool isDevelopmentBuild,
        bool allowUnsigned = UnsignedBuild.Allowed)
    {
        if (isDevelopmentBuild || allowUnsigned) return true;
        if (Classify(helper) != HelperHostSignatureKind.Signed
            || Classify(host) != HelperHostSignatureKind.Signed)
        {
            return false;
        }
        return string.Equals(
            helper.Publisher!.Trim(), host.Publisher!.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class HelperHostApplicationException : Exception
{
    public HelperHostApplicationException(string message) : base(message) { }

    public static HelperHostApplicationException Unavailable { get; }
        = new("Ask Key could not open the host application.");

    public static HelperHostApplicationException Failed { get; }
        = new("Ask Key could not open the host application.");
}

/// <summary>
/// Resolves the host executable beside this helper, then opens it only after the
/// install-topology and publisher gates pass.
/// </summary>
public static class HelperHostApplication
{
    /// <summary>Structural host path: Helpers\askkey.exe belongs to AskKey.exe in its parent.</summary>
    public static string? HostPath(string helperPath)
    {
        var resolved = Path.GetFullPath(helperPath);
        var directory = Path.GetDirectoryName(resolved);
        if (directory is null) return null;
        // Structural, like Swift's bundle lookup: a missing host fails at the
        // topology gate or opener, never by silently choosing another path.
        return Path.Combine(Path.GetDirectoryName(directory) ?? directory,
            OfficialInstallTopology.HostFileName);
    }

    public static void OpenHost(
        string helperPath,
        bool isDevelopmentBuild,
        Action<string>? opener = null,
        Func<string, HelperHostSigningFacts>? signingFacts = null,
        Func<string, string>? resolvePath = null,
        bool allowUnsigned = UnsignedBuild.Allowed)
    {
        opener ??= Open;
        signingFacts ??= SigningFacts;
        resolvePath ??= path => Path.GetFullPath(path);

        var host = HostPath(helperPath)
            ?? throw new HelperHostApplicationException(
                OfficialInstallTopology.NextStep(OfficialInstallDecision.HelperMismatch));
        var directory = Path.GetDirectoryName(Path.GetFullPath(host))!;
        var decision = OfficialInstallTopology.Decide(
            directory, helperPath, isDevelopmentBuild, resolvePath);
        if (!OfficialInstallTopology.AllowsOfficialRuntime(decision))
        {
            throw new HelperHostApplicationException(OfficialInstallTopology.NextStep(decision));
        }
        if (!HelperHostSignatureTrust.Allows(
                signingFacts(helperPath), signingFacts(host), isDevelopmentBuild, allowUnsigned))
        {
            throw new HelperHostApplicationException(
                OfficialInstallTopology.NextStep(OfficialInstallDecision.HelperMismatch));
        }
        opener(host);
    }

    /// <summary>
    /// Publisher facts from the Authenticode signer certificate. Dev/test builds
    /// skip this check; production treats a missing signer as unsigned.
    /// </summary>
    public static HelperHostSigningFacts SigningFacts(string path)
    {
        try
        {
            // Only a WinVerifyTrust pass counts as signed: a certificate embedded in the
            // PE image says nothing about whether the signature still validates.
            var signer = AuthenticodeVerifier.Shared.SignerSubject(path);
            if (string.IsNullOrWhiteSpace(signer)) return HelperHostSigningFacts.Unsigned;
#pragma warning disable SYSLIB0057 // Signed-file extraction has no X509CertificateLoader equivalent.
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            var publisher = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            return new HelperHostSigningFacts
            {
                Publisher = string.IsNullOrWhiteSpace(publisher) ? null : publisher,
                Thumbprint = certificate.Thumbprint,
                IsValid = true,
            };
        }
        catch (Exception)
        {
            return HelperHostSigningFacts.Unsigned;
        }
    }

    private static void Open(string hostPath)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = hostPath,
                UseShellExecute = true,
            });
            if (process is null) throw HelperHostApplicationException.Failed;
        }
        catch (global::System.ComponentModel.Win32Exception)
        {
            throw HelperHostApplicationException.Failed;
        }
    }
}
