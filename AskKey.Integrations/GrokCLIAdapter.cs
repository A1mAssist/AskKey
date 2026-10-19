using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AskKey.Integrations;

public enum GrokCLIAdapterError
{
    UnsafeConfig,
    InvalidConfig,
    UnsupportedClient,
    VerificationFailed,
    RollbackFailed,
    BackupCleanupFailed,
    DiagnosticsCleanupFailed,
}

public sealed class GrokCLIAdapterException : Exception
{
    public GrokCLIAdapterException(GrokCLIAdapterError error, string? detail = null)
        : base(Describe(error, detail))
    {
        Error = error;
        Detail = detail;
    }

    public GrokCLIAdapterError Error { get; }
    public string? Detail { get; }

    private static string Describe(GrokCLIAdapterError error, string? detail) => error switch
    {
        GrokCLIAdapterError.UnsafeConfig => "The Grok CLI config is not a regular user-level file.",
        GrokCLIAdapterError.InvalidConfig => "The Grok CLI config is not valid TOML.",
        GrokCLIAdapterError.UnsupportedClient =>
            "This Grok CLI build does not support user-scoped MCP management.",
        GrokCLIAdapterError.VerificationFailed => $"Grok CLI connection verification failed ({detail}).",
        GrokCLIAdapterError.RollbackFailed => "Ask Key could not restore the previous Grok CLI config.",
        GrokCLIAdapterError.BackupCleanupFailed =>
            "Ask Key connected Grok CLI but could not delete the rollback backup.",
        _ => "Ask Key could not remove the temporary Grok configuration probe.",
    };
}

public sealed record GrokCLIConnectResult(
    bool Connected,
    string Reason,
    string Diff,
    string ListJSON,
    string DoctorJSON,
    string HelperVersion);

internal sealed record GrokOriginalConfig(byte[]? Data, string? Text, int? Mode);

internal sealed class OutputCapture
{
    private readonly object _lock = new();
    private int _bytes;

    public int Bytes
    {
        get { lock (_lock) return _bytes; }
        set { lock (_lock) _bytes = value; }
    }
}

/// <summary>
/// Grok's user config lives at <c>~/.grok/config.toml</c>. The official CLI is used
/// only as an isolated preflight; the actual write is a lossless TOML edit so
/// unrelated tables survive byte-for-byte.
/// </summary>
public sealed class GrokCLIAdapter
{
    public const int CapturedOutputLimit = 1_048_576;

    private readonly OutputCapture _outputCapture = new();

