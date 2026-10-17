using System.Text.Json;
using System.Text.Json.Nodes;

namespace AskKey.Integrations;

internal sealed record CodexOriginalConfig(string Text, int Mode);

internal sealed class CodexRollbackBackup
{
    public bool OriginalExisted { get; set; }
    public string OriginalText { get; set; } = "";
    public int OriginalMode { get; set; }
    public string? ReplacementDigest { get; set; }

    public CodexOriginalConfig? Original
        => OriginalExisted ? new CodexOriginalConfig(OriginalText, OriginalMode) : null;

    public byte[]? ReplacementDigestBytes
        => ReplacementDigest is null ? null : Convert.FromBase64String(ReplacementDigest);

    public static CodexRollbackBackup From(CodexOriginalConfig? original, byte[]? digest) => new()
    {
        OriginalExisted = original is not null,
        OriginalText = original?.Text ?? "",
        OriginalMode = original?.Mode ?? 0,
        ReplacementDigest = digest is null ? null : Convert.ToBase64String(digest),
    };

    public static CodexRollbackBackup Decode(byte[] data)
    {
        var node = JsonNode.Parse(data) as JsonObject
            ?? throw new CodexUserMCPException(CodexUserMCPError.RollbackFailed);
        // Swift's JSONEncoder wrote Data as base64 and snake-free camelCase keys.
        return new CodexRollbackBackup
        {
            OriginalExisted = node["originalExisted"]?.GetValue<bool>()
                ?? throw new CodexUserMCPException(CodexUserMCPError.RollbackFailed),
            OriginalText = node["originalText"]?.GetValue<string>()
                ?? throw new CodexUserMCPException(CodexUserMCPError.RollbackFailed),
            OriginalMode = node["originalMode"]?.GetValue<int>()
                ?? throw new CodexUserMCPException(CodexUserMCPError.RollbackFailed),
            ReplacementDigest = node["replacementDigest"]?.GetValue<string>(),
        };
    }

    public byte[] Encode()
    {
        var node = new JsonObject
        {
            ["originalExisted"] = OriginalExisted,
            ["originalText"] = OriginalText,
            ["originalMode"] = OriginalMode,
        };
        if (ReplacementDigest is not null) node["replacementDigest"] = ReplacementDigest;
        return JsonSerializer.SerializeToUtf8Bytes(node);
    }
}

/// <summary>
/// User-scope Codex MCP configuration. The Ask Key table is rewritten losslessly,
/// every other byte is preserved, and a journal backup makes the write reversible.
/// </summary>
public sealed class CodexUserMCPAdapter
{
    public const int MaximumConfigBytes = 1_048_576;

    private readonly object _mutationLock = new();

    public CodexUserMCPAdapter(
        string configPath,
        string helperPath,
        string backupDirectory,
        string brokerPipeName,
        CodexMCPCommand? command = null,
        CodexHelperSigning? signing = null,
        bool requiresCredentialDiscovery = false,
        string? brokerPipeFile = null)
    {
        ConfigPath = configPath;
        HelperPath = helperPath;
        BackupDirectory = backupDirectory;
        BrokerPipeName = brokerPipeName;
        BrokerPipeFile = brokerPipeFile ?? brokerPipeName;
        Command = command ?? CodexMCPCommand.Missing;
        Signing = signing ?? CodexHelperSigning.Executable;
        RequiresCredentialDiscovery = requiresCredentialDiscovery;
    }

    public string ConfigPath { get; }
    public string HelperPath { get; }
    public string BackupDirectory { get; }
    public string BrokerPipeName { get; }
    public string BrokerPipeFile { get; }
    public CodexMCPCommand Command { get; }
    public CodexHelperSigning Signing { get; }
    public bool RequiresCredentialDiscovery { get; }

    public CodexApplyLifecycle Lifecycle { get; } = new();

    private string BackupFilePath => Path.Combine(BackupDirectory, "config.toml");

