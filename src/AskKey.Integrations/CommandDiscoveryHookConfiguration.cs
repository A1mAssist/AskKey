using System.Text.Json;
using System.Text.Json.Nodes;

namespace AskKey.Integrations;

/// <summary>
/// Transactional command-hook configuration for Cursor, Grok and Claude Code.
/// The integration supplies the expected hooks, so this type does not guess a
/// client matcher or event.
/// </summary>
public sealed class CommandDiscoveryHookConfiguration
{
    public const int MaximumHooksBytes = 1_048_576;

    private readonly object _lock = new();

    public CommandDiscoveryHookConfiguration(
        string hooksPath,
        string backupDirectory,
        byte[] expectedHooks,
        CommandDiscoveryHookFormat format)
    {
        HooksPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(hooksPath));
        BackupDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(backupDirectory));
        ExpectedHooks = expectedHooks;
        Format = format;
    }

    public string HooksPath { get; }
    public string BackupDirectory { get; }
    public byte[] ExpectedHooks { get; }
    public CommandDiscoveryHookFormat Format { get; }

    internal sealed class Snapshot : IEquatable<Snapshot>
    {
        public Snapshot(byte[] bytes, int mode)
        {
            Bytes = bytes;
            Mode = mode;
        }

        public byte[] Bytes { get; }
        public int Mode { get; }

        public bool Equals(Snapshot? other)
            => other is not null
                && Mode == other.Mode
                && Bytes.AsSpan().SequenceEqual(other.Bytes);

        public override bool Equals(object? obj) => Equals(obj as Snapshot);

        public override int GetHashCode() => HashCode.Combine(Mode, Bytes.Length);
    }

    internal sealed record Definition(
        JsonObject Root,
        Dictionary<string, JsonArray> Groups,
        string[] Commands,
        byte[] Canonical);

    internal sealed record Match(string Event, bool Exact, bool OwnLike);

    internal sealed record ClaudeMatch(string Event, int Group, int Handler);

    public CommandDiscoveryHookPlan Preview()
    {
        lock (_lock)
        {
            var definition = MakeDefinition();
            return MakePlan(ReadSnapshot(HooksPath, checkParent: true), definition);
        }
    }

    public void Apply(CommandDiscoveryHookPlan plan)
    {
        lock (_lock) ApplyLocked(plan, removingClaude: false);
    }

    public bool HasExpectedHook()
    {
        lock (_lock)
        {
            var definition = MakeDefinition();
            var snapshot = ReadSnapshot(HooksPath, checkParent: true);
            return snapshot is not null && HasExpectedHook(snapshot.Bytes, definition);
        }
    }

    /// <summary>Restores the reviewed before-state only while the file still equals
    /// the reviewed after-state. A concurrent change is preserved.</summary>
    public void Restore(CommandDiscoveryHookPlan plan)
    {
        lock (_lock)
        {
            if (plan.Format != Format)
            {
                throw Error(CommandDiscoveryHookConfigurationError.ConcurrentModification);
            }
            var current = ReadSnapshot(HooksPath, checkParent: true);
            if (Matches(current, plan.Before, plan.BeforeMode)) return;
            if (!Matches(current, plan.After, plan.AfterMode) || !plan.Changed)
            {
                throw Error(CommandDiscoveryHookConfigurationError.ConcurrentModification);
            }
            try
            {
                Replace(plan.After, plan.AfterMode, plan.Before,
                    plan.BeforeMode ?? WindowsFileSecurity.PrivateMode);
            }
            catch (CommandDiscoveryHookConfigurationException exception)
                when (exception.Error == CommandDiscoveryHookConfigurationError.ConcurrentModification)
            {
                throw new CommandDiscoveryHookConfigurationException(
                    CommandDiscoveryHookConfigurationError.ConcurrentModification);
            }
            catch (CommandDiscoveryHookConfigurationException)
            {
                throw;
            }
            catch
            {
                throw Error(CommandDiscoveryHookConfigurationError.RollbackFailed);
            }
        }
    }

    /// <summary>Remove only exact owned handlers, including those beside other tools
    /// inside a shared matcher group. Customized handlers stop removal.</summary>
    public void RemoveClaudeHooks()
    {
        lock (_lock)
        {
            if (Format != CommandDiscoveryHookFormat.ClaudeMerged)
            {
                throw Error(CommandDiscoveryHookConfigurationError.InvalidExpectedHooks);
            }
            var definition = MakeDefinition();
            var snapshot = ReadSnapshot(HooksPath, checkParent: true);
            ApplyLocked(ClaudeRemovalPlan(snapshot, definition), removingClaude: true);
        }
    }

    private void ApplyLocked(CommandDiscoveryHookPlan plan, bool removingClaude)
    {
        if (plan.Format != Format) throw Error(CommandDiscoveryHookConfigurationError.ConcurrentModification);
        var definition = MakeDefinition();
        var current = ReadSnapshot(HooksPath, checkParent: true);
        if (Matches(current, plan.After, plan.AfterMode)) return;
        if (!plan.Changed || !Matches(current, plan.Before, plan.BeforeMode))
        {
            throw Error(CommandDiscoveryHookConfigurationError.ConcurrentModification);
        }
        var reviewed = removingClaude
            ? ClaudeRemovalPlan(current, definition)
            : MakePlan(current, definition);
        if (!reviewed.Equals(plan) || plan.After is null)
        {
            throw Error(CommandDiscoveryHookConfigurationError.ConcurrentModification);
        }
        var replacement = plan.After;

        PrepareBackupDirectory();
        WriteBackup(plan.Before ?? []);

        try
        {
            Replace(plan.Before, plan.BeforeMode, replacement, plan.AfterMode);
            var written = ReadSnapshot(HooksPath, checkParent: true);
            if (!Matches(written, replacement, plan.AfterMode))
            {
                throw Error(CommandDiscoveryHookConfigurationError.WriteFailed);
            }
        }
        catch (CommandDiscoveryHookConfigurationException exception)
        {
            switch (exception.Error)
            {
                case CommandDiscoveryHookConfigurationError.ConcurrentModification:
                case CommandDiscoveryHookConfigurationError.UnsafeHooksFile:
                case CommandDiscoveryHookConfigurationError.UnsafeBackupDirectory:
                case CommandDiscoveryHookConfigurationError.InvalidHooksFile:
                case CommandDiscoveryHookConfigurationError.InvalidExpectedHooks:
                case CommandDiscoveryHookConfigurationError.FileTooLarge:
                case CommandDiscoveryHookConfigurationError.BackupFailed:
                case CommandDiscoveryHookConfigurationError.OwnedFileConflict:
                case CommandDiscoveryHookConfigurationError.CustomHookMismatch:
                case CommandDiscoveryHookConfigurationError.MultipleExpectedHooks:
                    throw;
                default:
                    Rollback(plan, replacement);
                    throw;
            }
        }
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
    private void WriteBackup(byte[] data)
    {
        // Recheck immediately before resolving the destination so a directory
        // replacement after preview cannot redirect the backup.
        WindowsPathSafety.EnsureDirectory(
            BackupDirectory, CommandDiscoveryHookConfigurationError.UnsafeBackupDirectory);
        try
        {
            ClientConfigFileIO.PublishAtomically(
                data, Path.Combine(BackupDirectory, "command-discovery.bak"),
                WindowsFileSecurity.PrivateMode, exclusive: false, ".askkey-command-backup-");
        }
        catch (ClientConfigFileException)
        {
            throw Error(CommandDiscoveryHookConfigurationError.BackupFailed);
        }
        PruneSupersededBackups();
    }

    private void PruneSupersededBackups()
    {
        try
        {
            foreach (var path in Directory.GetFiles(BackupDirectory, "command-discovery-*.bak"))
            {
                if (string.Equals(Path.GetFileName(path), "command-discovery.bak", StringComparison.Ordinal)) continue;
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Pruning is housekeeping; the owner backup is already in place.
        }
    }

    private void Replace(byte[]? expectedBytes, int? expectedMode, byte[]? replacement, int replacementMode)
    {
        var current = ReadSnapshot(HooksPath, checkParent: true);
        if (!Matches(current, expectedBytes, expectedMode))
        {
            throw Error(CommandDiscoveryHookConfigurationError.ConcurrentModification);
        }
        if (current is null)
        {
            if (replacement is null) return;
            WindowsPathSafety.EnsureDirectory(
                Path.GetDirectoryName(HooksPath)!, CommandDiscoveryHookConfigurationError.UnsafeHooksFile);
            try
            {
                ClientConfigFileIO.PublishAtomically(
                    replacement, HooksPath, replacementMode, exclusive: true, ".askkey-command-");
            }
            catch (ClientConfigFileException exception)
                when (exception.Failure == ClientConfigFileFailure.ExclusiveExists)
            {
                throw Error(CommandDiscoveryHookConfigurationError.ConcurrentModification);
            }
            catch (ClientConfigFileException)
            {
                throw Error(CommandDiscoveryHookConfigurationError.WriteFailed);
            }
            return;
        }

        if (Format == CommandDiscoveryHookFormat.ClaudeMerged && replacement is not null)
        {
            ReplaceClaude(current, replacement, replacementMode);
            return;
        }

        var quarantine = Path.Combine(
            Path.GetDirectoryName(HooksPath)!, $".askkey-command-{Guid.NewGuid()}");
        try
        {
            ClientConfigFileIO.RenameExclusively(HooksPath, quarantine);
        }
        catch (ClientConfigFileException)
        {
            throw Error(CommandDiscoveryHookConfigurationError.ConcurrentModification);
        }
        try
        {
            var moved = ReadSnapshot(quarantine, checkParent: false);
            if (moved is null || !moved.Equals(current))
            {
                Restore(quarantine);
                throw Error(CommandDiscoveryHookConfigurationError.ConcurrentModification);
            }
            if (replacement is not null)
            {
                try
                {
                    ClientConfigFileIO.PublishAtomically(
                        replacement, HooksPath, replacementMode, exclusive: true, ".askkey-command-");
                }
                catch (ClientConfigFileException)
                {
                    Restore(quarantine);
                    throw Error(CommandDiscoveryHookConfigurationError.WriteFailed);
                }
            }
            File.Delete(quarantine);
        }
        catch (CommandDiscoveryHookConfigurationException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Error(CommandDiscoveryHookConfigurationError.RollbackFailed);
        }
    }

    private void Restore(string quarantine)
    {
        if (File.Exists(HooksPath) || Directory.Exists(HooksPath))
        {
            throw Error(CommandDiscoveryHookConfigurationError.RollbackFailed);
        }
        try
        {
            ClientConfigFileIO.RenameExclusively(quarantine, HooksPath);
        }
        catch (ClientConfigFileException)
        {
            throw Error(CommandDiscoveryHookConfigurationError.RollbackFailed);
        }
    }

    private void Rollback(CommandDiscoveryHookPlan plan, byte[] replacement)
    {
        var current = ReadSnapshot(HooksPath, checkParent: true);
        if (Matches(current, plan.Before, plan.BeforeMode)) return;
        if (!Matches(current, replacement, plan.AfterMode))
        {
            throw Error(CommandDiscoveryHookConfigurationError.RollbackFailed);
        }
        try
        {
            Replace(replacement, plan.AfterMode, plan.Before, plan.BeforeMode ?? WindowsFileSecurity.PrivateMode);
        }
        catch (CommandDiscoveryHookConfigurationException)
        {
            throw Error(CommandDiscoveryHookConfigurationError.RollbackFailed);
        }
    }

    private static bool Matches(Snapshot? snapshot, byte[]? bytes, int? mode)
    {
        if (bytes is null) return snapshot is null;
        return snapshot is not null
            && snapshot.Bytes.AsSpan().SequenceEqual(bytes)
            && snapshot.Mode == mode;
    }

    internal Snapshot? ReadSnapshot(string path, bool checkParent)
    {
        if (checkParent)
        {
            WindowsPathSafety.Inspect(
                Path.GetDirectoryName(path)!, CommandDiscoveryHookConfigurationError.UnsafeHooksFile);
        }
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            if (WindowsFileSecurity.IsLinkOrReparse(path))
            {
                throw Error(CommandDiscoveryHookConfigurationError.UnsafeHooksFile);
            }
            return null;
        }
        if (WindowsFileSecurity.IsLinkOrReparse(path) || Directory.Exists(path))
        {
            throw Error(CommandDiscoveryHookConfigurationError.UnsafeHooksFile);
        }
        if (!WindowsPathSafety.OwnedByCurrentUser(path))
        {
            throw Error(CommandDiscoveryHookConfigurationError.UnsafeHooksFile);
        }
        try
        {
            var file = ClientConfigFileIO.ReadRegularFile(path, MaximumHooksBytes);
            return new Snapshot(file.Bytes, file.Mode);
        }
        catch (ClientConfigFileException exception)
        {
            return exception.Failure switch
            {
                ClientConfigFileFailure.TooLarge =>
                    throw Error(CommandDiscoveryHookConfigurationError.FileTooLarge),
                ClientConfigFileFailure.NotFound => null,
                _ => throw Error(CommandDiscoveryHookConfigurationError.UnsafeHooksFile),
            };
        }
    }

    internal Definition MakeDefinition()
    {
        if (ExpectedHooks.Length > MaximumHooksBytes || ExpectedHooks.Contains((byte)0))
        {
            throw Error(CommandDiscoveryHookConfigurationError.InvalidExpectedHooks);
        }
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(ExpectedHooks); }
        catch (JsonException) { throw Error(CommandDiscoveryHookConfigurationError.InvalidExpectedHooks); }
        if (parsed is not JsonObject root)
        {
            throw Error(CommandDiscoveryHookConfigurationError.InvalidExpectedHooks);
        }
        return Format switch
        {
            CommandDiscoveryHookFormat.ClaudeMerged => ClaudeDefinition(root),
            CommandDiscoveryHookFormat.GrokOwned => GrokDefinition(root),
            _ => CursorDefinition(root),
        };
    }

    private Definition ClaudeDefinition(JsonObject root)
    {
        var expectedEvents = new[]
        {
            "UserPromptSubmit", "PreToolUse", "PostToolUse", "PostToolUseFailure",
        };
        if (root["hooks"] is not JsonObject hooks
            || !hooks.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal)
                .SetEquals(expectedEvents))
        {
            throw Error(CommandDiscoveryHookConfigurationError.InvalidExpectedHooks);
        }
        var groups = new Dictionary<string, JsonArray>(StringComparer.Ordinal);
        var commands = new List<string>();
        foreach (var (eventName, value) in hooks)
        {
            if (value is not JsonArray entries || entries.Count != 1
                || entries[0] is not JsonObject entry
                || entry["hooks"] is not JsonArray handlers || handlers.Count != 1
                || handlers[0] is not JsonObject handler
                || handler["type"]?.GetValue<string>() != "command"
                || handler["command"]?.GetValue<string>() is not { Length: > 0 } command
                || (eventName == "UserPromptSubmit"
                    ? entry.ContainsKey("matcher")
                    : entry["matcher"]?.GetValue<string>() != "Bash|mcp__askkey__list_credentials"))
            {
                throw Error(CommandDiscoveryHookConfigurationError.InvalidExpectedHooks);
            }
            groups[eventName] = (JsonArray)entries.DeepClone();
            commands.Add(command);
        }
        if (commands.Distinct(StringComparer.Ordinal).Count() != 1)
        {
            throw Error(CommandDiscoveryHookConfigurationError.InvalidExpectedHooks);
        }
        return new Definition(root, groups, commands.ToArray(),
            Serialize(root, CommandDiscoveryHookConfigurationError.InvalidExpectedHooks));
    }

    private Definition GrokDefinition(JsonObject root)
    {
        var commands = AllCommands(root);
        if (commands.Count == 0)
        {
            throw Error(CommandDiscoveryHookConfigurationError.InvalidExpectedHooks);
        }
        return new Definition(root, new Dictionary<string, JsonArray>(StringComparer.Ordinal),
            commands.ToArray(), Serialize(root, CommandDiscoveryHookConfigurationError.InvalidExpectedHooks));
    }

    private Definition CursorDefinition(JsonObject root)
    {
        if (root["version"]?.GetValue<int>() != 1
            || root["hooks"] is not JsonObject rawHooks || rawHooks.Count == 0)
        {
            throw Error(CommandDiscoveryHookConfigurationError.InvalidExpectedHooks);
        }
        var groups = new Dictionary<string, JsonArray>(StringComparer.Ordinal);
        var commands = new List<string>();
        foreach (var eventName in rawHooks.Select(pair => pair.Key).Order(StringComparer.Ordinal))
        {
            if (rawHooks[eventName] is not JsonArray rawGroups || rawGroups.Count == 0)
            {
                throw Error(CommandDiscoveryHookConfigurationError.InvalidExpectedHooks);
            }
            var eventGroups = new List<JsonNode?>();
            foreach (var rawGroup in rawGroups)
            {
                if (rawGroup is not JsonObject group || CommandStrings(group).Count == 0)
                {
                    throw Error(CommandDiscoveryHookConfigurationError.InvalidExpectedHooks);
                }
                eventGroups.Add(group.DeepClone());
                commands.AddRange(CommandStrings(group));
            }
            groups[eventName] = new JsonArray(eventGroups
                .Select(node => node!.DeepClone())
                .ToArray());
        }
        return new Definition(root, groups, commands.ToArray(),
            Serialize(root, CommandDiscoveryHookConfigurationError.InvalidExpectedHooks));
    }

    private CommandDiscoveryHookPlan MakePlan(Snapshot? snapshot, Definition definition) => Format switch
    {
        CommandDiscoveryHookFormat.ClaudeMerged => ClaudePlan(snapshot, definition),
        CommandDiscoveryHookFormat.GrokOwned => GrokPlan(snapshot, definition),
        _ => CursorPlan(snapshot, definition),
    };

    private CommandDiscoveryHookPlan GrokPlan(Snapshot? snapshot, Definition definition)
    {
        if (snapshot is not null)
        {
            var existing = ParseObject(snapshot.Bytes, CommandDiscoveryHookConfigurationError.InvalidHooksFile);
            if (JsonEqual(existing, definition.Root)) return NoChange(snapshot);
            if (ContainsOwnLike(existing, definition.Commands))
            {
                throw Error(CommandDiscoveryHookConfigurationError.CustomHookMismatch);
            }
            throw Error(CommandDiscoveryHookConfigurationError.OwnedFileConflict);
        }
        return new CommandDiscoveryHookPlan(
            null, definition.Canonical, true, "Add Ask Key's Grok discovery command hook.",
            Format, null, WindowsFileSecurity.PrivateMode);
    }

    private CommandDiscoveryHookPlan CursorPlan(Snapshot? snapshot, Definition definition)
    {
        if (snapshot is null)
        {
            return new CommandDiscoveryHookPlan(
                null, definition.Canonical, true, "Add Ask Key's Cursor discovery command hooks.",
                Format, null, WindowsFileSecurity.PrivateMode);
        }
        var existing = ParseObject(snapshot.Bytes, CommandDiscoveryHookConfigurationError.InvalidHooksFile);
        var found = CursorMatches(existing, definition);
        ValidateMatches(found, definition.Groups);
        var exactCount = found.Count(match => match.Exact);
        var expectedCount = definition.Groups.Values.Sum(groups => groups.Count);
        if (exactCount == expectedCount) return NoChange(snapshot);
        var merged = (JsonObject)existing.DeepClone();
        AppendExpected(merged, definition);
        return new CommandDiscoveryHookPlan(
            snapshot.Bytes, Serialize(merged, CommandDiscoveryHookConfigurationError.InvalidHooksFile),
            true, "Add Ask Key's Cursor discovery command hooks.",
            Format, snapshot.Mode, snapshot.Mode);
    }

    private CommandDiscoveryHookPlan ClaudePlan(Snapshot? snapshot, Definition definition)
    {
        var bytes = snapshot?.Bytes ?? "{}"u8.ToArray();
        var document = CommandHookJson.Parse(bytes);
        var existing = ParseObject(bytes, CommandDiscoveryHookConfigurationError.InvalidHooksFile);
        var matches = ClaudeMatches(existing, definition);
        if (matches.Count == definition.Groups.Count) return ClaudeNoChange(snapshot);
        var hooks = document.Member("hooks") ?? CommandHookJson.EmptyObject;
        foreach (var eventName in definition.Groups.Keys.Order(StringComparer.Ordinal))
        {
            if (matches.Any(match => match.Event == eventName)) continue;
            var groups = hooks.Member(eventName) ?? CommandHookJson.EmptyArray;
            var generated = CommandHookJson.Generated(
                Serialize(definition.Groups[eventName][0],
                    CommandDiscoveryHookConfigurationError.InvalidExpectedHooks));
            groups.Append(generated);
            hooks.Set(eventName, groups);
        }
        document.Set("hooks", hooks);
        return ClaudeChanged(
            snapshot, document.Bytes, "Add Ask Key's Claude Code discovery command hooks.");
    }

    internal CommandDiscoveryHookPlan ClaudeRemovalPlan(Snapshot? snapshot, Definition definition)
    {
        if (snapshot is null) return ClaudeNoChange(null);
        var document = CommandHookJson.Parse(snapshot.Bytes);
        var matches = ClaudeMatches(
            ParseObject(snapshot.Bytes, CommandDiscoveryHookConfigurationError.InvalidHooksFile), definition);
        if (matches.Count == 0) return ClaudeNoChange(snapshot);
        var hooks = document.RequiredMember("hooks");
        foreach (var match in matches)
        {
            var groups = hooks.RequiredMember(match.Event);
            var group = groups.Element(match.Group);
            var handlers = group.RequiredMember("hooks");
            handlers.Remove(match.Handler);
            if (handlers.IsEmptyArray) groups.Remove(match.Group);
            else
            {
                group.Set("hooks", handlers);
                groups.SetElement(match.Group, group);
            }
            // Keep the event key, its remaining groups and all unrelated keys.
            hooks.Set(match.Event, groups);
        }
        document.Set("hooks", hooks);
        return ClaudeChanged(
            snapshot, document.Bytes, "Remove Ask Key's Claude Code discovery command hooks.");
    }

    private CommandDiscoveryHookPlan ClaudeNoChange(Snapshot? snapshot) => new(
        snapshot?.Bytes, snapshot?.Bytes, false,
        "Claude Code discovery command hooks are unchanged.", Format,
        snapshot?.Mode, snapshot?.Mode ?? WindowsFileSecurity.PrivateMode);

    private CommandDiscoveryHookPlan ClaudeChanged(Snapshot? snapshot, byte[] after, string summary)
    {
        if (after.Length > MaximumHooksBytes)
        {
            throw Error(CommandDiscoveryHookConfigurationError.FileTooLarge);
        }
        return new CommandDiscoveryHookPlan(
            snapshot?.Bytes, after, true, summary, Format,
            snapshot?.Mode, snapshot?.Mode ?? WindowsFileSecurity.PrivateMode);
    }

    internal CommandDiscoveryHookPlan NoChange(Snapshot snapshot) => new(
        snapshot.Bytes, snapshot.Bytes, false,
        $"Ask Key's {Format.DisplayName()} discovery command hook is already installed.",
        Format, snapshot.Mode, snapshot.Mode);

    internal List<ClaudeMatch> ClaudeMatches(JsonObject document, Definition definition)
    {
        if (!document.TryGetPropertyValue("hooks", out var value) || value is null) return new();
        if (value is not JsonObject hooks)
        {
            throw Error(CommandDiscoveryHookConfigurationError.InvalidHooksFile);
        }
        var matches = new List<ClaudeMatch>();
        foreach (var (eventName, groupsValue) in hooks)
        {
            if (groupsValue is not JsonArray groups)
            {
                throw Error(CommandDiscoveryHookConfigurationError.InvalidHooksFile);
            }
            for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
            {
                if (groups[groupIndex] is not JsonObject group
                    || group["hooks"] is not JsonArray handlers)
                {
                    throw Error(CommandDiscoveryHookConfigurationError.InvalidHooksFile);
                }
                for (var handlerIndex = 0; handlerIndex < handlers.Count; handlerIndex++)
                {
                    if (handlers[handlerIndex] is not JsonObject handler
                        || handler["command"]?.GetValue<string>() is not { } command)
                    {
                        continue;
                    }
                    var lower = command.ToLowerInvariant();
                    if (!definition.Commands.Contains(command, StringComparer.Ordinal)
                        && !lower.Contains("hook claude", StringComparison.Ordinal)
                        && !lower.Contains("askkey", StringComparison.Ordinal)
                        && !lower.Contains("ask key", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    if (!definition.Groups.TryGetValue(eventName, out var expectedGroups)
                        || expectedGroups[0] is not JsonObject expectedGroup
                        || expectedGroup["hooks"] is not JsonArray expectedHandlers
                        || expectedHandlers[0] is not JsonObject expectedHandler
                        || !JsonEqual(handler, expectedHandler)
                        || !JsonEqual(WithoutKey(group, "hooks"), WithoutKey(expectedGroup, "hooks")))
                    {
                        throw Error(CommandDiscoveryHookConfigurationError.CustomHookMismatch);
                    }
                    matches.Add(new ClaudeMatch(eventName, groupIndex, handlerIndex));
                }
            }
        }
        if (matches.GroupBy(match => match.Event, StringComparer.Ordinal).Any(group => group.Count() != 1))
        {
            throw Error(CommandDiscoveryHookConfigurationError.MultipleExpectedHooks);
        }
        return matches;
    }

    private static JsonObject WithoutKey(JsonObject source, string key)
    {
        var copy = new JsonObject();
        foreach (var (name, value) in source)
        {
            if (name != key) copy[name] = value?.DeepClone();
        }
        return copy;
    }

    internal List<Match> CursorMatches(JsonObject document, Definition definition)
    {
        if (!IsVersionOne(document["version"]))
        {
            throw Error(CommandDiscoveryHookConfigurationError.InvalidHooksFile);
        }
        JsonObject rawHooks;
        if (document.TryGetPropertyValue("hooks", out var value) && value is not null)
        {
            rawHooks = value as JsonObject
                ?? throw Error(CommandDiscoveryHookConfigurationError.InvalidHooksFile);
        }
        else
        {
            rawHooks = new JsonObject();
        }
        var found = new List<Match>();
        foreach (var (eventName, groupsValue) in rawHooks)
        {
            if (groupsValue is not JsonArray rawGroups)
            {
                throw Error(CommandDiscoveryHookConfigurationError.InvalidHooksFile);
            }
            foreach (var rawGroup in rawGroups)
            {
                if (rawGroup is not JsonObject group)
                {
                    throw Error(CommandDiscoveryHookConfigurationError.InvalidHooksFile);
                }
                var commands = CommandStrings(group);
                if (commands.Count == 0) continue;
                var exact = definition.Groups.TryGetValue(eventName, out var expected)
                    && expected.Any(candidate => JsonEqual(candidate, group));
                var ownLike = commands.Any(command => IsOwnLike(command, definition.Commands));
                if (exact || ownLike) found.Add(new Match(eventName, exact, ownLike));
            }
        }
        return found;
    }

    internal static void ValidateMatches(
        List<Match> matches, Dictionary<string, JsonArray> expected)
    {
        var own = matches.Count(match => match.OwnLike);
        var custom = matches.Count(match => match.OwnLike && !match.Exact);
        var expectedCount = expected.Values.Sum(groups => groups.Count);
        var exactByEvent = matches.Where(match => match.Exact)
            .GroupBy(match => match.Event, StringComparer.Ordinal);
        if (exactByEvent.Any(group =>
                group.Count() > (expected.TryGetValue(group.Key, out var groups) ? groups.Count : 0)))
        {
            throw Error(CommandDiscoveryHookConfigurationError.MultipleExpectedHooks);
        }
        if (own > expectedCount)
        {
            throw Error(CommandDiscoveryHookConfigurationError.MultipleExpectedHooks);
        }
        if (custom > 0)
        {
            throw Error(CommandDiscoveryHookConfigurationError.CustomHookMismatch);
        }
    }

    internal static void AppendExpected(JsonObject document, Definition definition)
    {
        JsonObject hooks;
        if (document.TryGetPropertyValue("hooks", out var raw) && raw is not null)
        {
            hooks = raw as JsonObject
                ?? throw Error(CommandDiscoveryHookConfigurationError.InvalidHooksFile);
        }
        else
        {
            hooks = new JsonObject();
        }
        foreach (var eventName in definition.Groups.Keys.Order(StringComparer.Ordinal))
        {
            JsonArray groups;
            if (hooks.TryGetPropertyValue(eventName, out var existing) && existing is not null)
            {
                groups = existing as JsonArray
                    ?? throw Error(CommandDiscoveryHookConfigurationError.InvalidHooksFile);
            }
            else
            {
                groups = new JsonArray();
            }
            foreach (var group in definition.Groups[eventName])
            {
                if (!groups.Any(item => item is JsonObject candidate && JsonEqual(candidate, group)))
                {
                    groups.Add(group!.DeepClone());
                }
            }
            hooks[eventName] = groups;
        }
        document["hooks"] = hooks;
    }

    private bool HasExpectedHook(byte[] bytes, Definition definition)
    {
        var existing = ParseObject(bytes, CommandDiscoveryHookConfigurationError.InvalidHooksFile);
        switch (Format)
        {
            case CommandDiscoveryHookFormat.ClaudeMerged:
                _ = CommandHookJson.Parse(bytes);
                return ClaudeMatches(existing, definition).Count == definition.Groups.Count;
            case CommandDiscoveryHookFormat.GrokOwned:
                if (JsonEqual(existing, definition.Root)) return true;
                if (ContainsOwnLike(existing, definition.Commands))
                {
                    throw Error(CommandDiscoveryHookConfigurationError.CustomHookMismatch);
                }
                throw Error(CommandDiscoveryHookConfigurationError.OwnedFileConflict);
            default:
                var found = CursorMatches(existing, definition);
                ValidateMatches(found, definition.Groups);
                return found.Count(match => match.Exact)
                    == definition.Groups.Values.Sum(groups => groups.Count);
        }
    }
    /// <summary>
    /// Windows has no RENAME_SWAP. ReplaceFile moves the temporary over the
    /// destination and keeps the displaced file, which gives the same
    /// "verify what we displaced, restore on mismatch" transaction.
    /// </summary>
    private void ReplaceClaude(Snapshot current, byte[] replacement, int mode)
    {
        var directory = Path.GetDirectoryName(HooksPath)!;
        string temporary;
        try
        {
            temporary = ClientConfigFileIO.WriteExclusiveTemporary(
                replacement, directory, ".askkey-claude-");
            WindowsFileSecurity.ApplyMode(temporary, mode);
        }
        catch (Exception exception) when (exception is ClientConfigFileException or IOException)
        {
            throw Error(CommandDiscoveryHookConfigurationError.WriteFailed);
        }

        var displaced = Path.Combine(directory, $".askkey-claude-swap-{Guid.NewGuid()}");
        var keepTemporary = true;
        try
        {
            if (!Matches(ReadSnapshot(HooksPath, checkParent: true), current.Bytes, current.Mode))
            {
                throw Error(CommandDiscoveryHookConfigurationError.ConcurrentModification);
            }
            try
            {
                File.Replace(temporary, HooksPath, displaced, ignoreMetadataErrors: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw Error(CommandDiscoveryHookConfigurationError.ConcurrentModification);
            }
            keepTemporary = false;
            try
            {
                WindowsFileSecurity.ApplyMode(HooksPath, mode);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                RestoreClaude(displaced, replacement, mode);
                throw Error(CommandDiscoveryHookConfigurationError.WriteFailed);
            }

            Snapshot? moved;
            try { moved = ReadSnapshot(displaced, checkParent: true); }
            catch (CommandDiscoveryHookConfigurationException)
            {
                RestoreClaude(displaced, replacement, mode);
                throw Error(CommandDiscoveryHookConfigurationError.RollbackFailed);
            }
            if (!Matches(moved, current.Bytes, current.Mode))
            {
                RestoreClaude(displaced, replacement, mode);
                throw Error(CommandDiscoveryHookConfigurationError.ConcurrentModification);
            }
            File.Delete(displaced);
        }
        finally
        {
            if (keepTemporary)
            {
                try { File.Delete(temporary); } catch (IOException) { }
            }
        }
    }

    private void RestoreClaude(string displaced, byte[] replacement, int mode)
    {
        Snapshot? published;
        try { published = ReadSnapshot(HooksPath, checkParent: true); }
        catch (CommandDiscoveryHookConfigurationException)
        {
            throw Error(CommandDiscoveryHookConfigurationError.RollbackFailed);
        }
        if (!Matches(published, replacement, mode))
        {
            throw Error(CommandDiscoveryHookConfigurationError.RollbackFailed);
        }
        try
        {
            File.Replace(displaced, HooksPath, null, ignoreMetadataErrors: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Error(CommandDiscoveryHookConfigurationError.RollbackFailed);
        }
    }

    internal bool ContainsOwnLike(JsonObject document, string[] commands)
        => AllCommands(document).Any(command => IsOwnLike(command, commands));

    private bool IsOwnLike(string command, string[] expected)
    {
        if (expected.Contains(command, StringComparer.Ordinal)) return true;
        var value = command.ToLowerInvariant();
        return value.Contains("askkey", StringComparison.Ordinal)
            || value.Contains("ask key", StringComparison.Ordinal)
            || value.Contains(Format.InvocationMarker(), StringComparison.Ordinal);
    }

    internal static List<string> CommandStrings(JsonObject group)
    {
        var values = new List<string>();
        if (group["command"]?.GetValue<string>() is { Length: > 0 } command) values.Add(command);
        if (group["hooks"] is JsonArray handlers)
        {
            foreach (var handler in handlers)
            {
                if (handler is JsonObject entry
                    && entry["command"]?.GetValue<string>() is { Length: > 0 } nested)
                {
                    values.Add(nested);
                }
            }
        }
        return values;
    }

    internal static List<string> AllCommands(JsonNode? value)
    {
        var values = new List<string>();
        switch (value)
        {
            case JsonObject obj:
                if (obj["command"]?.GetValue<string>() is { } command) values.Add(command);
                foreach (var (_, child) in obj) values.AddRange(AllCommands(child));
                break;
            case JsonArray array:
                foreach (var child in array) values.AddRange(AllCommands(child));
                break;
        }
        return values;
    }

    internal static JsonObject ParseObject(byte[] bytes, CommandDiscoveryHookConfigurationError error)
    {
        if (bytes.Length == 0 || bytes.Contains((byte)0))
        {
            throw Error(error);
        }
        try
        {
            return JsonNode.Parse(bytes) as JsonObject ?? throw Error(error);
        }
        catch (JsonException)
        {
            throw Error(error);
        }
    }

    internal static byte[] Serialize(JsonNode? node, CommandDiscoveryHookConfigurationError error)
    {
        try
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                WriteSorted(writer, node);
            }
            stream.WriteByte((byte)'\n');
            return stream.ToArray();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw Error(error);
        }
    }

    private static void WriteSorted(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var (key, value) in obj.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(key);
                    WriteSorted(writer, value);
                }
                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var value in array) WriteSorted(writer, value);
                writer.WriteEndArray();
                break;
            case JsonValue value:
                value.WriteTo(writer);
                break;
            default:
                throw new JsonException("unsupported JSON node");
        }
    }

    internal static bool JsonEqual(JsonNode? left, JsonNode? right)
        => JsonNode.DeepEquals(left, right);

    private static bool IsVersionOne(JsonNode? node)
        => node is JsonValue value
            && value.TryGetValue<double>(out var number)
            && number == 1d;

    internal static CommandDiscoveryHookConfigurationException Error(
        CommandDiscoveryHookConfigurationError error) => new(error);
}