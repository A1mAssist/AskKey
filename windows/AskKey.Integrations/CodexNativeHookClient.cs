using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AskKey.Integrations;

public enum CodexNativeHookStatus
{
    Missing,
    Enabled,
    Disabled,
    Untrusted,
    Unsupported,
}

public enum CodexNativeHookClientError
{
    CliMissing,
    UserHomeMismatch,
    Unsupported,
    InvalidResponse,
    UnsafeConfiguration,
    ConfigurationChanged,
    VerificationFailed,
    Cancelled,
    TimedOut,
    CommunicationFailed,
    WriteOutcomeUnknown,
}

public sealed class CodexNativeHookClientException : Exception
{
    public CodexNativeHookClientException(CodexNativeHookClientError error) : base(error.ToString())
        => Error = error;

    public CodexNativeHookClientError Error { get; }
}

/// <summary>
/// Reads Codex's own hook state and enables only the reviewed Ask Key rule.
/// No model is started, and no HOME/CODEX_HOME override is used.
/// </summary>
public sealed class CodexNativeHookClient
{
    public CodexNativeHookClient(string executable, string userHome)
    {
        Executable = executable;
        UserHome = Path.TrimEndingDirectorySeparator(Path.GetFullPath(userHome));
    }

    public string Executable { get; }
    public string UserHome { get; }

    private string CodexHome => Path.Combine(UserHome, ".codex");
    private string HooksPath => Path.Combine(CodexHome, "hooks.json");
    private string ConfigPath => Path.Combine(CodexHome, "config.toml");

    public CodexNativeHookStatus Status()
    {
        try
        {
            return WithSession(rpc =>
            {
                var snapshot = ReviewedSnapshot();
                var metadata = HookMetadata(rpc, snapshot);
                return State(metadata);
            });
        }
        catch (CodexNativeHookClientException exception)
            when (exception.Error == CodexNativeHookClientError.Unsupported)
        {
            return CodexNativeHookStatus.Unsupported;
        }
    }

    public CodexNativeHookStatus EnableReviewedHook(string? backupDirectory = null)
    {
        return WithSession(rpc =>
        {
            var reviewed = ReviewedSnapshot()
                ?? throw new CodexNativeHookClientException(CodexNativeHookClientError.VerificationFailed);
            var original = SafeConfig();
            var before = UserLayer(rpc);
            var metadata = HookMetadata(rpc, reviewed);
            if (State(metadata) == CodexNativeHookStatus.Enabled) return CodexNativeHookStatus.Enabled;

            var value = new JsonObject();
            foreach (var own in metadata)
            {
                var key = own["key"]?.GetValue<string>()
                    ?? throw new CodexNativeHookClientException(CodexNativeHookClientError.InvalidResponse);
                var hash = own["currentHash"]?.GetValue<string>()
                    ?? throw new CodexNativeHookClientException(CodexNativeHookClientError.InvalidResponse);
                value[key] = new JsonObject { ["enabled"] = true, ["trusted_hash"] = hash };
            }
            var keys = value.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
            if (keys.Count != 2)
            {
                throw new CodexNativeHookClientException(CodexNativeHookClientError.InvalidResponse);
            }

            var directory = backupDirectory ?? Path.Combine(
                UserHome, "AppData", "Roaming", "AskKey", "client-backups", "codex-trust");
            WindowsPathSafety.EnsureDirectory(
                directory, CommandDiscoveryHookConfigurationError.UnsafeBackupDirectory);
            WindowsFileSecurity.ApplyMode(directory, WindowsFileSecurity.PrivateMode);
            var backup = Path.Combine(directory, $"config-{Guid.NewGuid()}.toml");
            ClientConfigFileIO.PublishAtomically(
                original.Bytes, backup, WindowsFileSecurity.PrivateMode, exclusive: true, ".askkey-trust-");

            var current = SafeConfig();
            if (!current.Bytes.AsSpan().SequenceEqual(original.Bytes)
                || current.Mode != original.Mode
                || !(ReviewedSnapshot()?.Bytes.AsSpan().SequenceEqual(reviewed.Bytes) ?? false))
            {
                throw new CodexNativeHookClientException(CodexNativeHookClientError.ConfigurationChanged);
            }

            try
            {
                _ = rpc.Call("config/batchWrite", new JsonObject
                {
                    ["filePath"] = ConfigPath,
                    ["expectedVersion"] = before.Version,
                    ["reloadUserConfig"] = true,
                    ["edits"] = new JsonArray(new JsonObject
                    {
                        ["keyPath"] = "hooks.state",
                        ["mergeStrategy"] = "upsert",
                        ["value"] = value.DeepClone(),
                    }),
                });
                var after = UserLayer(rpc);
                var readback = HookMetadata(rpc, reviewed);
                var normalizedBefore = Normalized(before.Config, keys);
                var normalizedAfter = Normalized(after.Config, keys);
                var sameKeys = metadata.Count == readback.Count
                    && metadata.Zip(readback).All(pair =>
                        pair.First["key"]?.GetValue<string>() == pair.Second["key"]?.GetValue<string>()
                        && pair.First["currentHash"]?.GetValue<string>()
                            == pair.Second["currentHash"]?.GetValue<string>());
                if (!JsonNode.DeepEquals(normalizedBefore, normalizedAfter)
                    || !sameKeys
                    || State(readback) != CodexNativeHookStatus.Enabled)
                {
                    throw new CodexNativeHookClientException(CodexNativeHookClientError.VerificationFailed);
                }
            }
            catch
            {
                // The request may have been applied before its response was lost.
                // A fresh read is required before another write.
                throw new CodexNativeHookClientException(CodexNativeHookClientError.WriteOutcomeUnknown);
            }
            return CodexNativeHookStatus.Enabled;
        });
    }
    private sealed record Snapshot(byte[] Bytes);

