using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AskKey.Integrations;

public sealed record CursorMCPDiff(string Before, string After);

public sealed record CursorMCPConnectionStatus(
    bool Connected, bool ConfigReady, bool HelperReady, bool ProtocolReady, bool BrokerHealthy);

public enum CursorMCPError
{
    UnsafeFile,
    InvalidJSON,
    ReplaceFailed,
    ReadbackFailed,
    RollbackFailed,
    BackupCleanupFailed,
}

public sealed class CursorMCPException : Exception
{
    public CursorMCPException(CursorMCPError error) : base(Describe(error)) => Error = error;

    public CursorMCPError Error { get; }

    private static string Describe(CursorMCPError error) => error switch
    {
        CursorMCPError.UnsafeFile =>
            "Ask Key will not write a Cursor MCP config that is a symbolic link or special file.",
        CursorMCPError.InvalidJSON => "The Cursor MCP config is not valid JSON, so it was left unchanged.",
        CursorMCPError.ReplaceFailed => "Ask Key could not replace the Cursor MCP config.",
        CursorMCPError.ReadbackFailed => "The Cursor MCP config could not be verified after writing.",
        CursorMCPError.RollbackFailed =>
            "Ask Key could not restore the original Cursor MCP config. The managed backup was kept for recovery.",
        _ => "Ask Key verified Cursor, but could not remove the managed backup. The connection was not marked successful.",
    };
}

internal sealed record CursorConfigSnapshot(bool Existed, int Permissions, byte[] Bytes, string? Quarantine);

internal sealed class CursorRollbackBackup
{
    public string GenerationId { get; set; } = Guid.NewGuid().ToString();
    public bool OriginalExisted { get; set; }
    public int OriginalPermissions { get; set; }
    public string OriginalBytes { get; set; } = "";
    public string ReplacementDigest { get; set; } = "";

    public byte[] OriginalBytesValue => Convert.FromBase64String(OriginalBytes);
    public byte[] ReplacementDigestValue => Convert.FromBase64String(ReplacementDigest);

    public byte[] Encode() => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
    {
        ["generationID"] = GenerationId,
        ["originalExisted"] = OriginalExisted,
        ["originalPermissions"] = OriginalPermissions,
        ["originalBytes"] = OriginalBytes,
        ["replacementDigest"] = ReplacementDigest,
    });

    public static CursorRollbackBackup Decode(byte[] data)
    {
        var node = JsonNode.Parse(data) as JsonObject
            ?? throw new CursorMCPException(CursorMCPError.RollbackFailed);
        try
        {
            return new CursorRollbackBackup
            {
                GenerationId = node["generationID"]?.GetValue<string>()
                    ?? throw new CursorMCPException(CursorMCPError.RollbackFailed),
                OriginalExisted = node["originalExisted"]?.GetValue<bool>()
                    ?? throw new CursorMCPException(CursorMCPError.RollbackFailed),
                OriginalPermissions = node["originalPermissions"]?.GetValue<int>()
                    ?? throw new CursorMCPException(CursorMCPError.RollbackFailed),
                OriginalBytes = node["originalBytes"]?.GetValue<string>()
                    ?? throw new CursorMCPException(CursorMCPError.RollbackFailed),
                ReplacementDigest = node["replacementDigest"]?.GetValue<string>()
                    ?? throw new CursorMCPException(CursorMCPError.RollbackFailed),
            };
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
        {
            throw new CursorMCPException(CursorMCPError.RollbackFailed);
        }
    }
}

internal sealed class CursorBackupOwnership
{
    private readonly object _lock = new();
    private string? _generationId;

    public void Set(string generationId) { lock (_lock) _generationId = generationId; }

    public bool Matches(string generationId) { lock (_lock) return _generationId == generationId; }
}
/// <summary>
/// User-scope Cursor MCP configuration at <c>~/.cursor/mcp.json</c>. The Ask Key
/// entry is merged into <c>mcpServers</c>; every other key is preserved, and the
/// previous file is journalled so a failed verification can restore it.
/// </summary>
public sealed class CursorUserMCPAdapter
{
    public const int MaximumConfigBytes = 1_048_576;

    private static readonly object ProcessBackupLock = new();
    private readonly CursorBackupOwnership _ownership = new();

