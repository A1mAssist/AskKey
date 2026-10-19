using AskKey.Broker.Runtime;

namespace AskKey.Guard;

/// <summary>
/// Production accepts AskKey.exe only under the per-machine Program Files install
/// (root or the current payload), never the user-writable per-user install. Dev
/// mode accepts a repo build. Signed files must pass Authenticode; unsigned
/// release builds opt in through ASKKEY_UNSIGNED.
/// </summary>
public sealed class GuardCallerVerifier : IGuardCallerVerifier
{
    private readonly bool _development;

    public GuardCallerVerifier(bool development) => _development = development;

    public GuardCaller? Verify(uint processId)
    {
        var imagePath = GuardProcess.ImagePath(processId);
        var sid = GuardProcess.UserSid(processId);
        if (imagePath is null || sid is null) return null;
        if (!string.Equals(
                Path.GetFileName(imagePath),
                OfficialInstallTopology.HostFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(imagePath);
        if (directory is null) return null;
        if (!_development && !IsOfficialMachineInstall(directory)) return null;

        var signer = AuthenticodeVerifier.Shared.SignerSubject(imagePath);
        if (signer is null && !_development && !UnsignedBuild.Allowed) return null;

        return new GuardCaller(processId, sid, imagePath);
    }

    internal static bool IsOfficialMachineInstall(string directory)
    {
        if (OfficialInstallTopology.Decide(directory).Kind != OfficialInstallDecisionKind.Accepted)
        {
            return false;
        }

        // Decide also accepts the per-user root. The production rule is narrower:
        // a Program Files machine root or its Velopack current payload.
        return OfficialInstallTopology.MachineAppPaths.Any(root =>
            Matches(directory, root) || Matches(directory, OfficialInstallTopology.PayloadPath(root)));
    }

    private static bool Matches(string directory, string expected)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected)),
            StringComparison.OrdinalIgnoreCase);
}