    public CodexApplyResult Apply()
    {
        lock (_mutationLock)
        {
            AssertKnownCli();
            InspectConfigPath();
            AssertTrustedHelper();
            var original = ReadConfig();
            var diff = Preview();
            PrepareBackup(original);
            byte[]? replacementDigest = null;
            try
            {
                Lifecycle.AfterBackup();
                var replacement = WriteDesired(original);
                replacementDigest = global::System.Security.Cryptography.SHA256.HashData(replacement);
                UpdateBackupReplacementDigest(replacementDigest);
                Lifecycle.AfterWrite();
                ReadBackAskKey();
                VerifyConnection();
                RemoveBackupFile();
                return new CodexApplyResult(CodexConnectionStatus.Connected, diff);
            }
            catch (Exception failure)
            {
                try
                {
                    Lifecycle.BeforeRestore();
                    Restore(original, replacementDigest);
                    RemoveBackupFile();
                }
                catch
                {
                    throw new CodexUserMCPException(CodexUserMCPError.RollbackFailed);
                }
                if (failure is CodexUserMCPException typed) throw typed;
                throw CodexUserMCPException.ConnectionFailed("write");
            }
        }
    }

    public bool HasConfiguration()
    {
        InspectConfigPath();
        return CodexAskKeyTOML.HasServer(ReadConfig()?.Text ?? "", CodexUserMCP.ServerName);
    }

    public CodexConfigDiff Preview()
    {
        AssertKnownCli();
        InspectConfigPath();
        var original = ReadConfig()?.Text ?? "";
        var after = CodexAskKeyTOML.Upsert(original, HelperPath, new[] { "mcp" });
        return new CodexConfigDiff(original, after);
    }

    public CodexConnectionStatus Status()
    {
        try
        {
            InspectConfigPath();
            ReadBackAskKey();
            VerifyConnection();
            return CodexConnectionStatus.Connected;
        }
        catch
        {
            return CodexConnectionStatus.NotConnected;
        }
    }

    private byte[] WriteDesired(CodexOriginalConfig? original)
    {
        var cli = Command.Status();
        if (cli.Kind == CodexMCPCLIStatusKind.Supported)
        {
            RunOfficialCliInIsolation(original);
        }
        var next = CodexAskKeyTOML.Upsert(original?.Text ?? "", HelperPath, new[] { "mcp" });
        var bytes = global::System.Text.Encoding.UTF8.GetBytes(next);
        ReplaceIfUnchanged(original, bytes);
        return bytes;
    }

    private void ReplaceIfUnchanged(CodexOriginalConfig? original, byte[] replacement)
    {
        if (original is null)
        {
            AtomicWrite(replacement, WindowsFileSecurity.PrivateMode, exclusive: true);
            return;
        }
        var quarantine = Path.Combine(
            Path.GetDirectoryName(ConfigPath)!, $".askkey-input-{Guid.NewGuid()}");
        RenameExclusively(ConfigPath, quarantine);
        var snapshot = ReadRegularFile(quarantine);
        if (snapshot is null
            || !snapshot.Value.Bytes.AsSpan().SequenceEqual(global::System.Text.Encoding.UTF8.GetBytes(original.Text))
            || snapshot.Value.Mode != original.Mode)
        {
            RenameExclusively(quarantine, ConfigPath);
            throw new CodexUserMCPException(CodexUserMCPError.RollbackFailed);
        }
        try
        {
            AtomicWrite(replacement, original.Mode, exclusive: true);
            File.Delete(quarantine);
        }
        catch
        {
            if (File.Exists(quarantine) && !File.Exists(ConfigPath))
            {
                RenameExclusively(quarantine, ConfigPath);
            }
            throw new CodexUserMCPException(CodexUserMCPError.RollbackFailed);
        }
    }

    private void PrepareBackup(CodexOriginalConfig? original)
    {
        EnsureDirectory(BackupDirectory, WindowsFileSecurity.PrivateMode, excludeFromBackup: true);
        if (File.Exists(BackupFilePath))
        {
            var existing = PendingBackup();
            if (existing.ReplacementDigest is null)
            {
                if (!BackupMatchesOriginal(existing, original))
                {
                    throw new CodexUserMCPException(CodexUserMCPError.RollbackFailed);
                }
                return;
            }
            if (BackupMatchesCurrent(existing, original)) return;
        }
        WriteBackup(CodexRollbackBackup.From(original, null));
    }

    private static bool BackupMatchesCurrent(CodexRollbackBackup backup, CodexOriginalConfig? current)
    {
        if (BackupMatchesOriginal(backup, current)) return true;
        if (current is null || backup.ReplacementDigestBytes is null)
        {
            return !backup.OriginalExisted && current is null;
        }
        var expectedMode = backup.OriginalExisted ? backup.OriginalMode : WindowsFileSecurity.PrivateMode;
        var digest = global::System.Security.Cryptography.SHA256.HashData(
            global::System.Text.Encoding.UTF8.GetBytes(current.Text));
        return current.Mode == expectedMode && digest.AsSpan().SequenceEqual(backup.ReplacementDigestBytes);
    }

