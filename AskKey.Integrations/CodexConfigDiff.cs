namespace AskKey.Integrations;

public enum CodexConnectionStatus
{
    Connected,
    NotConnected,
}

public sealed class CodexConfigDiff : IEquatable<CodexConfigDiff>
{
    public CodexConfigDiff(string before, string after)
    {
        Before = before;
        After = after;
    }

    public string Before { get; }
    public string After { get; }

    public string RedactedDescription => $"--- before\n{CodexTomlRedaction.Redact(Before)}\n+++ after\n{CodexTomlRedaction.Redact(After)}";

    public bool Equals(CodexConfigDiff? other)
        => other is not null && Before == other.Before && After == other.After;

    public override bool Equals(object? obj) => Equals(obj as CodexConfigDiff);

    public override int GetHashCode() => HashCode.Combine(Before, After);
}

public readonly record struct CodexApplyResult(CodexConnectionStatus Status, CodexConfigDiff Diff);

/// <summary>Test seams for the Codex apply transaction; empty in production.</summary>
public sealed class CodexApplyLifecycle
{
    public Action AfterBackup { get; set; } = () => { };
    public Action AfterWrite { get; set; } = () => { };
    public Action BeforeRestore { get; set; } = () => { };
}

public enum CodexMCPCLIStatusKind
{
    Missing,
    Supported,
    Unknown,
}

public readonly record struct CodexMCPCLIStatus(CodexMCPCLIStatusKind Kind, string? Version = null)
{
    public static CodexMCPCLIStatus Missing => new(CodexMCPCLIStatusKind.Missing);
    public static CodexMCPCLIStatus Supported(string version) => new(CodexMCPCLIStatusKind.Supported, version);
    public static CodexMCPCLIStatus Unknown(string? version) => new(CodexMCPCLIStatusKind.Unknown, version);
}

/// <summary>Optional official CLI bridge. <see cref="Missing"/> never runs a process.</summary>
public sealed class CodexMCPCommand
{
    public CodexMCPCommand(Func<CodexMCPCLIStatus> status, Action<string, string> addAskKey)
    {
        Status = status;
        AddAskKey = addAskKey;
    }

    public Func<CodexMCPCLIStatus> Status { get; }

    /// <summary>(helperPath, isolatedConfigPath)</summary>
    public Action<string, string> AddAskKey { get; }

    public static CodexMCPCommand Missing { get; } = new(() => CodexMCPCLIStatus.Missing, (_, _) => { });
}

public static class CodexUserMCP
{
    public const string ServerName = "askkey";

    public static string BundledHelperPath => Broker.Runtime.OfficialInstallTopology.CanonicalHelperPath;

    public static string UserConfigPath(string home) => Path.Combine(home, ".codex", "config.toml");

    public static string ManagedBackupDirectory(string applicationSupport)
        => Path.Combine(applicationSupport, "client-backups", "codex");

    /// <summary>Only minor lines whose stable `mcp add/get --json` contract was verified.</summary>
    public static bool AllowsOfficialCLI(string version)
    {
        var components = version.Split('.');
        if (components.Length != 3 || components.Any(c => c.Length == 0 || !c.All(char.IsAsciiDigit)))
        {
            return false;
        }
        if (!int.TryParse(components[0], out var major)
            || !int.TryParse(components[1], out var minor)
            || !int.TryParse(components[2], out _))
        {
            return false;
        }
        return major == 0 && ((minor is >= 42 and <= 50) || minor is 151 or 153 or 154 or 156);
    }
}
