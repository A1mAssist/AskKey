using System.Text.Json;
using System.Text.Json.Nodes;

namespace AskKey.Integrations;

public enum CodexDiscoveryHookConfigurationError
{
    UnsafeHooksFile,
    UnsafeBackupDirectory,
    InvalidHooksFile,
    FileTooLarge,
    MultipleExpectedHooks,
    CustomHookMismatch,
    ConcurrentModification,
    BackupFailed,
    WriteFailed,
    RollbackFailed,
    RestoreConflict,
}

public sealed class CodexDiscoveryHookConfigurationException : Exception
{
    public CodexDiscoveryHookConfigurationException(CodexDiscoveryHookConfigurationError error)
        : base(Describe(error)) => Error = error;

    public CodexDiscoveryHookConfigurationError Error { get; }

    private static string Describe(CodexDiscoveryHookConfigurationError error) => error switch
    {
        CodexDiscoveryHookConfigurationError.UnsafeHooksFile =>
            "The Codex hooks file is not a safe regular file.",
        CodexDiscoveryHookConfigurationError.UnsafeBackupDirectory =>
            "The Ask Key Codex hook backup directory is not safe.",
        CodexDiscoveryHookConfigurationError.InvalidHooksFile =>
            "The Codex hooks file is not valid JSON hook configuration.",
        CodexDiscoveryHookConfigurationError.FileTooLarge =>
            "The Codex hooks file is too large to inspect safely.",
        CodexDiscoveryHookConfigurationError.MultipleExpectedHooks =>
            "Multiple Ask Key discovery hooks were found.",
        CodexDiscoveryHookConfigurationError.CustomHookMismatch =>
            "An existing Ask Key discovery hook has been customized.",
        CodexDiscoveryHookConfigurationError.ConcurrentModification =>
            "The Codex hooks file changed after it was reviewed.",
        CodexDiscoveryHookConfigurationError.BackupFailed =>
            "The previous Codex hooks file could not be backed up safely.",
        CodexDiscoveryHookConfigurationError.WriteFailed =>
            "The Ask Key Codex discovery hook could not be written.",
        CodexDiscoveryHookConfigurationError.RollbackFailed =>
            "The previous Codex hooks file could not be restored safely.",
        _ => "The Codex hooks file changed before restoration.",
    };
}

public sealed class CodexDiscoveryHookPlan : IEquatable<CodexDiscoveryHookPlan>
{
    internal CodexDiscoveryHookPlan(
        byte[]? before, byte[]? after, bool changed, string summary, int? beforeMode, int afterMode)
    {
        Before = before;
        After = after;
        Changed = changed;
        Summary = summary;
        BeforeMode = beforeMode;
        AfterMode = afterMode;
    }

    public byte[]? Before { get; }
    public byte[]? After { get; }
    public bool Changed { get; }
    public string Summary { get; }
    internal int? BeforeMode { get; }
    internal int AfterMode { get; }

    public string RedactedDescription => Summary;

    public bool Equals(CodexDiscoveryHookPlan? other)
        => other is not null
            && Same(Before, other.Before)
            && Same(After, other.After)
            && Changed == other.Changed
            && Summary == other.Summary
            && BeforeMode == other.BeforeMode
            && AfterMode == other.AfterMode;

    public override bool Equals(object? obj) => Equals(obj as CodexDiscoveryHookPlan);

    public override int GetHashCode() => HashCode.Combine(Changed, Summary);

    private static bool Same(byte[]? left, byte[]? right)
        => left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);
}

/// <summary>
/// Manages Ask Key's Codex discovery hook in <c>~/.codex/hooks.json</c>. Codex
/// trust state and config.toml stay with their own integration boundary.
/// </summary>
public sealed class CodexDiscoveryHookConfiguration
{
    public const int MaximumHooksBytes = 1_048_576;
    internal const string ExpectedMatcher = "^(Bash|mcp__askkey__list_credentials)$";
    internal const string ExpectedServer = "askkey";
    internal const string ExpectedTool = "credential_discovery_guard";
    private static readonly string[] ExpectedEventNames = { "PreToolUse", "PostToolUse" };