    private Snapshot? ReviewedSnapshot()
    {
        // The file adapter validates the full definition, including input, and
        // rejects duplicate/customized Ask Key rules.
        var configuration = new CodexDiscoveryHookConfiguration(
            HooksPath, Path.Combine(CodexHome, "unused"), HelperPathFromHooks());
        if (!configuration.HasExpectedHook()) return null;
        var bytes = ClientConfigFileIO.ReadRegularFile(HooksPath, 1_048_576).Bytes;
        if (!configuration.HasExpectedHook()
            || !ClientConfigFileIO.ReadRegularFile(HooksPath).Bytes.AsSpan().SequenceEqual(bytes))
        {
            throw new CodexNativeHookClientException(CodexNativeHookClientError.ConfigurationChanged);
        }
        return new Snapshot(bytes);
    }

    private string HelperPathFromHooks()
    {
        // The reviewed command embeds the helper path; the file adapter only needs
        // it to compare exact command text.
        try
        {
            var document = JsonNode.Parse(File.ReadAllBytes(HooksPath)) as JsonObject;
            if (document?["hooks"] is JsonObject hooks
                && hooks["PreToolUse"] is JsonArray groups
                && groups.FirstOrDefault() is JsonObject group
                && group["hooks"] is JsonArray handlers
                && handlers.FirstOrDefault() is JsonObject handler
                && handler["command"]?.GetValue<string>() is { } command)
            {
                var trimmed = command.Trim('"');
                var marker = trimmed.LastIndexOf(" hook codex", StringComparison.Ordinal);
                return marker > 0 ? trimmed[..marker] : trimmed;
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
        }
        return Path.Combine(UserHome, ".codex", "askkey.exe");
    }

    private List<JsonObject> HookMetadata(RPC rpc, Snapshot? snapshot)
    {
        var response = rpc.Call("hooks/list", new JsonObject { ["cwds"] = new JsonArray(UserHome) });
        if (response["data"] is not JsonArray entries || entries.Count != 1
            || entries[0] is not JsonObject entry
            || entry["errors"] is not JsonArray errors || errors.Count != 0
            || entry["hooks"] is not JsonArray hooks)
        {
            throw new CodexNativeHookClientException(CodexNativeHookClientError.InvalidResponse);
        }
        if (snapshot is null)
        {
            if (ReviewedSnapshot() is not null)
            {
                throw new CodexNativeHookClientException(CodexNativeHookClientError.ConfigurationChanged);
            }
            return new List<JsonObject>();
        }
        var matches = hooks.OfType<JsonObject>().Where(hook =>
            hook["sourcePath"]?.GetValue<string>() == HooksPath
            && new CodexDiscoveryHookConfiguration(HooksPath, Path.Combine(CodexHome, "unused"),
                    HelperPathFromHooks()).IsOwnCommand(hook["command"]?.GetValue<string>())).ToList();
        var result = new List<JsonObject>();
        foreach (var eventName in new[] { "preToolUse", "postToolUse" })
        {
            var candidates = matches
                .Where(hook => hook["eventName"]?.GetValue<string>() == eventName).ToList();
            if (candidates.Count != 1) throw VerificationFailed();
            var own = candidates[0];
            var key = own["key"]?.GetValue<string>();
            var hash = own["currentHash"]?.GetValue<string>();
            if (key is null || key.Length == 0 || !ValidKey(key, HooksPath, eventName)
                || own["source"]?.GetValue<string>() != "user"
                || own["isManaged"]?.GetValue<bool>() != false
                || own["handlerType"]?.GetValue<string>() != "command"
                || own["command"]?.GetValue<string>() != ExpectedCommand()
                || own["async"]?.GetValue<bool>() != false
                || own["matcher"]?.GetValue<string>() != CodexDiscoveryHookConfiguration.ExpectedMatcher
                || own["timeoutSec"]?.GetValue<int>() != 3
                || hash is null || !hash.StartsWith("sha256:", StringComparison.Ordinal) || hash.Length != 71
                || !hash[7..].All(char.IsAsciiHexDigit)
                || own["enabled"] is not JsonValue
                || own["trustStatus"]?.GetValue<string>() is not ("trusted" or "untrusted" or "modified"))
            {
                throw VerificationFailed();
            }
            result.Add(own);
        }
        if (matches.Count != 2
            || result.Select(hook => hook["key"]?.GetValue<string>()).Distinct(StringComparer.Ordinal).Count() != 2
            || !(ReviewedSnapshot()?.Bytes.AsSpan().SequenceEqual(snapshot.Bytes) ?? false))
        {
            throw VerificationFailed();
        }
        return result;
    }

    private string ExpectedCommand() => $"\"{HelperPathFromHooks()}\" hook codex";

    private static CodexNativeHookClientException VerificationFailed()
        => new(CodexNativeHookClientError.VerificationFailed);

    private static bool ValidKey(string key, string sourcePath, string eventName)
    {
        var eventKey = eventName == "preToolUse" ? "pre_tool_use" : "post_tool_use";
        var prefix = $"{sourcePath}:{eventKey}:";
        if (!key.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var indices = key[prefix.Length..].Split(':');
        return indices.Length == 2 && indices.All(index => int.TryParse(index, out var value) && value >= 0);
    }

    private static CodexNativeHookStatus State(List<JsonObject> metadata)
    {
        if (metadata.Count != 2) return CodexNativeHookStatus.Missing;
        if (metadata.Any(hook => hook["trustStatus"]?.GetValue<string>() != "trusted"))
        {
            return CodexNativeHookStatus.Untrusted;
        }
        return metadata.All(hook => hook["enabled"]?.GetValue<bool>() == true)
            ? CodexNativeHookStatus.Enabled
            : CodexNativeHookStatus.Disabled;
    }

    private (string Version, JsonObject Config) UserLayer(RPC rpc)
    {
        var result = rpc.Call("config/read", new JsonObject { ["includeLayers"] = true });
        if (result["layers"] is not JsonArray layers)
        {
            throw new CodexNativeHookClientException(CodexNativeHookClientError.InvalidResponse);
        }
        var users = layers.OfType<JsonObject>().Where(layer =>
            layer["name"] is JsonObject name && name["type"]?.GetValue<string>() == "user").ToList();
        if (users.Count != 1) throw new CodexNativeHookClientException(CodexNativeHookClientError.UserHomeMismatch);
        var layer = users[0];
        var layerName = layer["name"] as JsonObject;
        if (layerName?["file"]?.GetValue<string>() != ConfigPath
            || layer["version"]?.GetValue<string>() is not { Length: > 0 } version
            || layer["config"] is not JsonObject config
            || (layer["disabledReason"] is not null && layer["disabledReason"] is not JsonValue))
        {
            throw new CodexNativeHookClientException(CodexNativeHookClientError.UserHomeMismatch);
        }
        return (version, config);
    }

    private (byte[] Bytes, int Mode) SafeConfig()
    {
        try
        {
            var file = ClientConfigFileIO.ReadRegularFile(ConfigPath, 1_048_576);
            return (file.Bytes, file.Mode);
        }
        catch (ClientConfigFileException)
        {
            throw new CodexNativeHookClientException(CodexNativeHookClientError.UnsafeConfiguration);
        }
    }

    private static JsonNode? Normalized(JsonObject input, HashSet<string> keys)
    {
        var config = (JsonObject)input.DeepClone();
        if (config["hooks"] is JsonObject hooks)
        {
            if (hooks["state"] is JsonObject state)
            {
                foreach (var key in keys) state.Remove(key);
                if (state.Count == 0) hooks.Remove("state");
                else hooks["state"] = state;
            }
            if (hooks.Count == 0) config.Remove("hooks");
            else config["hooks"] = hooks;
        }
        return config;
    }

    private T WithSession<T>(Func<RPC, T> action)
    {
        if (!HelperTrust.IsExecutableRegularFile(Executable))
        {
            throw new CodexNativeHookClientException(CodexNativeHookClientError.CliMissing);
        }
        try
        {
            using var session = global::AskKey.System.InteractiveSession.Start(
                new global::AskKey.System.InteractiveProcessRequest
                {
                    Executable = Executable,
                    Arguments = new[] { "app-server", "--disable", "plugins", "--disable", "apps" },
                    Environment = IntegrationProcess.CurrentEnvironment(),
                    CurrentDirectory = UserHome,
                    Timeout = TimeSpan.FromSeconds(10),
                    MaximumInputBytes = 1_048_576,
                    MaximumOutputBytes = 4_194_304,
                });
            var rpc = new RPC(session);
            var info = rpc.Call("initialize", new JsonObject
            {
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = "askkey-onboarding",
                    ["version"] = "1",
                },
                ["capabilities"] = new JsonObject { ["experimentalApi"] = true },
            });
            if (info["codexHome"]?.GetValue<string>() != CodexHome)
            {
                throw new CodexNativeHookClientException(CodexNativeHookClientError.UserHomeMismatch);
            }
            rpc.Notify("initialized");
            return action(rpc);
        }
        catch (global::AskKey.System.InteractiveProcessException exception)
        {
            throw new CodexNativeHookClientException(exception.Kind switch
            {
                global::AskKey.System.InteractiveFailureKind.Cancelled => CodexNativeHookClientError.Cancelled,
                global::AskKey.System.InteractiveFailureKind.TimedOut => CodexNativeHookClientError.TimedOut,
                _ => CodexNativeHookClientError.CommunicationFailed,
            });
        }
    }

    private sealed class RPC
    {
        private readonly global::AskKey.System.InteractiveSession _session;
        private int _nextId;

        public RPC(global::AskKey.System.InteractiveSession session) => _session = session;

        public void Notify(string method)
            => _session.WriteLine(JsonSerializer.SerializeToUtf8Bytes(new { method }));

        public JsonObject Call(string method, JsonObject parameters)
        {
            var id = ++_nextId;
            _session.WriteLine(JsonSerializer.SerializeToUtf8Bytes(new
            {
                id,
                method,
                @params = parameters,
            }));
            var deadline = DateTime.UtcNow.AddSeconds(10);
            var bytes = 0;
            while (DateTime.UtcNow < deadline)
            {
                var line = _session.ReadLine();
                bytes += line.Length;
                if (bytes > 4_194_304)
                {
                    throw new CodexNativeHookClientException(CodexNativeHookClientError.InvalidResponse);
                }
                JsonNode? message;
                try { message = JsonNode.Parse(line); }
                catch (JsonException)
                {
                    throw new CodexNativeHookClientException(CodexNativeHookClientError.InvalidResponse);
                }
                if (message is not JsonObject obj
                    || obj["id"]?.GetValue<int>() is not { } messageId)
                {
                    throw new CodexNativeHookClientException(CodexNativeHookClientError.InvalidResponse);
                }
                if (messageId != id) continue;
                if (obj["error"] is JsonObject error)
                {
                    if (error["code"]?.GetValue<int>() == -32601)
                    {
                        throw new CodexNativeHookClientException(CodexNativeHookClientError.Unsupported);
                    }
                    throw new CodexNativeHookClientException(CodexNativeHookClientError.CommunicationFailed);
                }
                return obj["result"] as JsonObject
                    ?? throw new CodexNativeHookClientException(CodexNativeHookClientError.InvalidResponse);
            }
            throw new CodexNativeHookClientException(CodexNativeHookClientError.TimedOut);
        }
    }
}