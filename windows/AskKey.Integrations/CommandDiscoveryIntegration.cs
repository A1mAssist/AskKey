namespace AskKey.Integrations;

public enum CommandDiscoveryHookFormat
{
    CursorMerged,
    GrokOwned,
    ClaudeMerged,
}

public static class CommandDiscoveryHookFormatExtensions
{
    public static string DisplayName(this CommandDiscoveryHookFormat format) => format switch
    {
        CommandDiscoveryHookFormat.CursorMerged => "Cursor",
        CommandDiscoveryHookFormat.GrokOwned => "Grok",
        _ => "Claude Code",
    };

    public static string InvocationMarker(this CommandDiscoveryHookFormat format) => format switch
    {
        CommandDiscoveryHookFormat.CursorMerged => "hook cursor",
        CommandDiscoveryHookFormat.GrokOwned => "hook grok",
        _ => "hook claude",
    };
}

public sealed class CommandDiscoveryHookPlan : IEquatable<CommandDiscoveryHookPlan>
{
    public CommandDiscoveryHookPlan(
        byte[]? before,
        byte[]? after,
        bool changed,
        string summary,
        CommandDiscoveryHookFormat format,
        int? beforeMode,
        int afterMode)
    {
        Before = before;
        After = after;
        Changed = changed;
        Summary = summary;
        Format = format;
        BeforeMode = beforeMode;
        AfterMode = afterMode;
    }

    public byte[]? Before { get; }
    public byte[]? After { get; }
    public bool Changed { get; }
    public string Summary { get; }
    public CommandDiscoveryHookFormat Format { get; }
    internal int? BeforeMode { get; }
    internal int AfterMode { get; }

    public string RedactedDescription => Summary;

    public bool Equals(CommandDiscoveryHookPlan? other)
        => other is not null
            && SameBytes(Before, other.Before)
            && SameBytes(After, other.After)
            && Changed == other.Changed
            && Summary == other.Summary
            && Format == other.Format
            && BeforeMode == other.BeforeMode
            && AfterMode == other.AfterMode;

    public override bool Equals(object? obj) => Equals(obj as CommandDiscoveryHookPlan);

    public override int GetHashCode() => HashCode.Combine(Changed, Summary, Format);

    internal static bool SameBytes(byte[]? left, byte[]? right)
        => left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);
}

public enum CommandDiscoveryHookConfigurationError
{
    UnsafeHooksFile,
    UnsafeBackupDirectory,
    InvalidHooksFile,
    InvalidExpectedHooks,
    FileTooLarge,
    MultipleExpectedHooks,
    CustomHookMismatch,
    UntrustedHelper,
    OwnedFileConflict,
    ConcurrentModification,
    BackupFailed,
    WriteFailed,
    RollbackFailed,
}

public sealed class CommandDiscoveryHookConfigurationException : Exception
{
    public CommandDiscoveryHookConfigurationException(CommandDiscoveryHookConfigurationError error)
        : base(Describe(error)) => Error = error;

    public CommandDiscoveryHookConfigurationError Error { get; }

    private static string Describe(CommandDiscoveryHookConfigurationError error) => error switch
    {
        CommandDiscoveryHookConfigurationError.UnsafeHooksFile => "The command-hook file is not safe.",
        CommandDiscoveryHookConfigurationError.UnsafeBackupDirectory => "The command-hook backup directory is not safe.",
        CommandDiscoveryHookConfigurationError.InvalidHooksFile => "The command-hook file is invalid.",
        CommandDiscoveryHookConfigurationError.InvalidExpectedHooks => "The reviewed command-hook definition is invalid.",
        CommandDiscoveryHookConfigurationError.FileTooLarge => "The command-hook file is too large.",
        CommandDiscoveryHookConfigurationError.MultipleExpectedHooks => "Multiple Ask Key command hooks were found.",
        CommandDiscoveryHookConfigurationError.CustomHookMismatch => "An Ask Key command hook has been customized.",
        CommandDiscoveryHookConfigurationError.UntrustedHelper => "The Ask Key helper is not trusted, so Ask Key cannot verify it.",
        CommandDiscoveryHookConfigurationError.OwnedFileConflict => "The Grok command-hook file contains unknown settings.",
        CommandDiscoveryHookConfigurationError.ConcurrentModification => "The command-hook file changed after review.",
        CommandDiscoveryHookConfigurationError.BackupFailed => "The command-hook backup failed.",
        CommandDiscoveryHookConfigurationError.WriteFailed => "The command hook could not be written.",
        _ => "The command-hook rollback failed.",
    };
}

public enum CommandDiscoveryClient
{
    Cursor,
    Grok,
    Claude,
}

public static class CommandDiscoveryIntegration
{
    public static string HooksPath(this CommandDiscoveryClient client, string home) => client switch
    {
        CommandDiscoveryClient.Cursor => Path.Combine(home, ".cursor", "hooks.json"),
        CommandDiscoveryClient.Grok => Path.Combine(home, ".grok", "hooks", "askkey-discovery.json"),
        _ => Path.Combine(home, ".claude", "settings.json"),
    };

    public static CommandDiscoveryHookFormat Format(this CommandDiscoveryClient client) => client switch
    {
        CommandDiscoveryClient.Cursor => CommandDiscoveryHookFormat.CursorMerged,
        CommandDiscoveryClient.Grok => CommandDiscoveryHookFormat.GrokOwned,
        _ => CommandDiscoveryHookFormat.ClaudeMerged,
    };

    private static string RawValue(this CommandDiscoveryClient client) => client switch
    {
        CommandDiscoveryClient.Cursor => "cursor",
        CommandDiscoveryClient.Grok => "grok",
        _ => "claude",
    };