    private static bool BackupMatchesOriginal(CodexRollbackBackup backup, CodexOriginalConfig? current)
    {
        if (!backup.OriginalExisted) return current is null;
        return current is not null
            && current.Text == backup.OriginalText
            && current.Mode == backup.OriginalMode;
    }

    private void UpdateBackupReplacementDigest(byte[]? digest)
    {
        if (!File.Exists(BackupFilePath)) return;
        var backup = PendingBackup();
        backup.ReplacementDigest = digest is null ? null : Convert.ToBase64String(digest);
        WriteBackup(backup);
    }

    private void WriteBackup(CodexRollbackBackup backup)
    {
        var bytes = backup.Encode();
        var temporary = BackupFilePath + ".tmp-" + Guid.NewGuid();
        File.WriteAllBytes(temporary, bytes);
        WindowsFileSecurity.SecureFile(temporary);
        File.Move(temporary, BackupFilePath, overwrite: true);
        WindowsFileSecurity.SecureFile(BackupFilePath);
    }

    private void RemoveBackupFile()
    {
        if (File.Exists(BackupFilePath)) File.Delete(BackupFilePath);
    }

    private void Restore(CodexOriginalConfig? original, byte[]? replacementDigest)
    {
        var pending = PendingBackup();
        var target = pending.Original ?? original;
        if (ReadRegularFile(ConfigPath) is null)
        {
            if (replacementDigest is not null)
            {
                throw new CodexUserMCPException(CodexUserMCPError.RollbackFailed);
            }
            if (target is not null)
            {
                AtomicWrite(global::System.Text.Encoding.UTF8.GetBytes(target.Text), target.Mode, exclusive: true);
            }
            return;
        }
        var quarantine = Path.Combine(
            Path.GetDirectoryName(ConfigPath)!, $".askkey-rollback-{Guid.NewGuid()}");
        RenameExclusively(ConfigPath, quarantine);
        var quarantined = ReadRegularFile(quarantine)
            ?? throw new CodexUserMCPException(CodexUserMCPError.RollbackFailed);
        var targetBytes = target is null ? null : global::System.Text.Encoding.UTF8.GetBytes(target.Text);
        var digest = global::System.Security.Cryptography.SHA256.HashData(quarantined.Bytes);
        if ((targetBytes is not null && quarantined.Bytes.AsSpan().SequenceEqual(targetBytes))
            || (replacementDigest is not null && digest.AsSpan().SequenceEqual(replacementDigest)))
        {
            try
            {
                if (target is not null && targetBytes is not null)
                {
                    AtomicWrite(targetBytes, target.Mode, exclusive: true);
                }
                File.Delete(quarantine);
            }
            catch
            {
                if (File.Exists(quarantine) && !File.Exists(ConfigPath))
                {
                    RenameExclusively(quarantine, ConfigPath);
                }
                throw new CodexUserMCPException(CodexUserMCPError.RollbackFailed);
            }
            return;
        }
        RenameExclusively(quarantine, ConfigPath);
        throw new CodexUserMCPException(CodexUserMCPError.RollbackFailed);
    }