    private readonly object _lock = new();

    public CodexDiscoveryHookConfiguration(string hooksPath, string backupDirectory, string helperPath)
    {
        HooksPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(hooksPath));
        BackupDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(backupDirectory));
        ExpectedCommand = $"\"{helperPath}\" hook codex";
    }

    public string HooksPath { get; }
    public string BackupDirectory { get; }
    public string ExpectedCommand { get; }

    internal JsonObject ExpectedHookGroup => new()
    {
        ["matcher"] = ExpectedMatcher,
        ["hooks"] = new JsonArray(new JsonObject
        {
            ["type"] = "command",
            ["command"] = ExpectedCommand,
            ["timeout"] = 3,
        }),
    };

    private static JsonObject LegacyHookGroup => new()
    {
        ["matcher"] = ExpectedMatcher,
        ["hooks"] = new JsonArray(new JsonObject
        {
            ["type"] = "mcp_tool",
            ["server"] = ExpectedServer,
            ["tool"] = ExpectedTool,
            ["input"] = new JsonObject
            {
                ["session_id"] = "${session_id}",
                ["turn_id"] = "${turn_id}",
                ["tool_name"] = "${tool_name}",
                ["tool_input"] = "${tool_input}",
            },
            ["timeout"] = 3,
        }),
    };

    internal bool IsOwnCommand(string? command)
        => command is not null
            && (command == ExpectedCommand
                || (command.Contains("askkey", StringComparison.Ordinal)
                    && command.Contains("hook codex", StringComparison.Ordinal)));
    internal sealed record HooksSnapshot(byte[] Bytes, int Mode);

    internal sealed record HookGroupMatch(string EventName, JsonObject Group, int Index, bool Legacy);

    public CodexDiscoveryHookPlan Preview()
    {
        lock (_lock)
        {
            var snapshot = ReadHooksSnapshot();
            var document = ParseDocument(snapshot?.Bytes ?? global::System.Array.Empty<byte>());
            var matches = MatchingHookGroups(document);
            ValidateOwnHook(matches);
            if (matches.Count == ExpectedEventNames.Length && matches.All(match => !match.Legacy))
            {
                return new CodexDiscoveryHookPlan(
                    snapshot?.Bytes, snapshot?.Bytes, false,
                    "Ask Key's Codex discovery hook is already installed.",
                    snapshot?.Mode, snapshot?.Mode ?? WindowsFileSecurity.PrivateMode);
            }
            var next = (JsonObject)document.DeepClone();
            AppendExpectedHook(next, matches);
            var after = SerializeDocument(next);
            return new CodexDiscoveryHookPlan(
                snapshot?.Bytes, after, true, "Install Ask Key's Codex command discovery hooks.",
                snapshot?.Mode, snapshot?.Mode ?? WindowsFileSecurity.PrivateMode);
        }
    }

    /// <summary>Applies exactly the reviewed plan. A plan for an already-applied
    /// state is a safe no-op, which keeps repeated installation idempotent.</summary>
    public void Apply(CodexDiscoveryHookPlan plan)
    {
        lock (_lock)
        {
            if (plan.After is not { } after)
            {
                throw new CodexDiscoveryHookConfigurationException(
                    CodexDiscoveryHookConfigurationError.WriteFailed);
            }
            var current = ReadHooksSnapshot();
            if (SnapshotMatches(current, after, plan.AfterMode)) return;
            if (!SnapshotMatches(current, plan.Before, plan.BeforeMode))
            {
                throw new CodexDiscoveryHookConfigurationException(
                    CodexDiscoveryHookConfigurationError.ConcurrentModification);
            }
            if (!plan.Changed) return;
            PrepareBackupDirectory();
            try
            {
                WriteBackup(plan.Before ?? global::System.Array.Empty<byte>());
            }
            catch (CodexDiscoveryHookConfigurationException)
            {
                throw;
            }
            catch
            {
                throw new CodexDiscoveryHookConfigurationException(
                    CodexDiscoveryHookConfigurationError.BackupFailed);
            }
            try
            {
                ReplaceFile(plan.Before, plan.BeforeMode, after, plan.AfterMode);
                var written = ReadHooksSnapshot();
                if (!SnapshotMatches(written, after, plan.AfterMode))
                {
                    throw new CodexDiscoveryHookConfigurationException(
                        CodexDiscoveryHookConfigurationError.WriteFailed);
                }
            }
            catch (CodexDiscoveryHookConfigurationException exception)
            {
                switch (exception.Error)
                {
                    case CodexDiscoveryHookConfigurationError.ConcurrentModification:
                    case CodexDiscoveryHookConfigurationError.UnsafeHooksFile:
                    case CodexDiscoveryHookConfigurationError.UnsafeBackupDirectory:
                    case CodexDiscoveryHookConfigurationError.InvalidHooksFile:
                    case CodexDiscoveryHookConfigurationError.FileTooLarge:
                    case CodexDiscoveryHookConfigurationError.BackupFailed:
                        throw;
                    default:
                        RollbackAfterFailedApply(plan, after);
                        throw;
                }
            }
            catch
            {
                RollbackAfterFailedApply(plan, after);
                throw new CodexDiscoveryHookConfigurationException(
                    CodexDiscoveryHookConfigurationError.WriteFailed);
            }
        }
    }

    /// <summary>Restores the reviewed `before` bytes only while the file still
    /// equals the reviewed `after` bytes and mode. A concurrent change is preserved.</summary>
    public void Restore(CodexDiscoveryHookPlan plan)
    {
        lock (_lock)
        {
            var current = ReadHooksSnapshot();
            if (!SnapshotMatches(current, plan.After, plan.AfterMode))
            {
                throw new CodexDiscoveryHookConfigurationException(
                    CodexDiscoveryHookConfigurationError.RestoreConflict);
            }
            if (!plan.Changed) return;
            try
            {
                ReplaceFile(plan.After, plan.AfterMode, plan.Before,
                    plan.BeforeMode ?? WindowsFileSecurity.PrivateMode);
            }
            catch (CodexDiscoveryHookConfigurationException exception)
                when (exception.Error == CodexDiscoveryHookConfigurationError.ConcurrentModification)
            {
                throw new CodexDiscoveryHookConfigurationException(
                    CodexDiscoveryHookConfigurationError.RestoreConflict);
            }
            catch (CodexDiscoveryHookConfigurationException)
            {
                throw;
            }
            catch
            {
                throw new CodexDiscoveryHookConfigurationException(
                    CodexDiscoveryHookConfigurationError.RollbackFailed);
            }
        }
    }

    public bool HasExpectedHook()
    {
        lock (_lock)
        {
            var snapshot = ReadHooksSnapshot();
            if (snapshot is null) return false;
            var document = ParseDocument(snapshot.Bytes);
            var matches = MatchingHookGroups(document);
            ValidateOwnHook(matches);
            return matches.Count == ExpectedEventNames.Length && matches.All(match => !match.Legacy);
        }
    }
    private HooksSnapshot? ReadHooksSnapshot()
    {
        WindowsPathSafety.Inspect(
            Path.GetDirectoryName(HooksPath)!, CommandDiscoveryHookConfigurationError.UnsafeHooksFile);
        try
        {
            var file = ClientConfigFileIO.ReadRegularFile(HooksPath, MaximumHooksBytes);
            if (file.Bytes.Contains((byte)0))
            {
                throw new CodexDiscoveryHookConfigurationException(
                    CodexDiscoveryHookConfigurationError.InvalidHooksFile);
            }
            return new HooksSnapshot(file.Bytes, file.Mode);
        }
        catch (ClientConfigFileException exception)
        {
            return exception.Failure switch
            {
                ClientConfigFileFailure.NotFound => null,
                ClientConfigFileFailure.TooLarge => throw new CodexDiscoveryHookConfigurationException(
                    CodexDiscoveryHookConfigurationError.FileTooLarge),
                _ => throw new CodexDiscoveryHookConfigurationException(
                    CodexDiscoveryHookConfigurationError.UnsafeHooksFile),
            };
        }
    }

    private HooksSnapshot? ReadSnapshotAt(string path)
    {
        try
        {
            var file = ClientConfigFileIO.ReadRegularFile(path, MaximumHooksBytes);
            return new HooksSnapshot(file.Bytes, file.Mode);
        }
        catch (ClientConfigFileException exception)
        {
            return exception.Failure switch
            {
                ClientConfigFileFailure.NotFound => null,
                ClientConfigFileFailure.TooLarge => throw new CodexDiscoveryHookConfigurationException(
                    CodexDiscoveryHookConfigurationError.FileTooLarge),
                _ => throw new CodexDiscoveryHookConfigurationException(
                    CodexDiscoveryHookConfigurationError.UnsafeHooksFile),
            };
        }
    }

    internal JsonObject ParseDocument(byte[] bytes)
    {
        if (bytes.Length == 0) return new JsonObject();
        try
        {
            return JsonNode.Parse(bytes) as JsonObject
                ?? throw new CodexDiscoveryHookConfigurationException(
                    CodexDiscoveryHookConfigurationError.InvalidHooksFile);
        }
        catch (JsonException)
        {
            throw new CodexDiscoveryHookConfigurationException(
                CodexDiscoveryHookConfigurationError.InvalidHooksFile);
        }
    }

    internal static byte[] SerializeDocument(JsonNode document)
        => CommandDiscoveryHookConfiguration.Serialize(
            document, CommandDiscoveryHookConfigurationError.InvalidHooksFile);

    internal List<HookGroupMatch> MatchingHookGroups(JsonObject document)
    {
        if (document["hooks"] is not { } rawHooks) return new List<HookGroupMatch>();
        if (rawHooks is not JsonObject hooks)
        {
            throw new CodexDiscoveryHookConfigurationException(
                CodexDiscoveryHookConfigurationError.InvalidHooksFile);
        }
        var matches = new List<HookGroupMatch>();
        foreach (var (eventName, rawGroups) in hooks)
        {
            if (rawGroups is not JsonArray groups)
            {
                throw new CodexDiscoveryHookConfigurationException(
                    CodexDiscoveryHookConfigurationError.InvalidHooksFile);
            }
            for (var index = 0; index < groups.Count; index++)
            {
                if (groups[index] is not JsonObject group || group["hooks"] is not JsonArray rawHooksInGroup)
                {
                    throw new CodexDiscoveryHookConfigurationException(
                        CodexDiscoveryHookConfigurationError.InvalidHooksFile);
                }
                foreach (var rawHook in rawHooksInGroup)
                {
                    if (rawHook is not JsonObject hook)
                    {
                        throw new CodexDiscoveryHookConfigurationException(
                            CodexDiscoveryHookConfigurationError.InvalidHooksFile);
                    }
                    var legacy = hook["server"]?.GetValue<string>() == ExpectedServer
                        && hook["tool"]?.GetValue<string>() == ExpectedTool;
                    if (!legacy && !IsOwnCommand(hook["command"]?.GetValue<string>())) continue;
                    matches.Add(new HookGroupMatch(eventName, group, index, legacy));
                }
            }
        }
        return matches;
    }

    private void ValidateOwnHook(List<HookGroupMatch> matches)
    {
        var legacy = matches.Where(match => match.Legacy).ToList();
        if ((legacy.Count > 0 && matches.Count > 1)
            || matches.Select(match => match.EventName).Distinct(StringComparer.Ordinal).Count() != matches.Count)
        {
            throw new CodexDiscoveryHookConfigurationException(
                CodexDiscoveryHookConfigurationError.MultipleExpectedHooks);
        }
        foreach (var match in matches)
        {
            var valid = match.Legacy
                ? match.EventName == "PreToolUse" && JsonNode.DeepEquals(match.Group, LegacyHookGroup)
                : ExpectedEventNames.Contains(match.EventName, StringComparer.Ordinal)
                    && JsonNode.DeepEquals(match.Group, ExpectedHookGroup);
            if (!valid)
            {
                throw new CodexDiscoveryHookConfigurationException(
                    CodexDiscoveryHookConfigurationError.CustomHookMismatch);
            }
        }
    }

    private void AppendExpectedHook(JsonObject document, List<HookGroupMatch> matches)
    {
        JsonObject hooks;
        if (document["hooks"] is { } rawHooks)
        {
            hooks = rawHooks as JsonObject
                ?? throw new CodexDiscoveryHookConfigurationException(
                    CodexDiscoveryHookConfigurationError.InvalidHooksFile);
        }
        else
        {
            hooks = new JsonObject();
        }
        foreach (var eventName in ExpectedEventNames)
        {
            var groups = hooks[eventName] as JsonArray ?? new JsonArray();
            if (matches.FirstOrDefault(match => match.EventName == eventName) is { } match)
            {
                if (match.Legacy) groups[match.Index] = ExpectedHookGroup.DeepClone();
            }
            else
            {
                groups.Add(ExpectedHookGroup.DeepClone());
            }
            hooks[eventName] = groups;
        }
        document["hooks"] = hooks;
    }

    private static bool SnapshotMatches(HooksSnapshot? snapshot, byte[]? bytes, int? mode)
    {
        if (bytes is null) return snapshot is null;
        return snapshot is not null && snapshot.Bytes.AsSpan().SequenceEqual(bytes) && snapshot.Mode == mode;
    }

    private void PrepareBackupDirectory()
    {
        WindowsPathSafety.EnsureDirectory(
            BackupDirectory, CommandDiscoveryHookConfigurationError.UnsafeBackupDirectory);
        WindowsFileSecurity.ApplyMode(BackupDirectory, WindowsFileSecurity.PrivateMode);
    }

    /// <summary>Keeps one owner backup per directory, replaced atomically. The
    /// backup directory is Ask Key managed, so the previous per-install GUID files
    /// are pruned here instead of accumulating forever.</summary>
    private void WriteBackup(byte[] bytes)
    {
        try
        {
            ClientConfigFileIO.PublishAtomically(
                bytes, Path.Combine(BackupDirectory, "codex-discovery.bak"),
                WindowsFileSecurity.PrivateMode, exclusive: false, ".askkey-discovery-backup-");
        }
        catch (ClientConfigFileException)
        {
            throw new CodexDiscoveryHookConfigurationException(
                CodexDiscoveryHookConfigurationError.BackupFailed);
        }
        PruneSupersededBackups();
    }

    private void PruneSupersededBackups()
    {
        try
        {
            foreach (var path in Directory.GetFiles(BackupDirectory, "codex-discovery-*.bak"))
            {
                if (string.Equals(Path.GetFileName(path), "codex-discovery.bak", StringComparison.Ordinal)) continue;
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Pruning is housekeeping; the owner backup is already in place.
        }
    }

    private void ReplaceFile(byte[]? expectedBytes, int? expectedMode, byte[]? replacement, int replacementMode)
    {
        var current = ReadHooksSnapshot();
        if (!SnapshotMatches(current, expectedBytes, expectedMode))
        {
            throw new CodexDiscoveryHookConfigurationException(
                CodexDiscoveryHookConfigurationError.ConcurrentModification);
        }
        if (current is null)
        {
            if (replacement is null) return;
            WindowsPathSafety.EnsureDirectory(
                Path.GetDirectoryName(HooksPath)!, CommandDiscoveryHookConfigurationError.UnsafeHooksFile);
            try
            {
                ClientConfigFileIO.PublishAtomically(
                    replacement, HooksPath, replacementMode, exclusive: true, ".askkey-discovery-");
            }
            catch (ClientConfigFileException exception)
                when (exception.Failure == ClientConfigFileFailure.ExclusiveExists)
            {
                throw new CodexDiscoveryHookConfigurationException(
                    CodexDiscoveryHookConfigurationError.ConcurrentModification);
            }
            catch (ClientConfigFileException)
            {
                throw new CodexDiscoveryHookConfigurationException(
                    CodexDiscoveryHookConfigurationError.WriteFailed);
            }
            return;
        }

        var quarantine = Path.Combine(
            Path.GetDirectoryName(HooksPath)!, $".askkey-discovery-{Guid.NewGuid()}");
        try
        {
            ClientConfigFileIO.RenameExclusively(HooksPath, quarantine);
        }
        catch (ClientConfigFileException)
        {
            throw new CodexDiscoveryHookConfigurationException(
                CodexDiscoveryHookConfigurationError.ConcurrentModification);
        }
        try
        {
            var moved = ReadSnapshotAt(quarantine);
            if (moved is null || !SnapshotMatches(moved, current.Bytes, current.Mode))
            {
                RestoreQuarantine(quarantine);
                throw new CodexDiscoveryHookConfigurationException(
                    CodexDiscoveryHookConfigurationError.ConcurrentModification);
            }
            if (replacement is not null)
            {
                try
                {
                    ClientConfigFileIO.PublishAtomically(
                        replacement, HooksPath, replacementMode, exclusive: true, ".askkey-discovery-");
                }
                catch (ClientConfigFileException)
                {
                    RestoreQuarantine(quarantine);
                    throw new CodexDiscoveryHookConfigurationException(
                        CodexDiscoveryHookConfigurationError.WriteFailed);
                }
            }
            File.Delete(quarantine);
        }
        catch (CodexDiscoveryHookConfigurationException)
        {
            throw;
        }
        catch
        {
            throw new CodexDiscoveryHookConfigurationException(
                CodexDiscoveryHookConfigurationError.RollbackFailed);
        }
    }

    private void RestoreQuarantine(string quarantine)
    {
        if (File.Exists(HooksPath) || Directory.Exists(HooksPath))
        {
            throw new CodexDiscoveryHookConfigurationException(
                CodexDiscoveryHookConfigurationError.RollbackFailed);
        }
        try
        {
            ClientConfigFileIO.RenameExclusively(quarantine, HooksPath);
        }
        catch (ClientConfigFileException)
        {
            throw new CodexDiscoveryHookConfigurationException(
                CodexDiscoveryHookConfigurationError.RollbackFailed);
        }
    }

    private void RollbackAfterFailedApply(CodexDiscoveryHookPlan plan, byte[] replacement)
    {
        var current = ReadHooksSnapshot();
        if (SnapshotMatches(current, plan.Before, plan.BeforeMode)) return;
        if (!SnapshotMatches(current, replacement, plan.AfterMode))
        {
            throw new CodexDiscoveryHookConfigurationException(
                CodexDiscoveryHookConfigurationError.RollbackFailed);
        }
        try
        {
            ReplaceFile(replacement, plan.AfterMode, plan.Before,
                plan.BeforeMode ?? WindowsFileSecurity.PrivateMode);
        }
        catch
        {
            throw new CodexDiscoveryHookConfigurationException(
                CodexDiscoveryHookConfigurationError.RollbackFailed);
        }
    }
}