    /// <summary>Definitions contain only a quoted helper path and a client selector.
    /// Event input stays on stdin; it never becomes shell text.</summary>
    public static byte[] Definition(this CommandDiscoveryClient client, string helper)
    {
        // Windows has no POSIX shell; quote for the command line the client parses.
        var command = "\"" + helper.Replace("\"", "\\\"") + "\" hook " + client.RawValue();
        object root;
        switch (client)
        {
            case CommandDiscoveryClient.Cursor:
                var cursorHandler = new Dictionary<string, object>
                {
                    ["command"] = command,
                    ["timeout"] = 3,
                    ["matcher"] = "Shell|MCP:.*",
                };
                root = new Dictionary<string, object>
                {
                    ["version"] = 1,
                    ["hooks"] = new Dictionary<string, object>
                    {
                        ["preToolUse"] = new[] { cursorHandler },
                        ["postToolUse"] = new[] { cursorHandler },
                        ["postToolUseFailure"] = new[] { cursorHandler },
                    },
                };
                break;
            case CommandDiscoveryClient.Grok:
                var grokHandler = new Dictionary<string, object>
                {
                    ["type"] = "command",
                    ["command"] = command,
                    ["timeout"] = 3,
                };
                var grokTool = new Dictionary<string, object>
                {
                    ["matcher"] = "^(run_terminal_command|askkey__list_credentials)$",
                    ["hooks"] = new[] { grokHandler },
                };
                root = new Dictionary<string, object>
                {
                    ["hooks"] = new Dictionary<string, object>
                    {
                        ["UserPromptSubmit"] = new[] { new Dictionary<string, object> { ["hooks"] = new[] { grokHandler } } },
                        ["PreToolUse"] = new[] { grokTool },
                        ["PostToolUse"] = new[] { grokTool },
                        ["PostToolUseFailure"] = new[] { grokTool },
                    },
                };
                break;
            default:
                var claudeHandler = new Dictionary<string, object>
                {
                    ["type"] = "command",
                    ["command"] = command,
                    ["timeout"] = 3,
                };
                var claudeGroup = new Dictionary<string, object>
                {
                    ["matcher"] = "Bash|mcp__askkey__list_credentials",
                    ["hooks"] = new[] { claudeHandler },
                };
                root = new Dictionary<string, object>
                {
                    ["hooks"] = new Dictionary<string, object>
                    {
                        ["UserPromptSubmit"] = new[] { new Dictionary<string, object> { ["hooks"] = new[] { claudeHandler } } },
                        ["PreToolUse"] = new[] { claudeGroup },
                        ["PostToolUse"] = new[] { claudeGroup },
                        ["PostToolUseFailure"] = new[] { claudeGroup },
                    },
                };
                break;
        }
        return CommandHookJson.Serialize(root);
    }

    public static CommandDiscoveryHookConfiguration Configuration(
        this CommandDiscoveryClient client, string home, string helper, string backupDirectory)
        => new(client.HooksPath(home), backupDirectory, client.Definition(helper), client.Format());

    /// <summary>Probes the installed helper's real command surface, so an old helper
    /// cannot leave a new rule pointing at an unsupported command. Trust is settled
    /// before the helper ever runs, exactly like the Codex path.</summary>
    public static void VerifyHelper(
        this CommandDiscoveryClient client, string helper, string workingDirectory, Func<string, bool>? isTrusted = null)
    {
        if (!HelperTrust.IsExecutableRegularFile(helper)
            || !(isTrusted ?? CodexHelperSigning.Executable.IsTrusted)(helper))
        {
            throw new CommandDiscoveryHookConfigurationException(
                CommandDiscoveryHookConfigurationError.UntrustedHelper);
        }
        RunResult result;
        try
        {
            result = IntegrationProcess.RunCaptured(
                helper, new[] { "hook", "capabilities" }, IntegrationProcess.PortableEnvironment(),
                workingDirectory, input: null, TimeSpan.FromSeconds(3), TimeSpan.Zero, 8192);
        }
        catch (global::AskKey.System.RestrictedProcessException)
        {
            throw new CommandDiscoveryHookConfigurationException(
                CommandDiscoveryHookConfigurationError.InvalidExpectedHooks);
        }
        if (result.TimedOut || result.Status != 0)
        {
            throw new CommandDiscoveryHookConfigurationException(
                CommandDiscoveryHookConfigurationError.InvalidExpectedHooks);
        }
        try
        {
            using var document = global::System.Text.Json.JsonDocument.Parse(result.Stdout);
            var root = document.RootElement;
            var ok = root.ValueKind == global::System.Text.Json.JsonValueKind.Object
                && root.TryGetProperty("protocolVersion", out var protocol)
                && protocol.ValueKind == global::System.Text.Json.JsonValueKind.Number
                && protocol.TryGetInt32(out var protocolVersion) && protocolVersion == 1
                && root.TryGetProperty("clients", out var clients)
                && clients.ValueKind == global::System.Text.Json.JsonValueKind.Array
                && clients.EnumerateArray().Any(item =>
                    item.ValueKind == global::System.Text.Json.JsonValueKind.String
                    && item.GetString() == client.RawValue());
            if (!ok)
            {
                throw new CommandDiscoveryHookConfigurationException(
                    CommandDiscoveryHookConfigurationError.InvalidExpectedHooks);
            }
        }
        catch (global::System.Text.Json.JsonException)
        {
            throw new CommandDiscoveryHookConfigurationException(
                CommandDiscoveryHookConfigurationError.InvalidExpectedHooks);
        }
    }
}