    private CodexRollbackBackup PendingBackup()
    {
        var file = ReadRegularFile(BackupFilePath)
            ?? throw new CodexUserMCPException(CodexUserMCPError.RollbackFailed);
        try
        {
            return CodexRollbackBackup.Decode(file.Bytes);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
        {
            throw new CodexUserMCPException(CodexUserMCPError.RollbackFailed);
        }
    }

    private void RunOfficialCliInIsolation(CodexOriginalConfig? original)
    {
        var directory = Path.Combine(BackupDirectory, $"official-cli-{Guid.NewGuid()}");
        var isolatedConfig = Path.Combine(directory, "config.toml");
        try
        {
            EnsureDirectory(directory, WindowsFileSecurity.PrivateMode, excludeFromBackup: true);
            if (original is not null)
            {
                File.WriteAllBytes(isolatedConfig, global::System.Text.Encoding.UTF8.GetBytes(original.Text));
                WindowsFileSecurity.SecureFile(isolatedConfig);
            }
            Command.AddAskKey(HelperPath, isolatedConfig);
            Directory.Delete(directory, recursive: true);
        }
        catch
        {
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
            catch
            {
                throw new CodexUserMCPException(CodexUserMCPError.RollbackFailed);
            }
            throw;
        }
    }

    internal void InspectConfigPath()
    {
        RejectUnsafeNode(ConfigPath, allowMissing: true, requireRegularFile: true);
        RejectUnsafeNode(Path.GetDirectoryName(ConfigPath)!, allowMissing: true, requireRegularFile: false);
    }

    private static void RejectUnsafeNode(string path, bool allowMissing, bool requireRegularFile)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            if (allowMissing) return;
            throw new CodexUserMCPException(CodexUserMCPError.UnsafeConfigFile);
        }
        if (WindowsFileSecurity.IsLinkOrReparse(path))
        {
            throw new CodexUserMCPException(CodexUserMCPError.UnsafeConfigFile);
        }
        if (requireRegularFile && !File.Exists(path))
        {
            throw new CodexUserMCPException(CodexUserMCPError.UnsafeConfigFile);
        }
        if (!requireRegularFile && !Directory.Exists(path))
        {
            throw new CodexUserMCPException(CodexUserMCPError.UnsafeConfigFile);
        }
    }

    internal CodexOriginalConfig? ReadConfig()
    {
        InspectConfigPath();
        var data = ReadRegularFile(ConfigPath);
        if (data is null) return null;
        if (data.Value.Bytes.Length > MaximumConfigBytes || data.Value.Bytes.Contains((byte)0))
        {
            throw new CodexUserMCPException(CodexUserMCPError.IllegalConfig);
        }
        string text;
        try { text = new global::System.Text.UTF8Encoding(false, true).GetString(data.Value.Bytes); }
        catch (global::System.Text.DecoderFallbackException)
        {
            throw new CodexUserMCPException(CodexUserMCPError.IllegalConfig);
        }
        CodexAskKeyTOML.Validate(text);
        return new CodexOriginalConfig(text, data.Value.Mode);
    }

    internal void AtomicWrite(byte[] data, int mode, bool exclusive = false)
    {
        EnsureDirectory(Path.GetDirectoryName(ConfigPath)!, WindowsFileSecurity.PrivateMode, excludeFromBackup: false);
        try
        {
            ClientConfigFileIO.PublishAtomically(
                data, ConfigPath, mode, exclusive, ".askkey-");
        }
        catch (ClientConfigFileException)
        {
            throw CodexUserMCPException.ConnectionFailed("write");
        }
    }

    internal void RenameExclusively(string source, string destination)
    {
        try
        {
            ClientConfigFileIO.RenameExclusively(source, destination);
        }
        catch (ClientConfigFileException)
        {
            throw new CodexUserMCPException(CodexUserMCPError.RollbackFailed);
        }
    }

    internal static void EnsureDirectory(string path, int mode, bool excludeFromBackup)
    {
        if (Directory.Exists(path))
        {
            if (WindowsFileSecurity.IsLinkOrReparse(path))
            {
                throw new CodexUserMCPException(CodexUserMCPError.UnsafeConfigFile);
            }
            if (excludeFromBackup) WindowsFileSecurity.ApplyMode(path, mode);
            return;
        }
        if (File.Exists(path)) throw new CodexUserMCPException(CodexUserMCPError.UnsafeConfigFile);
        Directory.CreateDirectory(path);
        WindowsFileSecurity.ApplyMode(path, mode);
    }

    internal static RegularFile? ReadRegularFile(string path)
    {
        try
        {
            return ClientConfigFileIO.ReadRegularFile(path, MaximumConfigBytes);
        }
        catch (ClientConfigFileException exception) when (exception.Failure == ClientConfigFileFailure.NotFound)
        {
            return null;
        }
        catch (ClientConfigFileException exception) when (exception.Failure == ClientConfigFileFailure.TooLarge)
        {
            throw new CodexUserMCPException(CodexUserMCPError.IllegalConfig);
        }
        catch (ClientConfigFileException)
        {
            throw new CodexUserMCPException(CodexUserMCPError.UnsafeConfigFile);
        }
    }

    private void AssertKnownCli()
    {
        var status = Command.Status();
        switch (status.Kind)
        {
            case CodexMCPCLIStatusKind.Missing:
                return;
            case CodexMCPCLIStatusKind.Unknown:
                throw new CodexUserMCPException(CodexUserMCPError.UnknownCodexVersion);
            default:
                if (!CodexUserMCP.AllowsOfficialCLI(status.Version!))
                {
                    throw new CodexUserMCPException(CodexUserMCPError.UnknownCodexVersion);
                }
                return;
        }
    }

    internal void ReadBackAskKey()
    {
        var current = ReadConfig()?.Text;
        if (current is null || !HasExpectedAskKey(current))
        {
            throw CodexUserMCPException.ConnectionFailed("config");
        }
    }

    private bool HasExpectedAskKey(string text)
    {
        var entry = CodexAskKeyTOML.AskKey(text);
        return entry is { } value
            && value.Enabled
            && value.Command == HelperPath
            && value.Args.Length == 1
            && value.Args[0] == "mcp";
    }

    internal void AssertTrustedHelper()
    {
        if (!Signing.IsTrusted(HelperPath) || !HelperTrust.IsExecutableRegularFile(HelperPath))
        {
            throw CodexUserMCPException.ConnectionFailed("helper");
        }
    }

    internal void VerifyConnection()
    {
        AssertTrustedHelper();
        try
        {
            var client = new Broker.Transport.BrokerSocketClient(BrokerPipeName);
            var health = client.Send(new Broker.Protocol.BrokerRequest
            {
                Version = Broker.Protocol.BrokerProtocolVersion.Current,
                Method = "health",
            });
            if (health.ResponseKind != Broker.Protocol.BrokerResponse.Kind.Success
                || health.Payload?.PayloadKind != Broker.Protocol.BrokerPayload.Kind.Health
                || health.Payload.Health?.Status != "ok")
            {
                throw CodexUserMCPException.ConnectionFailed("broker");
            }
            var version = client.Send(new Broker.Protocol.BrokerRequest
            {
                Version = Broker.Protocol.BrokerProtocolVersion.Current,
                Method = "version",
            });
            if (version.ResponseKind != Broker.Protocol.BrokerResponse.Kind.Success
                || version.Payload?.PayloadKind != Broker.Protocol.BrokerPayload.Kind.Version
                || version.Payload.Version?.ProtocolVersion != Broker.Protocol.BrokerProtocolVersion.Current)
            {
                throw CodexUserMCPException.ConnectionFailed("version");
            }
        }
        catch (CodexUserMCPException)
        {
            throw;
        }
        catch (Exception)
        {
            throw CodexUserMCPException.ConnectionFailed("broker");
        }
        VerifyHelperMcp();
        if (RequiresCredentialDiscovery) VerifyCommandDiscoveryHelper();
    }

    public void VerifyCommandDiscoveryHelper()
    {
        AssertTrustedHelper();
        var response = IntegrationProcess.RunStrict(
            HelperPath, new[] { "hook", "capabilities" }, IntegrationProcess.PortableEnvironment());
        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("protocolVersion", out var protocol)
                || protocol.ValueKind != JsonValueKind.Number
                || !protocol.TryGetInt32(out var protocolVersion)
                || protocolVersion != 1
                || !root.TryGetProperty("clients", out var clients)
                || clients.ValueKind != JsonValueKind.Array
                || !clients.EnumerateArray().Any(item =>
                    item.ValueKind == JsonValueKind.String && item.GetString() == "codex"))
            {
                throw CodexUserMCPException.ConnectionFailed("helper");
            }
        }
        catch (JsonException)
        {
            throw CodexUserMCPException.ConnectionFailed("helper");
        }
    }

    private void VerifyHelperMcp()
    {
        var identity = MCPHelperContract.Identity.AskKeyHelper;
        var environment = IntegrationProcess.PortableEnvironment();
        environment["ASKKEY_BROKER_PIPE"] = BrokerPipeFile;
        string response;
        try
        {
            response = IntegrationProcess.RunStrict(
                HelperPath, new[] { "mcp" }, environment, MCPHelperContract.RequestPayload(identity));
        }
        catch (CodexUserMCPException)
        {
            throw CodexUserMCPException.ConnectionFailed("protocol");
        }
        try
        {
            _ = MCPHelperContract.Inspect(response, identity);
        }
        catch (MCPHelperContract.MCPHelperContractException)
        {
            throw CodexUserMCPException.ConnectionFailed("protocol");
        }
    }
}