    public GrokCLIAdapter(
        string grokHome,
        string isolatedHome,
        string helperExecutable,
        string grokExecutable,
        string backupDirectory,
        string brokerPipeName,
        CodexHelperSigning? signing = null,
        IReadOnlyDictionary<string, string>? helperEnvironment = null,
        string serverName = "askkey",
        Func<string>? makeDiagnosticsProbe = null,
        Action<string>? removeDiagnosticsProbe = null,
        string? brokerPipeFile = null)
    {
        GrokHome = Path.GetFullPath(grokHome);
        IsolatedHome = Path.GetFullPath(isolatedHome);
        HelperExecutable = helperExecutable;
        GrokExecutable = grokExecutable;
        BackupDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(backupDirectory));
        BrokerPipeName = brokerPipeName;
        BrokerPipeFile = brokerPipeFile ?? brokerPipeName;
        Signing = signing ?? CodexHelperSigning.Executable;
        HelperEnvironment = helperEnvironment is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(helperEnvironment, StringComparer.Ordinal);
        ServerName = serverName;
        MakeDiagnosticsProbe = makeDiagnosticsProbe
            ?? (() => Path.Combine(Path.GetTempPath(), $"AskKey-Grok-Probe-{Guid.NewGuid()}"));
        RemoveDiagnosticsProbe = removeDiagnosticsProbe ?? (path => Directory.Delete(path, recursive: true));
    }

    public string GrokHome { get; set; }
    public string IsolatedHome { get; }
    public string HelperExecutable { get; }
    public string GrokExecutable { get; }
    public string BackupDirectory { get; }
    public string BrokerPipeName { get; }
    public string BrokerPipeFile { get; }
    public CodexHelperSigning Signing { get; }
    public IReadOnlyDictionary<string, string> HelperEnvironment { get; }
    public string ServerName { get; }
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(12);
    public TimeSpan TerminationGrace { get; set; } = TimeSpan.FromSeconds(1);
    public Action<string>? RemoveBackupItem { get; set; }
    public Func<string> MakeDiagnosticsProbe { get; }
    public Action<string> RemoveDiagnosticsProbe { get; }
    public Action? BeforeRollback { get; set; }
    public Action? AfterReplacementWrite { get; set; }
    public Action? AfterOfficialPreflight { get; set; }
    public Action<string>? ObserveAtomicWriteTemporary { get; set; }

    public string ConfigPath => Path.Combine(GrokHome, "config.toml");

    private string BackupDataPath => Path.Combine(BackupDirectory, "grok-cli.config.toml");
    private string BackupStatePath => Path.Combine(BackupDirectory, "grok-cli.state");

    internal int LastCapturedOutputBytes => _outputCapture.Bytes;
    public bool HasConfiguration()
    {
        InspectConfigFile();
        var text = ReadOriginal().Text ?? "";
        _ = GrokUserTOML.Parse(text);
        try
        {
            return CodexAskKeyTOML.HasServer(text, ServerName);
        }
        catch
        {
            throw new GrokCLIAdapterException(GrokCLIAdapterError.InvalidConfig);
        }
    }

    public string Preview()
    {
        PrepareIsolatedHome();
        InspectConfigFile();
        var original = ReadOriginal().Text ?? "";
        ValidateUserToml(original);
        var desired = GrokUserTOML.UpsertStdio(
            original, ServerName, HelperExecutable, new[] { "mcp" }, HelperEnvironment);
        return GrokUserTOML.RedactedDiff(original, desired);
    }

    public GrokCLIConnectResult Connect()
    {
        PrepareIsolatedHome();
        InspectConfigFile();
        var original = ReadOriginal();
        if (original.Text is { } text) ValidateUserToml(text);
        WriteBackup(original);
        GrokCLIConnectResult result;
        byte[]? appliedDigest = null;
        try
        {
            ApplyStdioConfig(original.Text, digest => appliedDigest = digest);
            var verified = Verify(original.Text ?? "");
            if (!verified.Connected)
            {
                throw new GrokCLIAdapterException(GrokCLIAdapterError.VerificationFailed, verified.Reason);
            }
            result = verified;
        }
        catch (Exception error)
        {
            if (appliedDigest is not null)
            {
                BeforeRollback?.Invoke();
                RestoreBackup(original, appliedDigest);
            }
            else
            {
                ClearBackup();
            }
            if (error is GrokCLIAdapterException) throw;
            throw new GrokCLIAdapterException(GrokCLIAdapterError.VerificationFailed, error.Message);
        }
        ClearBackup();
        return result;
    }

    public GrokCLIConnectResult Status()
    {
        PrepareIsolatedHome();
        InspectConfigFile();
        var original = ReadOriginal();
        if (original.Text is { } text)
        {
            ValidateUserToml(text);
            if (GrokUserTOML.AskKeyTransport(text, ServerName).Kind == GrokTomlTransportKind.Remote)
            {
                return new GrokCLIConnectResult(false, "remote_connector", "", "", "", "");
            }
        }
        return Verify(original.Text ?? "");
    }

    private void WriteBackup(GrokOriginalConfig original)
    {
        WindowsPathSafety.EnsureDirectory(
            BackupDirectory, CommandDiscoveryHookConfigurationError.UnsafeBackupDirectory);
        WindowsFileSecurity.ApplyMode(BackupDirectory, WindowsFileSecurity.PrivateMode);
        if (original.Data is { } data && original.Mode is { } mode)
        {
            ClientConfigFileIO.PublishAtomically(
                data, BackupDataPath, WindowsFileSecurity.PrivateMode, exclusive: false, ".askkey-grok-backup-");
            ClientConfigFileIO.PublishAtomically(
                Encoding.UTF8.GetBytes($"mode={mode}"), BackupStatePath,
                WindowsFileSecurity.PrivateMode, exclusive: false, ".askkey-grok-backup-");
        }
        else
        {
            try { File.Delete(BackupDataPath); } catch (IOException) { }
            ClientConfigFileIO.PublishAtomically(
                "missing"u8.ToArray(), BackupStatePath,
                WindowsFileSecurity.PrivateMode, exclusive: false, ".askkey-grok-backup-");
        }
    }

    private void ClearBackup()
    {
        try
        {
            if (File.Exists(BackupDataPath)) (RemoveBackupItem ?? File.Delete)(BackupDataPath);
            if (File.Exists(BackupStatePath)) (RemoveBackupItem ?? File.Delete)(BackupStatePath);
        }
        catch
        {
            throw new GrokCLIAdapterException(GrokCLIAdapterError.BackupCleanupFailed);
        }
    }

    private void RestoreBackup(GrokOriginalConfig original, byte[]? expectedDigest = null)
    {
        try
        {
            if (expectedDigest is not null)
            {
                var quarantine = Path.Combine(
                    Path.GetDirectoryName(ConfigPath)!, $".askkey-rollback-{Guid.NewGuid()}");
                RenameExclusively(ConfigPath, quarantine);
                var quarantined = ClientConfigFileIO.ReadRegularFile(quarantine).Bytes;
                if (!SHA256.HashData(quarantined).AsSpan().SequenceEqual(expectedDigest))
                {
                    RenameExclusively(quarantine, ConfigPath);
                    throw new GrokCLIAdapterException(GrokCLIAdapterError.RollbackFailed);
                }
                try
                {
                    if (original.Data is { } data && original.Mode is { } mode)
                    {
                        AtomicWrite(data, ConfigPath, mode, exclusive: true);
                    }
                    File.Delete(quarantine);
                }
                catch
                {
                    if (File.Exists(quarantine) && !File.Exists(ConfigPath))
                    {
                        RenameExclusively(quarantine, ConfigPath);
                    }
                    throw new GrokCLIAdapterException(GrokCLIAdapterError.RollbackFailed);
                }
            }
            else if (original.Data is { } data && original.Mode is { } mode)
            {
                AtomicWrite(data, ConfigPath, mode);
            }
            else if (File.Exists(ConfigPath))
            {
                File.Delete(ConfigPath);
            }
            ClearBackup();
        }
        catch (GrokCLIAdapterException)
        {
            throw;
        }
        catch
        {
            throw new GrokCLIAdapterException(GrokCLIAdapterError.RollbackFailed);
        }
    }

    private void ApplyStdioConfig(string? originalText, Action<byte[]?> setDigest)
    {
        if (OfficialGrokIsUnsupported())
        {
            throw new GrokCLIAdapterException(GrokCLIAdapterError.UnsupportedClient);
        }
        WindowsPathSafety.EnsureDirectory(
            GrokHome, CommandDiscoveryHookConfigurationError.UnsafeBackupDirectory);
        WindowsFileSecurity.ApplyMode(GrokHome, WindowsFileSecurity.PrivateMode);
        var preservedMode = ReadOriginal().Mode ?? WindowsFileSecurity.PrivateMode;
        if (CanUseOfficialGrok())
        {
            RunOfficialCliInIsolation(originalText);
        }
        AfterOfficialPreflight?.Invoke();
        WriteLosslessStdio(originalText, preservedMode, setDigest);
        ReadBackStdio();
    }

    private bool OfficialGrokIsUnsupported()
    {
        if (!HelperTrust.IsExecutableRegularFile(GrokExecutable)) return false;
        RunResult help;
        try { help = RunGrok(new[] { "mcp", "add", "--help" }); }
        catch { return false; }
        return help.Status == 0
            && !(help.StdoutText + help.StderrText).Contains("--scope", StringComparison.Ordinal);
    }

    private void RunOfficialCliInIsolation(string? originalText)
    {
        var home = Path.Combine(BackupDirectory, $"official-grok-{Guid.NewGuid()}");
        var config = Path.Combine(home, "config.toml");
        try
        {
            WindowsPathSafety.EnsureDirectory(
                home, CommandDiscoveryHookConfigurationError.UnsafeBackupDirectory);
            if (originalText is not null)
            {
                File.WriteAllBytes(config, Encoding.UTF8.GetBytes(originalText));
                WindowsFileSecurity.SecureFile(config);
            }
            var isolated = new GrokCLIAdapter(
                home, IsolatedHome, HelperExecutable, GrokExecutable, BackupDirectory, BrokerPipeName,
                Signing, HelperEnvironment, ServerName, MakeDiagnosticsProbe, RemoveDiagnosticsProbe,
                BrokerPipeFile);
            try { isolated.AddWithOfficialCli(); }
            catch { /* Official preflight is advisory; the lossless writer is authoritative. */ }
            Directory.Delete(home, recursive: true);
        }
        catch
        {
            try
            {
                if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
            }
            catch
            {
                throw new GrokCLIAdapterException(GrokCLIAdapterError.RollbackFailed);
            }
            throw;
        }
    }

    private void AddWithOfficialCli()
    {
        var arguments = new List<string> { "mcp", "add", "--scope", "user" };
        foreach (var (key, value) in HelperEnvironment.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            arguments.Add("-e");
            arguments.Add($"{key}={value}");
        }
        arguments.Add(ServerName);
        arguments.Add("--");
        arguments.Add(HelperExecutable);
        arguments.Add("mcp");
        var result = RunGrok(arguments);
        if (result.Status != 0)
        {
            throw new GrokCLIAdapterException(GrokCLIAdapterError.VerificationFailed, "official_add");
        }
    }

    private void WriteLosslessStdio(string? originalText, int mode, Action<byte[]?> setDigest)
    {
        var next = GrokUserTOML.UpsertStdio(
            originalText ?? "", ServerName, HelperExecutable, new[] { "mcp" }, HelperEnvironment);
        var replacement = Encoding.UTF8.GetBytes(next);
        setDigest(SHA256.HashData(replacement));
        ReplaceIfUnchanged(originalText, replacement, mode);
        AfterReplacementWrite?.Invoke();
    }

    private void ReplaceIfUnchanged(string? originalText, byte[] replacement, int mode)
    {
        if (originalText is null)
        {
            AtomicWrite(replacement, ConfigPath, mode, exclusive: true);
            return;
        }
        var quarantine = Path.Combine(
            Path.GetDirectoryName(ConfigPath)!, $".askkey-input-{Guid.NewGuid()}");
        RenameExclusively(ConfigPath, quarantine);
        var current = ClientConfigFileIO.ReadRegularFile(quarantine);
        if (!current.Bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(originalText))
            || current.Mode != mode)
        {
            RenameExclusively(quarantine, ConfigPath);
            throw new GrokCLIAdapterException(GrokCLIAdapterError.RollbackFailed);
        }
        try
        {
            AtomicWrite(replacement, ConfigPath, mode, exclusive: true);
            File.Delete(quarantine);
        }
        catch
        {
            if (File.Exists(quarantine) && !File.Exists(ConfigPath))
            {
                RenameExclusively(quarantine, ConfigPath);
            }
            throw new GrokCLIAdapterException(GrokCLIAdapterError.RollbackFailed);
        }
    }

    private void ReadBackStdio()
    {
        InspectConfigFile();
        var text = File.ReadAllText(ConfigPath, Encoding.UTF8);
        if (!GrokUserTOML.AskKeyTransport(text, ServerName)
                .IsStdio(HelperExecutable, new[] { "mcp" }))
        {
            throw new GrokCLIAdapterException(GrokCLIAdapterError.VerificationFailed, "readback");
        }
    }

    private void RenameExclusively(string source, string destination)
    {
        try
        {
            ClientConfigFileIO.RenameExclusively(source, destination);
        }
        catch (ClientConfigFileException)
        {
            throw new GrokCLIAdapterException(GrokCLIAdapterError.RollbackFailed);
        }
    }
    private void PrepareIsolatedHome()
    {
        WindowsPathSafety.EnsureDirectory(
            IsolatedHome, CommandDiscoveryHookConfigurationError.UnsafeBackupDirectory);
        WindowsFileSecurity.ApplyMode(IsolatedHome, WindowsFileSecurity.PrivateMode);
    }

    private void InspectConfigFile()
    {
        try
        {
            _ = ClientConfigFileIO.InspectRegularFile(ConfigPath);
        }
        catch (ClientConfigFileException)
        {
            throw new GrokCLIAdapterException(GrokCLIAdapterError.UnsafeConfig);
        }
    }

    internal GrokOriginalConfig ReadOriginal()
    {
        try
        {
            var file = ClientConfigFileIO.ReadRegularFile(ConfigPath, CodexUserMCPAdapter.MaximumConfigBytes);
            string text;
            try { text = new UTF8Encoding(false, true).GetString(file.Bytes); }
            catch (DecoderFallbackException) { throw new GrokCLIAdapterException(GrokCLIAdapterError.InvalidConfig); }
            return new GrokOriginalConfig(file.Bytes, text, file.Mode);
        }
        catch (ClientConfigFileException exception) when (exception.Failure == ClientConfigFileFailure.NotFound)
        {
            return new GrokOriginalConfig(null, null, null);
        }
        catch (ClientConfigFileException exception) when (
            exception.Failure is ClientConfigFileFailure.TooLarge or ClientConfigFileFailure.Unsafe)
        {
            throw new GrokCLIAdapterException(GrokCLIAdapterError.UnsafeConfig);
        }
    }

    private void ValidateUserToml(string text)
    {
        if (CanUseOfficialGrok())
        {
            var combined = GrokTomlDiagnostics(text);
            if (combined.Contains("syntax errors", StringComparison.OrdinalIgnoreCase)
                || combined.Contains("TOML parse", StringComparison.OrdinalIgnoreCase))
            {
                throw new GrokCLIAdapterException(GrokCLIAdapterError.InvalidConfig);
            }
        }
        _ = GrokUserTOML.Parse(text);
    }

    internal bool CanUseOfficialGrok()
    {
        if (!HelperTrust.IsExecutableRegularFile(GrokExecutable)) return false;
        RunResult help;
        try { help = RunGrok(new[] { "mcp", "add", "--help" }); }
        catch { return false; }
        if (help.Status != 0) return false;
        return (help.StdoutText + help.StderrText).Contains("--scope", StringComparison.Ordinal);
    }

    private string GrokTomlDiagnostics(string text)
    {
        var probe = MakeDiagnosticsProbe();
        try
        {
            WindowsPathSafety.EnsureDirectory(
                probe, CommandDiscoveryHookConfigurationError.UnsafeBackupDirectory);
            var config = Path.Combine(probe, "config.toml");
            File.WriteAllText(config, text, new UTF8Encoding(false));
            WindowsFileSecurity.SecureFile(config);
            var output = RunGrok(new[] { "mcp", "list", "--json" }, probe);
            var diagnostics = output.StdoutText + output.StderrText;
            try { RemoveDiagnosticsProbe(probe); }
            catch { throw new GrokCLIAdapterException(GrokCLIAdapterError.DiagnosticsCleanupFailed); }
            return diagnostics;
        }
        catch (GrokCLIAdapterException) { throw; }
        catch
        {
            try { RemoveDiagnosticsProbe(probe); }
            catch { throw new GrokCLIAdapterException(GrokCLIAdapterError.DiagnosticsCleanupFailed); }
            throw;
        }
    }

    internal Dictionary<string, string> HelperProcessEnvironment()
    {
        var environment = IntegrationProcess.CurrentEnvironment();
        environment["ASKKEY_BROKER_PIPE"] = BrokerPipeFile;
        foreach (var (key, value) in HelperEnvironment) environment[key] = value;
        return environment;
    }

    private Dictionary<string, string> GrokEnvironment(string? grokHomeOverride)
    {
        var environment = IntegrationProcess.CurrentEnvironment();
        environment["HOME"] = IsolatedHome;
        environment["USERPROFILE"] = IsolatedHome;
        environment["GROK_HOME"] = grokHomeOverride ?? GrokHome;
        environment["GROK_CURSOR_MCPS_ENABLED"] = "0";
        environment["GROK_CLAUDE_MCPS_ENABLED"] = "0";
        environment.Remove("GROK_CONFIG");
        environment.Remove("GROK_CONFIG_PATH");
        environment.Remove("ASKKEY_BROKER_PIPE");
        return environment;
    }

    internal RunResult RunGrok(
        IReadOnlyList<string> arguments, string? grokHomeOverride = null, TimeSpan? timeout = null)
        => RunCapturedProcess(
            GrokExecutable, arguments, GrokEnvironment(grokHomeOverride), IsolatedHome, null,
            timeout ?? CommandTimeout);

    internal RunResult RunCapturedProcess(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        string currentDirectory,
        byte[]? input,
        TimeSpan timeout)
    {
        var result = IntegrationProcess.RunCaptured(
            executable, arguments, environment, currentDirectory, input, timeout, TerminationGrace,
            CapturedOutputLimit);
        _outputCapture.Bytes = result.Stdout.Length + result.Stderr.Length;
        if (result.TimedOut)
        {
            throw new GrokCLIAdapterException(GrokCLIAdapterError.VerificationFailed, "timeout");
        }
        return result;
    }

    internal GrokCLIConnectResult Verify(string before)
    {
        var after = File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath, Encoding.UTF8) : "";
        var diff = GrokUserTOML.RedactedDiff(before, after);
        var transport = GrokUserTOML.AskKeyTransport(after, ServerName);
        if (transport.Kind == GrokTomlTransportKind.Remote)
        {
            return new GrokCLIConnectResult(false, "remote_connector", diff, "", "", "");
        }
        if (!transport.IsStdio(HelperExecutable, new[] { "mcp" }))
        {
            return new GrokCLIConnectResult(false, "not_configured", diff, "", "", "");
        }
        if (!CanUseOfficialGrok())
        {
            return new GrokCLIConnectResult(false, "list_unavailable", diff, "", "", "");
        }
        var listed = RunGrok(new[] { "mcp", "list", "--json" });
        var listJson = ExtractJson(listed.StdoutText);
        if (listed.Status != 0 || !ListMatches(listJson))
        {
            return new GrokCLIConnectResult(false, "list_mismatch", diff, listJson, "", "");
        }
        var doctor = RunGrok(new[] { "mcp", "doctor", "--json", ServerName });
        var doctorJson = ExtractJson(doctor.StdoutText);
        if (doctor.Status != 0 || !DoctorHealthy(doctorJson))
        {
            return new GrokCLIConnectResult(false, "doctor_unhealthy", diff, listJson, doctorJson, "");
        }
        if (!Signing.IsTrusted(HelperExecutable))
        {
            return new GrokCLIConnectResult(false, "helper_signature", diff, listJson, doctorJson, "");
        }
        MCPHelperContract.Inspection helper;
        try { helper = InspectHelper(); }
        catch (MCPHelperContract.MCPHelperContractException)
        {
            return new GrokCLIConnectResult(false, "helper_initialize", diff, listJson, doctorJson, "");
        }
        if (helper.Version != AskKeyVersion.Current)
        {
            return new GrokCLIConnectResult(
                false, "helper_version", diff, listJson, doctorJson, helper.Version);
        }
        if (!helper.Tools.Contains("list_credentials") || !helper.Tools.Contains("run"))
        {
            return new GrokCLIConnectResult(
                false, "helper_tools", diff, listJson, doctorJson, helper.Version);
        }
        if (!BrokerIsHealthy())
        {
            return new GrokCLIConnectResult(
                false, "broker_unhealthy", diff, listJson, doctorJson, helper.Version);
        }
        return new GrokCLIConnectResult(true, "ok", diff, listJson, doctorJson, helper.Version);
    }

    private bool ListMatches(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return false;
            foreach (var server in document.RootElement.EnumerateArray())
            {
                if (server.ValueKind != JsonValueKind.Object
                    || !server.TryGetProperty("name", out var name)
                    || name.GetString() != ServerName
                    || (server.TryGetProperty("scope", out var scope)
                        && scope.ValueKind == JsonValueKind.String
                        && scope.GetString() == "project")
                    || server.TryGetProperty("url", out _))
                {
                    continue;
                }
                if (!server.TryGetProperty("command", out var command)
                    || command.GetString() != HelperExecutable
                    || !server.TryGetProperty("args", out var args)
                    || args.ValueKind != JsonValueKind.Array)
                {
                    return false;
                }
                var values = args.EnumerateArray()
                    .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
                    .ToArray();
                return values.Length == 1 && values[0] == "mcp";
            }
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private bool DoctorHealthy(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("servers", out var servers)
                || servers.ValueKind != JsonValueKind.Array)
            {
                return false;
            }
            foreach (var server in servers.EnumerateArray())
            {
                if (server.ValueKind != JsonValueKind.Object
                    || !server.TryGetProperty("name", out var name)
                    || name.GetString() != ServerName)
                {
                    continue;
                }
                if (!server.TryGetProperty("transport", out var transport)
                    || transport.GetString() != "stdio")
                {
                    return false;
                }
                if (server.TryGetProperty("target", out var target)
                    && target.ValueKind == JsonValueKind.String
                    && target.GetString()!.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                return server.TryGetProperty("healthy", out var healthy)
                    && healthy.ValueKind == JsonValueKind.True;
            }
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private MCPHelperContract.Inspection InspectHelper()
    {
        var ran = RunCapturedProcess(
            HelperExecutable, new[] { "mcp" }, HelperProcessEnvironment(), IsolatedHome,
            MCPHelperContract.RequestPayload(MCPHelperContract.Identity.GrokClient), CommandTimeout);
        if (ran.Status != 0)
        {
            throw new GrokCLIAdapterException(GrokCLIAdapterError.VerificationFailed, "helper_initialize");
        }
        return MCPHelperContract.Inspect(ran.StdoutText, MCPHelperContract.Identity.GrokClient);
    }

    private bool BrokerIsHealthy()
    {
        var ran = RunCapturedProcess(
            HelperExecutable, new[] { "health" }, HelperProcessEnvironment(), IsolatedHome,
            null, CommandTimeout);
        if (ran.Status != 0) return false;
        try
        {
            var response = Broker.Protocol.BrokerJson.DecodeResponse(ran.Stdout);
            return response?.ResponseKind == Broker.Protocol.BrokerResponse.Kind.Success
                && response.Payload?.PayloadKind == Broker.Protocol.BrokerPayload.Kind.Health
                && response.Payload.Health?.Status == "ok"
                && response.Payload.Health.Version == Broker.Protocol.BrokerProtocolVersion.Current;
        }
        catch
        {
            return false;
        }
    }

    internal static string ExtractJson(string raw)
    {
        var trimmed = raw.Trim();
        var objectStart = trimmed.IndexOf('{');
        var arrayStart = trimmed.IndexOf('[');
        if (objectStart >= 0 && arrayStart >= 0 && arrayStart < objectStart)
        {
            var end = trimmed.LastIndexOf(']');
            if (end >= 0) return trimmed[arrayStart..(end + 1)];
        }
        else if (objectStart >= 0)
        {
            var end = trimmed.LastIndexOf('}');
            if (end >= 0) return trimmed[objectStart..(end + 1)];
        }
        else if (arrayStart >= 0)
        {
            var end = trimmed.LastIndexOf(']');
            if (end >= 0) return trimmed[arrayStart..(end + 1)];
        }
        return trimmed;
    }

    private void AtomicWrite(byte[] data, string path, int mode, bool exclusive = false)
    {
        WindowsPathSafety.EnsureDirectory(
            Path.GetDirectoryName(path)!, CommandDiscoveryHookConfigurationError.UnsafeBackupDirectory);
        WindowsFileSecurity.ApplyMode(Path.GetDirectoryName(path)!, WindowsFileSecurity.PrivateMode);
        try
        {
            ClientConfigFileIO.PublishAtomically(
                data, path, mode, exclusive, $".{Path.GetFileName(path)}.tmp-",
                temporary => ObserveAtomicWriteTemporary?.Invoke(temporary));
        }
        catch (ClientConfigFileException exception)
        {
            throw exception.Failure == ClientConfigFileFailure.ExclusiveExists
                ? new IOException("file already exists")
                : new GrokCLIAdapterException(GrokCLIAdapterError.RollbackFailed);
        }
    }
}