    public CursorUserMCPAdapter(
        string homeDirectory,
        string backupDirectory,
        string helperPath,
        string brokerPipeName,
        CodexHelperSigning? signing = null,
        Action<string, string>? replaceConfig = null,
        Action<string>? removeConfig = null,
        Action<string, string>? moveConfigExclusively = null,
        Action<string>? removeBackupItem = null,
        string? brokerPipeFile = null)
    {
        UserConfigPath = Path.Combine(homeDirectory, ".cursor", "mcp.json");
        BackupDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(backupDirectory));
        HelperPath = helperPath;
        BrokerPipeName = brokerPipeName;
        BrokerPipeFile = brokerPipeFile ?? brokerPipeName;
        Signing = signing ?? CodexHelperSigning.Executable;
        ReplaceConfig = replaceConfig ?? ((source, destination) =>
            ClientConfigFileIO.RenameExclusively(source, destination));
        RemoveConfig = removeConfig ?? (path => File.Delete(path));
        MoveConfigExclusively = moveConfigExclusively ?? ((source, destination) =>
            ClientConfigFileIO.RenameExclusively(source, destination));
        RemoveBackupItem = removeBackupItem ?? (path => File.Delete(path));
    }

    public string UserConfigPath { get; }
    public string BackupDirectory { get; }
    public string HelperPath { get; }
    public string BrokerPipeName { get; }
    public string BrokerPipeFile { get; }
    public CodexHelperSigning Signing { get; }

    internal Action<string, string> ReplaceConfig { get; }
    internal Action<string> RemoveConfig { get; }
    internal Action<string, string> MoveConfigExclusively { get; }
    internal Action<string> RemoveBackupItem { get; }

    internal string BackupPath => Path.Combine(BackupDirectory, "cursor-mcp.json");
    private string BackupLockPath => Path.Combine(Path.GetDirectoryName(BackupDirectory)!, ".cursor.lock");

    public bool HasConfiguration()
    {
        var existing = LoadExistingObject();
        return existing?["mcpServers"] is JsonObject servers && servers.ContainsKey("askkey");
    }

    public CursorMCPDiff Preview()
    {
        var existing = LoadExistingObject();
        var merged = MergeAskKey(existing ?? new JsonObject());
        return MakeDiff(existing, merged);
    }

    public CursorMCPConnectionStatus Status()
    {
        var configReady = ConfiguredAskKeyMatches();
        var helperReady = HelperTrust.IsExecutableRegularFile(HelperPath) && Signing.IsTrusted(HelperPath);
        var protocolReady = configReady && ProbeMcp();
        var brokerHealthy = ProbeBroker();
        return new CursorMCPConnectionStatus(
            configReady && helperReady && protocolReady && brokerHealthy,
            configReady, helperReady, protocolReady, brokerHealthy);
    }

    public CursorMCPConnectionStatus Verify()
    {
        return WithBackupLock(() =>
        {
            var status = Status();
            if (status.Connected && File.Exists(BackupPath)) CleanupOwnedBackup();
            return status;
        });
    }

    public CursorMCPDiff Apply()
    {
        return WithBackupLock(() =>
        {
            var snapshot = TakeConfigSnapshot();
            try
            {
                var existing = snapshot.Existed
                    ? (snapshot.Bytes.Length == 0 ? new JsonObject() : DecodeObject(snapshot.Bytes))
                    : null;
                var merged = MergeAskKey(existing ?? new JsonObject());
                var diff = MakeDiff(existing, merged);
                var bytes = Encode(merged);
                var backup = PrepareBackup(
                    snapshot.Existed, snapshot.Permissions, snapshot.Bytes, bytes);
                try
                {
                    WriteAtomically(bytes, snapshot.Existed ? snapshot.Permissions : WindowsFileSecurity.PrivateMode, ReplaceConfig);
                }
                catch
                {
                    RestoreInputSnapshot(snapshot);
                    throw;
                }
                if (snapshot.Quarantine is { } quarantine && File.Exists(quarantine))
                {
                    File.Delete(quarantine);
                }
                try
                {
                    ReadBack(snapshot.Existed ? snapshot.Permissions : WindowsFileSecurity.PrivateMode);
                }
                catch (CursorMCPException)
                {
                    try
                    {
                        if (ReadBackup().GenerationId != backup.GenerationId)
                        {
                            throw new CursorMCPException(CursorMCPError.RollbackFailed);
                        }
                        Restore(backup);
                    }
                    catch
                    {
                        throw new CursorMCPException(CursorMCPError.RollbackFailed);
                    }
                    throw;
                }
                return diff;
            }
            catch (CursorMCPException)
            {
                RestoreInputSnapshot(snapshot);
                throw;
            }
            catch (Exception)
            {
                RestoreInputSnapshot(snapshot);
                throw new CursorMCPException(CursorMCPError.ReplaceFailed);
            }
        });
    }

    public void Rollback()
    {
        WithBackupLock(() =>
        {
            if (!File.Exists(BackupPath)) return false;
            try
            {
                var backup = ReadBackup();
                if (!_ownership.Matches(backup.GenerationId))
                {
                    throw new CursorMCPException(CursorMCPError.RollbackFailed);
                }
                Restore(backup);
                RemoveBackupItem(BackupPath);
                return true;
            }
            catch
            {
                throw new CursorMCPException(CursorMCPError.RollbackFailed);
            }
        });
    }
    private CursorConfigSnapshot TakeConfigSnapshot()
    {
        Inspect(UserConfigPath);
        if (!File.Exists(UserConfigPath))
        {
            return new CursorConfigSnapshot(false, 0, global::System.Array.Empty<byte>(), null);
        }
        var quarantine = Path.Combine(
            Path.GetDirectoryName(UserConfigPath)!, $".askkey-input-{Guid.NewGuid()}");
        try
        {
            MoveConfigExclusively(UserConfigPath, quarantine);
        }
        catch
        {
            throw new CursorMCPException(CursorMCPError.RollbackFailed);
        }
        try
        {
            var frozen = ReadRegularFileSnapshot(quarantine);
            return new CursorConfigSnapshot(true, frozen.Mode, frozen.Bytes, quarantine);
        }
        catch
        {
            try { MoveConfigExclusively(quarantine, UserConfigPath); }
            catch { throw new CursorMCPException(CursorMCPError.RollbackFailed); }
            throw;
        }
    }

    private void RestoreInputSnapshot(CursorConfigSnapshot snapshot)
    {
        if (snapshot.Quarantine is not { } quarantine || !File.Exists(quarantine)) return;
        if (File.Exists(UserConfigPath) || Directory.Exists(UserConfigPath))
        {
            throw new CursorMCPException(CursorMCPError.RollbackFailed);
        }
        try
        {
            MoveConfigExclusively(quarantine, UserConfigPath);
        }
        catch
        {
            throw new CursorMCPException(CursorMCPError.RollbackFailed);
        }
    }

    private CursorRollbackBackup PrepareBackup(
        bool originalExisted, int originalPermissions, byte[] originalBytes, byte[] replacementBytes)
    {
        CursorRollbackBackup backup;
        if (File.Exists(BackupPath))
        {
            var existing = ReadBackup();
            var currentDigest = SHA256.HashData(originalBytes);
            var currentPermissions = originalExisted ? originalPermissions : WindowsFileSecurity.PrivateMode;
            var sameReplacement = existing.ReplacementDigestValue.AsSpan().SequenceEqual(currentDigest)
                && currentPermissions == (existing.OriginalExisted
                    ? existing.OriginalPermissions : WindowsFileSecurity.PrivateMode);
            var sameOriginal = existing.OriginalExisted
                && originalBytes.AsSpan().SequenceEqual(existing.OriginalBytesValue)
                && currentPermissions == existing.OriginalPermissions;
            backup = sameReplacement || sameOriginal ? existing : NewBackup();
        }
        else
        {
            backup = NewBackup();
        }
        backup.OriginalExisted = originalExisted;
        backup.OriginalPermissions = originalPermissions;
        backup.OriginalBytes = Convert.ToBase64String(originalBytes);
        backup.ReplacementDigest = Convert.ToBase64String(SHA256.HashData(replacementBytes));
        WriteBackup(backup);
        _ownership.Set(backup.GenerationId);
        return backup;

        CursorRollbackBackup NewBackup() => new()
        {
            OriginalExisted = originalExisted,
            OriginalPermissions = originalPermissions,
            OriginalBytes = Convert.ToBase64String(originalBytes),
        };
    }

    private void WriteBackup(CursorRollbackBackup backup)
    {
        WindowsPathSafety.EnsureDirectory(BackupDirectory, CommandDiscoveryHookConfigurationError.UnsafeBackupDirectory);
        WindowsFileSecurity.ApplyMode(BackupDirectory, WindowsFileSecurity.PrivateMode);
        var bytes = backup.Encode();
        var temporary = BackupPath + ".tmp-" + Guid.NewGuid();
        try
        {
            File.WriteAllBytes(temporary, bytes);
            WindowsFileSecurity.SecureFile(temporary);
            File.Move(temporary, BackupPath, overwrite: true);
            WindowsFileSecurity.SecureFile(BackupPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temporary); } catch (IOException) { }
            throw new CursorMCPException(CursorMCPError.ReplaceFailed);
        }
    }

    private CursorRollbackBackup ReadBackup(string? path = null)
    {
        try
        {
            return CursorRollbackBackup.Decode(ClientConfigFileIO.ReadRegularFile(path ?? BackupPath, MaximumConfigBytes).Bytes);
        }
        catch (ClientConfigFileException)
        {
            throw new CursorMCPException(CursorMCPError.RollbackFailed);
        }
    }

    private void CleanupOwnedBackup()
    {
        var quarantine = Path.Combine(BackupDirectory, $".cursor-backup-cleanup-{Guid.NewGuid()}");
        try
        {
            ClientConfigFileIO.RenameExclusively(BackupPath, quarantine);
            var backup = ReadBackup(quarantine);
            if (!_ownership.Matches(backup.GenerationId))
            {
                throw new CursorMCPException(CursorMCPError.BackupCleanupFailed);
            }
            var current = Inspect(UserConfigPath);
            var expectedMode = backup.OriginalExisted
                ? backup.OriginalPermissions : WindowsFileSecurity.PrivateMode;
            var currentBytes = ClientConfigFileIO.ReadRegularFile(UserConfigPath, MaximumConfigBytes).Bytes;
            if (!current.Exists || current.Mode != expectedMode
                || !SHA256.HashData(currentBytes).AsSpan().SequenceEqual(backup.ReplacementDigestValue))
            {
                throw new CursorMCPException(CursorMCPError.BackupCleanupFailed);
            }
            RemoveBackupItem(quarantine);
            if (File.Exists(quarantine)) throw new CursorMCPException(CursorMCPError.BackupCleanupFailed);
        }
        catch
        {
            if (File.Exists(quarantine) && !File.Exists(BackupPath))
            {
                try { ClientConfigFileIO.RenameExclusively(quarantine, BackupPath); }
                catch { throw new CursorMCPException(CursorMCPError.BackupCleanupFailed); }
            }
            throw new CursorMCPException(CursorMCPError.BackupCleanupFailed);
        }
    }

    private void Restore(CursorRollbackBackup backup)
    {
        var current = Inspect(UserConfigPath);
        if (backup.OriginalExisted)
        {
            RestoreOriginal(backup, current.Exists);
        }
        else if (current.Exists)
        {
            RemoveApplied(backup);
        }
    }

    private void RestoreOriginal(CursorRollbackBackup backup, bool currentExists)
    {
        if (!currentExists) throw new CursorMCPException(CursorMCPError.RollbackFailed);
        var quarantine = QuarantineCurrent();
        byte[] bytes;
        try { bytes = ClientConfigFileIO.ReadRegularFile(quarantine, MaximumConfigBytes).Bytes; }
        catch
        {
            try { MoveConfigExclusively(quarantine, UserConfigPath); }
            catch { throw new CursorMCPException(CursorMCPError.RollbackFailed); }
            throw new CursorMCPException(CursorMCPError.RollbackFailed);
        }
        if (!bytes.AsSpan().SequenceEqual(backup.OriginalBytesValue)
            && !SHA256.HashData(bytes).AsSpan().SequenceEqual(backup.ReplacementDigestValue))
        {
            try { MoveConfigExclusively(quarantine, UserConfigPath); }
            catch { throw new CursorMCPException(CursorMCPError.RollbackFailed); }
            throw new CursorMCPException(CursorMCPError.RollbackFailed);
        }
        try
        {
            WriteAtomically(backup.OriginalBytesValue, backup.OriginalPermissions, MoveConfigExclusively);
            RemoveConfig(quarantine);
            if (File.Exists(quarantine)) throw new CursorMCPException(CursorMCPError.RollbackFailed);
        }
        catch
        {
            if (File.Exists(quarantine) && !File.Exists(UserConfigPath))
            {
                try { MoveConfigExclusively(quarantine, UserConfigPath); }
                catch { throw new CursorMCPException(CursorMCPError.RollbackFailed); }
            }
            throw new CursorMCPException(CursorMCPError.RollbackFailed);
        }
    }

    private void RemoveApplied(CursorRollbackBackup backup)
    {
        var quarantine = QuarantineCurrent();
        byte[] bytes;
        try { bytes = ClientConfigFileIO.ReadRegularFile(quarantine, MaximumConfigBytes).Bytes; }
        catch
        {
            try { MoveConfigExclusively(quarantine, UserConfigPath); }
            catch { throw new CursorMCPException(CursorMCPError.RollbackFailed); }
            throw new CursorMCPException(CursorMCPError.RollbackFailed);
        }
        if (!SHA256.HashData(bytes).AsSpan().SequenceEqual(backup.ReplacementDigestValue))
        {
            try { MoveConfigExclusively(quarantine, UserConfigPath); }
            catch { throw new CursorMCPException(CursorMCPError.RollbackFailed); }
            throw new CursorMCPException(CursorMCPError.RollbackFailed);
        }
        try
        {
            RemoveConfig(quarantine);
            if (File.Exists(quarantine)) throw new CursorMCPException(CursorMCPError.RollbackFailed);
        }
        catch
        {
            if (File.Exists(quarantine) && !File.Exists(UserConfigPath))
            {
                try { MoveConfigExclusively(quarantine, UserConfigPath); }
                catch { throw new CursorMCPException(CursorMCPError.RollbackFailed); }
            }
            throw new CursorMCPException(CursorMCPError.RollbackFailed);
        }
    }

    private string QuarantineCurrent()
    {
        var quarantine = Path.Combine(
            Path.GetDirectoryName(UserConfigPath)!, $".askkey-rollback-{Guid.NewGuid()}");
        try { MoveConfigExclusively(UserConfigPath, quarantine); }
        catch { throw new CursorMCPException(CursorMCPError.RollbackFailed); }
        return quarantine;
    }

    private void WriteAtomically(byte[] bytes, int mode, Action<string, string> replace)
    {
        var directory = Path.GetDirectoryName(UserConfigPath)!;
        Directory.CreateDirectory(directory);
        var temporary = ClientConfigFileIO.WriteExclusiveTemporary(bytes, directory, ".askkey-mcp-");
        try
        {
            WindowsFileSecurity.ApplyMode(temporary, mode);
            replace(temporary, UserConfigPath);
            var info = Inspect(UserConfigPath);
            if (!info.Exists || info.Mode != mode)
            {
                throw new CursorMCPException(CursorMCPError.ReadbackFailed);
            }
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { }
        }
    }

    private void ReadBack(int expectedMode)
    {
        var info = Inspect(UserConfigPath);
        if (!info.Exists || info.Mode != expectedMode)
        {
            throw new CursorMCPException(CursorMCPError.ReadbackFailed);
        }
        if (!ConfiguredAskKeyMatches()) throw new CursorMCPException(CursorMCPError.ReadbackFailed);
    }
    private (bool Exists, int Mode) Inspect(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            if (WindowsFileSecurity.IsLinkOrReparse(path))
            {
                throw new CursorMCPException(CursorMCPError.UnsafeFile);
            }
            return (false, 0);
        }
        if (WindowsFileSecurity.IsLinkOrReparse(path) || !File.Exists(path))
        {
            throw new CursorMCPException(CursorMCPError.UnsafeFile);
        }
        return (true, WindowsFileSecurity.ModeForFile(path));
    }

    private (byte[] Bytes, int Mode) ReadRegularFileSnapshot(string path)
    {
        try
        {
            var file = ClientConfigFileIO.ReadRegularFile(path, MaximumConfigBytes);
            return (file.Bytes, file.Mode);
        }
        catch (ClientConfigFileException exception)
        {
            throw exception.Failure is ClientConfigFileFailure.NotFound or ClientConfigFileFailure.Unsafe
                ? new CursorMCPException(CursorMCPError.UnsafeFile)
                : new CursorMCPException(CursorMCPError.ReplaceFailed);
        }
    }

    internal JsonObject? LoadExistingObject()
    {
        var info = Inspect(UserConfigPath);
        if (!info.Exists) return null;
        var bytes = ReadRegularFileSnapshot(UserConfigPath).Bytes;
        return bytes.Length == 0 ? new JsonObject() : DecodeObject(bytes);
    }

    internal JsonObject MergeAskKey(JsonObject root)
    {
        var merged = (JsonObject)root.DeepClone();
        var servers = merged["mcpServers"] as JsonObject ?? new JsonObject();
        servers["askkey"] = new JsonObject
        {
            ["command"] = HelperPath,
            ["args"] = new JsonArray("mcp"),
        };
        merged["mcpServers"] = servers;
        return merged;
    }

    private CursorMCPDiff MakeDiff(JsonObject? existing, JsonObject merged)
    {
        var before = existing is null ? "" : Encoding.UTF8.GetString(Encode(Redacted(existing)));
        var after = Encoding.UTF8.GetString(Encode(Redacted(merged)));
        return new CursorMCPDiff(before, after);
    }

    internal static JsonObject DecodeObject(byte[] data)
    {
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(data); }
        catch (JsonException) { throw new CursorMCPException(CursorMCPError.InvalidJSON); }
        if (parsed is not JsonObject root
            || (root["mcpServers"] is not null && root["mcpServers"] is not JsonObject))
        {
            throw new CursorMCPException(CursorMCPError.InvalidJSON);
        }
        return root;
    }

    private static byte[] Encode(JsonNode node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            WriteSorted(writer, node);
        }
        return stream.ToArray();
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
                throw new JsonException("unsupported node");
        }
    }

    private static JsonObject Redacted(JsonObject value)
    {
        var output = new JsonObject();
        foreach (var (key, child) in value)
        {
            output[key] = IsSensitiveKey(key) ? "***" : RedactedValue(child);
        }
        return output;
    }

    private static JsonNode? RedactedValue(JsonNode? value) => value switch
    {
        JsonObject obj => Redacted(obj),
        JsonArray array => new JsonArray(array.Select(RedactedValue).ToArray()),
        _ => value?.DeepClone(),
    };

    private static bool IsSensitiveKey(string key)
    {
        var normalized = new string(key.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        return normalized is "env" or "headers"
            || new[] { "token", "secret", "password", "authorization", "apikey", "credential", "cookie" }
                .Any(normalized.Contains);
    }

    internal bool ConfiguredAskKeyMatches()
    {
        var existing = LoadExistingObject();
        return existing?["mcpServers"] is JsonObject servers
            && servers["askkey"] is JsonObject askkey
            && askkey["command"]?.GetValue<string>() == HelperPath
            && askkey["args"] is JsonArray args
            && args.Count == 1
            && args[0]?.GetValue<string>() == "mcp";
    }

    internal bool ProbeMcp()
    {
        if (!HelperTrust.IsExecutableRegularFile(HelperPath) || !Signing.IsTrusted(HelperPath)) return false;
        var environment = IntegrationProcess.CurrentEnvironment();
        environment["ASKKEY_BROKER_PIPE"] = BrokerPipeFile;
        try
        {
            var result = IntegrationProcess.RunCaptured(
                HelperPath, new[] { "mcp" }, environment, Path.GetDirectoryName(HelperPath)!,
                MCPHelperContract.RequestPayload(MCPHelperContract.Identity.CursorClient),
                TimeSpan.FromSeconds(4), TimeSpan.FromMilliseconds(200), 1_048_576);
            if (result.TimedOut || result.Status != 0) return false;
            _ = MCPHelperContract.Inspect(result.StdoutText, MCPHelperContract.Identity.CursorClient);
            return true;
        }
        catch (Exception exception) when (exception is global::AskKey.System.RestrictedProcessException
            or MCPHelperContract.MCPHelperContractException)
        {
            return false;
        }
    }

    internal bool ProbeBroker()
    {
        try
        {
            var client = new Broker.Transport.BrokerSocketClient(BrokerPipeName);
            var response = client.Send(new Broker.Protocol.BrokerRequest
            {
                Version = Broker.Protocol.BrokerProtocolVersion.Current,
                Method = "health",
            });
            return response.ResponseKind == Broker.Protocol.BrokerResponse.Kind.Success
                && response.Payload?.PayloadKind == Broker.Protocol.BrokerPayload.Kind.Health
                && response.Payload.Health?.Status == "ok";
        }
        catch
        {
            return false;
        }
    }

    private T WithBackupLock<T>(Func<T> body)
    {
        lock (ProcessBackupLock)
        {
            var directory = Path.GetDirectoryName(BackupLockPath)!;
            Directory.CreateDirectory(directory);
            using var stream = new FileStream(
                BackupLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return body();
        }
    }
}