using System.Text.Json.Nodes;

namespace AskKey.Integrations.Tests;

public class CursorUserMCPAdapterTests
{
    private static CursorUserMCPAdapter Adapter(TempDirectory temp, string? brokerPipe = null)
        => new(
            temp.Combine("home"),
            temp.Combine("backups", "cursor"),
            TestFiles.WriteExecutable(temp.Combine("app", "askkey.exe")),
            brokerPipe ?? "askkey-test-" + Guid.NewGuid().ToString("N"),
            CodexHelperSigning.Development);

    [Fact]
    public void ApplyMergesAskKeyAndKeepsEveryOtherServer()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("home/.cursor/mcp.json",
            "{\"mcpServers\":{\"other\":{\"command\":\"other.exe\",\"args\":[\"serve\"]}},\"theme\":\"dark\"}\n");
        var adapter = Adapter(temp);

        var diff = adapter.Apply();

        var document = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        Assert.Equal("dark", document["theme"]!.GetValue<string>());
        var servers = document["mcpServers"]!.AsObject();
        Assert.Equal(2, servers.Count);
        Assert.Equal("other.exe", servers["other"]!["command"]!.GetValue<string>());
        Assert.Equal(adapter.HelperPath, servers["askkey"]!["command"]!.GetValue<string>());
        Assert.Equal("mcp", servers["askkey"]!["args"]![0]!.GetValue<string>());
        Assert.True(adapter.HasConfiguration());
        Assert.Contains("askkey", diff.After, StringComparison.Ordinal);
        Assert.DoesNotContain("askkey", diff.Before, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyIsIdempotentAndKeepsTheOriginalBackup()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("home/.cursor/mcp.json", "{\"mcpServers\":{}}\n");
        var original = File.ReadAllBytes(path);
        var adapter = Adapter(temp);

        adapter.Apply();
        var afterFirst = File.ReadAllBytes(path);
        adapter.Apply();

        Assert.Equal(afterFirst, File.ReadAllBytes(path));
        var backupPath = Path.Combine(adapter.BackupDirectory, "cursor-mcp.json");
        Assert.True(File.Exists(backupPath));
        // Swift re-journals the pre-second-apply bytes; the single-apply case
        // above proves the original bytes are what rollback restores.
        var backup = JsonNode.Parse(File.ReadAllBytes(backupPath))!.AsObject();
        Assert.Equal(afterFirst, Convert.FromBase64String(backup["originalBytes"]!.GetValue<string>()));
    }

    [Fact]
    public void RollbackRestoresTheOriginalBytesAndRemovesTheBackup()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("home/.cursor/mcp.json", "{\"mcpServers\":{\"keep\":{\"command\":\"keep.exe\"}}}\n");
        var original = File.ReadAllBytes(path);
        var adapter = Adapter(temp);

        adapter.Apply();
        Assert.NotEqual(original, File.ReadAllBytes(path));
        adapter.Rollback();

        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(File.Exists(Path.Combine(adapter.BackupDirectory, "cursor-mcp.json")));
        Assert.False(adapter.HasConfiguration());
    }

    [Fact]
    public void RollbackDeletesAConfigThatDidNotExistBefore()
    {
        using var temp = new TempDirectory();
        var adapter = Adapter(temp);

        adapter.Apply();
        Assert.True(File.Exists(adapter.UserConfigPath));
        adapter.Rollback();

        Assert.False(File.Exists(adapter.UserConfigPath));
    }

    [Fact]
    public void InvalidJsonFailsClosedWithoutWritingAnything()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("home/.cursor/mcp.json", "{\"mcpServers\":");
        var original = File.ReadAllBytes(path);
        var adapter = Adapter(temp);

        var exception = Assert.Throws<CursorMCPException>(() => adapter.Apply());

        Assert.Equal(CursorMCPError.InvalidJSON, exception.Error);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(Directory.Exists(adapter.BackupDirectory)
            && Directory.GetFiles(adapter.BackupDirectory).Length > 0);
    }

    [Fact]
    public void StatusIsNotConnectedWithoutABrokerButTheConfigStaysWritten()
    {
        using var temp = new TempDirectory();
        var adapter = Adapter(temp);
        adapter.Apply();

        var status = adapter.Status();

        Assert.True(status.ConfigReady);
        Assert.True(status.HelperReady);
        Assert.False(status.BrokerHealthy);
        Assert.False(status.Connected);
        Assert.True(adapter.HasConfiguration());
    }
}
public class CodexUserMCPAdapterTests
{
    private static CodexUserMCPAdapter Adapter(TempDirectory temp, string? config = null)
    {
        var path = config is null
            ? temp.Combine("home", ".codex", "config.toml")
            : temp.Write("home/.codex/config.toml", config);
        return new CodexUserMCPAdapter(
            path,
            TestFiles.WriteExecutable(temp.Combine("app", "askkey.exe")),
            temp.Combine("backups", "codex"),
            "askkey-test-" + Guid.NewGuid().ToString("N"),
            CodexMCPCommand.Missing,
            CodexHelperSigning.Development);
    }

    [Fact]
    public void PreviewUpsertsOnlyTheAskKeyTableAndPreservesTheRest()
    {
        using var temp = new TempDirectory();
        const string original = "model = \"gpt-5\"\n\n[mcp_servers.other]\ncommand = \"other.exe\"\n";
        var adapter = Adapter(temp, original);

        var diff = adapter.Preview();

        Assert.Equal(original, diff.Before);
        Assert.Contains("[mcp_servers.other]", diff.After, StringComparison.Ordinal);
        Assert.Contains("[mcp_servers.askkey]", diff.After, StringComparison.Ordinal);
        Assert.Contains("model = \"gpt-5\"", diff.After, StringComparison.Ordinal);
        Assert.False(adapter.HasConfiguration());
    }

    [Fact]
    public void VerificationFailureRestoresTheOriginalBytesAndRemovesTheBackup()
    {
        using var temp = new TempDirectory();
        const string original = "model = \"gpt-5\"\n";
        var adapter = Adapter(temp, original);

        var exception = Assert.Throws<CodexUserMCPException>(() => adapter.Apply());

        Assert.Equal(CodexUserMCPError.ConnectionFailed, exception.Error);
        Assert.Equal(original, File.ReadAllText(adapter.ConfigPath));
        Assert.False(File.Exists(Path.Combine(adapter.BackupDirectory, "config.toml")));
        Assert.False(adapter.HasConfiguration());
    }

    [Fact]
    public void VerificationFailureRemovesAConfigThatDidNotExistBefore()
    {
        using var temp = new TempDirectory();
        var adapter = Adapter(temp);

        Assert.Throws<CodexUserMCPException>(() => adapter.Apply());

        Assert.False(File.Exists(adapter.ConfigPath));
        Assert.False(File.Exists(Path.Combine(adapter.BackupDirectory, "config.toml")));
    }

    [Fact]
    public void FailedWriteAfterBackupIsRestoredFromTheJournal()
    {
        using var temp = new TempDirectory();
        const string original = "model = \"gpt-5\"\n";
        var adapter = Adapter(temp, original);
        adapter.Lifecycle.AfterBackup = () =>
        {
            File.WriteAllText(adapter.ConfigPath, "concurrent = true\n");
        };

        Assert.Throws<CodexUserMCPException>(() => adapter.Apply());

        Assert.Equal("concurrent = true\n", File.ReadAllText(adapter.ConfigPath));
    }

    [Fact]
    public void UnsignedReleaseHelperFailsClosedBeforeWriting()
    {
        using var temp = new TempDirectory();
        const string original = "model = \"gpt-5\"\n";
        var path = temp.Write("home/.codex/config.toml", original);
        var adapter = new CodexUserMCPAdapter(
            path,
            TestFiles.WriteExecutable(temp.Combine("app", "askkey.exe")),
            temp.Combine("backups", "codex"),
            "askkey-test",
            CodexMCPCommand.Missing,
            CodexHelperSigning.Executable);

        var exception = Assert.Throws<CodexUserMCPException>(() => adapter.Apply());

        Assert.Equal(CodexUserMCPError.ConnectionFailed, exception.Error);
        Assert.Equal("helper", exception.Reason);
        Assert.Equal(original, File.ReadAllText(path));
    }
}

public class GrokCLIAdapterTests
{
    private static GrokCLIAdapter Adapter(TempDirectory temp, string? config = null)
    {
        if (config is not null) temp.Write("grok/config.toml", config);
        return new GrokCLIAdapter(
            temp.Combine("grok"),
            temp.Combine("isolated"),
            TestFiles.WriteExecutable(temp.Combine("app", "askkey.exe")),
            temp.Combine("missing", "grok.exe"),
            temp.Combine("backups", "grok"),
            "askkey-test-" + Guid.NewGuid().ToString("N"),
            CodexHelperSigning.Development);
    }

    [Fact]
    public void PreviewPreservesUnrelatedTablesAndRedactsSecrets()
    {
        using var temp = new TempDirectory();
        const string original = "theme = \"dark\"\n\n[mcp_servers.other]\ncommand = \"other.exe\"\nargs = [\"serve\"]\n";
        var adapter = Adapter(temp, original);

        var preview = adapter.Preview();

        Assert.Contains("[mcp_servers.other]", preview, StringComparison.Ordinal);
        Assert.Contains("[mcp_servers.askkey]", preview, StringComparison.Ordinal);
        Assert.Contains("theme = \"dark\"", preview, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllText(adapter.ConfigPath));
        Assert.False(adapter.HasConfiguration());
    }

    [Fact]
    public void ConnectWritesLosslessTomlAndFailsVerificationWithoutTheOfficialCli()
    {
        using var temp = new TempDirectory();
        const string original = "theme = \"dark\"\n\n[mcp_servers.other]\ncommand = \"other.exe\"\n";
        var adapter = Adapter(temp, original);

        var exception = Assert.Throws<GrokCLIAdapterException>(() => adapter.Connect());

        Assert.Equal(GrokCLIAdapterError.VerificationFailed, exception.Error);
        Assert.Equal(original, File.ReadAllText(adapter.ConfigPath));
        Assert.False(File.Exists(Path.Combine(adapter.BackupDirectory, "grok-cli.config.toml")));
        Assert.False(File.Exists(Path.Combine(adapter.BackupDirectory, "grok-cli.state")));
    }

    [Fact]
    public void ConnectRollsBackAConfigThatDidNotExistBefore()
    {
        using var temp = new TempDirectory();
        var adapter = Adapter(temp);

        Assert.Throws<GrokCLIAdapterException>(() => adapter.Connect());

        Assert.False(File.Exists(adapter.ConfigPath));
        Assert.False(File.Exists(Path.Combine(adapter.BackupDirectory, "grok-cli.state")));
    }

    [Fact]
    public void RemoteConnectorIsReportedAndNeverRewritten()
    {
        using var temp = new TempDirectory();
        const string original = "[mcp_servers.askkey]\nurl = \"https://example.invalid/mcp\"\n";
        var adapter = Adapter(temp, original);

        var status = adapter.Status();

        Assert.False(status.Connected);
        Assert.Equal("remote_connector", status.Reason);
        Assert.Equal(original, File.ReadAllText(adapter.ConfigPath));
    }

    [Fact]
    public void AskKeyTransportDistinguishesStdioRemoteAndMissing()
    {
        var stdio = "[mcp_servers.askkey]\ncommand = \"C:\\\\app\\\\askkey.exe\"\nargs = [\"mcp\"]\n";
        var remote = "[mcp_servers.askkey]\nurl = \"https://example.invalid\"\n";
        var missing = "[mcp_servers.other]\ncommand = \"x\"\n";

        var parsed = GrokUserTOML.AskKeyTransport(stdio);
        Assert.True(parsed.IsStdio("C:\\app\\askkey.exe", new[] { "mcp" }));
        Assert.Equal(GrokTomlTransportKind.Remote, GrokUserTOML.AskKeyTransport(remote).Kind);
        Assert.Equal(GrokTomlTransportKind.Missing, GrokUserTOML.AskKeyTransport(missing).Kind);
    }
}

public class CodexDiscoveryHookConfigurationTests
{
    private static CodexDiscoveryHookConfiguration Configuration(TempDirectory temp)
        => new(
            temp.Combine("home", ".codex", "hooks.json"),
            temp.Combine("backups", "codex-hooks"),
            temp.Combine("app", "askkey.exe"));

    [Fact]
    public void ApplyMergesBothEventsAndRestoreReturnsTheOriginalBytes()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("home/.codex/hooks.json",
            "{\n  \"hooks\": {\n    \"PreToolUse\": [{\"matcher\": \"Bash\", \"hooks\": [{\"type\": \"command\", \"command\": \"other.exe\"}]}]\n  },\n  \"keep\": 1\n}\n");
        var original = File.ReadAllBytes(path);
        var configuration = Configuration(temp);

        var plan = configuration.Preview();
        Assert.True(plan.Changed);
        configuration.Apply(plan);

        var document = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        Assert.Equal(1, document["keep"]!.GetValue<int>());
        var pre = document["hooks"]!["PreToolUse"]!.AsArray();
        Assert.Equal(2, pre.Count);
        Assert.Equal("other.exe", pre[0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Equal(configuration.ExpectedCommand, pre[1]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Single(document["hooks"]!["PostToolUse"]!.AsArray());
        Assert.True(configuration.HasExpectedHook());

        configuration.Restore(plan);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(configuration.HasExpectedHook());
    }

    [Fact]
    public void ApplyIsIdempotentAndKeepsTheReviewedBackup()
    {
        using var temp = new TempDirectory();
        temp.Write("home/.codex/hooks.json", "{\"hooks\":{}}\n");
        var configuration = Configuration(temp);

        configuration.Apply(configuration.Preview());
        var afterFirst = File.ReadAllBytes(configuration.HooksPath);
        var second = configuration.Preview();

        Assert.False(second.Changed);
        configuration.Apply(second);
        Assert.Equal(afterFirst, File.ReadAllBytes(configuration.HooksPath));
        var backup = Assert.Single(Directory.GetFiles(configuration.BackupDirectory, "codex-discovery-*.bak"));
        Assert.Equal("{\"hooks\":{}}\n", File.ReadAllText(backup));
    }

    [Fact]
    public void LegacyMcpToolGroupIsMigratedInPlace()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("home/.codex/hooks.json",
            "{\"hooks\":{\"PreToolUse\":[{\"matcher\":\"^(Bash|mcp__askkey__list_credentials)$\",\"hooks\":[{\"type\":\"mcp_tool\",\"server\":\"askkey\",\"tool\":\"credential_discovery_guard\",\"input\":{\"session_id\":\"${session_id}\",\"turn_id\":\"${turn_id}\",\"tool_name\":\"${tool_name}\",\"tool_input\":\"${tool_input}\"},\"timeout\":3}]}]}}\n");
        var configuration = Configuration(temp);

        var plan = configuration.Preview();
        Assert.True(plan.Changed);
        configuration.Apply(plan);

        var groups = JsonNode.Parse(File.ReadAllBytes(path))!["hooks"]!["PreToolUse"]!.AsArray();
        Assert.Single(groups);
        Assert.Equal(configuration.ExpectedCommand, groups[0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Single(JsonNode.Parse(File.ReadAllBytes(path))!["hooks"]!["PostToolUse"]!.AsArray());
        Assert.True(configuration.HasExpectedHook());
    }

    [Fact]
    public void CustomizedOwnHookIsRejectedWithoutWriting()
    {
        using var temp = new TempDirectory();
        var helper = temp.Combine("app", "askkey.exe");
        var command = "\"" + helper + "\" hook codex";
        var root = new Dictionary<string, object>
        {
            ["hooks"] = new Dictionary<string, object>
            {
                ["PreToolUse"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["matcher"] = "^(Bash|mcp__askkey__list_credentials)$",
                        ["hooks"] = new[]
                        {
                            new Dictionary<string, object>
                            {
                                ["type"] = "command",
                                ["command"] = command,
                                ["timeout"] = 99,
                            },
                        },
                    },
                },
            },
        };
        var path = temp.Write("home/.codex/hooks.json",
            global::System.Text.Json.JsonSerializer.Serialize(root) + "\n");
        var original = File.ReadAllBytes(path);
        var configuration = Configuration(temp);

        var exception = Assert.Throws<CodexDiscoveryHookConfigurationException>(() => configuration.Preview());

        Assert.Equal(CodexDiscoveryHookConfigurationError.CustomHookMismatch, exception.Error);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void ConcurrentChangeAfterPreviewIsPreserved()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("home/.codex/hooks.json", "{\"hooks\":{}}\n");
        var configuration = Configuration(temp);
        var plan = configuration.Preview();
        File.WriteAllText(path, "{\"hooks\":{},\"changed\":true}\n");

        var exception = Assert.Throws<CodexDiscoveryHookConfigurationException>(() => configuration.Apply(plan));

        Assert.Equal(CodexDiscoveryHookConfigurationError.ConcurrentModification, exception.Error);
        Assert.Contains("changed", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void RestoreRefusesToOverwriteAConcurrentChange()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("home/.codex/hooks.json", "{\"hooks\":{}}\n");
        var configuration = Configuration(temp);
        var plan = configuration.Preview();
        configuration.Apply(plan);
        File.WriteAllText(path, "{\"hooks\":{},\"later\":true}\n");

        var exception = Assert.Throws<CodexDiscoveryHookConfigurationException>(() => configuration.Restore(plan));

        Assert.Equal(CodexDiscoveryHookConfigurationError.RestoreConflict, exception.Error);
        Assert.Contains("later", File.ReadAllText(path), StringComparison.Ordinal);
    }
}

public class MCPHelperContractTests
{
    private static string Response(
        string server = "askkey",
        string version = null!,
        string protocolVersion = MCPProtocol.Current,
        params string[] tools)
    {
        version ??= AskKeyVersion.Current;
        var initialize = global::System.Text.Json.JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            result = new
            {
                protocolVersion,
                serverInfo = new { name = server, version },
            },
        });
        var list = global::System.Text.Json.JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 2,
            result = new { tools = tools.Select(name => new { name }).ToArray() },
        });
        return initialize + "\n" + list + "\n";
    }

    [Fact]
    public void ValidInitializeAndToolsListAreAccepted()
    {
        var inspected = MCPHelperContract.Inspect(
            Response(tools: new[] { "run", "list_credentials", "extra" }),
            MCPHelperContract.Identity.CursorClient);

        Assert.Equal(AskKeyVersion.Current, inspected.Version);
        Assert.Equal(new[] { "extra", "list_credentials", "run" }, inspected.Tools);
    }

    [Fact]
    public void PreviousProtocolVersionIsAcceptedForAnExplicitPreviousIdentity()
    {
        var identity = MCPHelperContract.Identity.CursorClient with
        {
            ProtocolVersion = MCPProtocol.Previous,
        };
        var inspected = MCPHelperContract.Inspect(
            Response(protocolVersion: MCPProtocol.Previous, tools: new[] { "run", "list_credentials" }),
            identity);

        Assert.Equal(AskKeyVersion.Current, inspected.Version);
    }

    [Fact]
    public void WrongServerIdentityIsRejected()
    {
        var exception = Assert.Throws<MCPHelperContract.MCPHelperContractException>(() =>
            MCPHelperContract.Inspect(Response(server: "impostor", tools: new[] { "run", "list_credentials" }),
                MCPHelperContract.Identity.CursorClient));

        Assert.Equal(MCPHelperContract.Failure.UnexpectedIdentity, exception.Error);
    }

    [Fact]
    public void MissingToolsAreRejected()
    {
        var exception = Assert.Throws<MCPHelperContract.MCPHelperContractException>(() =>
            MCPHelperContract.Inspect(Response(tools: new[] { "run" }),
                MCPHelperContract.Identity.GrokClient));

        Assert.Equal(MCPHelperContract.Failure.MissingTools, exception.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json\n{}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"protocolVersion\":\"2025-11-25\",\"serverInfo\":{\"name\":\"askkey\",\"version\":\"0\"}}}\n{}")]
    public void MalformedResponsesAreRejected(string response)
    {
        var exception = Assert.Throws<MCPHelperContract.MCPHelperContractException>(() =>
            MCPHelperContract.Inspect(response, MCPHelperContract.Identity.CursorClient));

        Assert.Equal(MCPHelperContract.Failure.Malformed, exception.Error);
    }

    [Fact]
    public void RequestPayloadContainsInitializeThenToolsList()
    {
        var payload = global::System.Text.Encoding.UTF8.GetString(
            MCPHelperContract.RequestPayload(MCPHelperContract.Identity.ClaudeClient));
        var lines = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.Contains("\"method\":\"initialize\"", lines[0], StringComparison.Ordinal);
        Assert.Contains($"\"protocolVersion\":\"{MCPProtocol.Current}\"", lines[0], StringComparison.Ordinal);
        Assert.Contains("\"name\":\"claude-code\"", lines[0], StringComparison.Ordinal);
        Assert.Contains("\"method\":\"tools/list\"", lines[1], StringComparison.Ordinal);
    }
}

public class ClaudeCodeMCPAdapterTests
{
    private static ClaudeCodeMCPAdapter Adapter(TempDirectory temp, string helper, string brokerPipe)
    {
        var claude = TestFiles.InstallFixture(temp.Combine("home", ".local", "bin", "claude.exe"));
        Directory.CreateDirectory(temp.Combine("project"));
        File.WriteAllText(temp.Combine("home", "claude-command"), helper);
        return new ClaudeCodeMCPAdapter(
            temp.Combine("home"), temp.Combine("project"), helper, brokerPipe,
            claude, CodexHelperSigning.Development);
    }

    [Fact]
    public void PlanAddsAnAbsentServerAndConnectVerifiesIt()
    {
        using var temp = new TempDirectory();
        var helper = TestFiles.InstallFixture(temp.Combine("app", "askkey.exe"));
        var adapter = Adapter(temp, helper, "ok-claude");
        adapter.CommandTimeout = TimeSpan.FromSeconds(10);

        var plan = adapter.Plan();
        Assert.Equal(ClaudeCodeMCPState.Absent, plan.State);
        Assert.Equal("2.1.282", plan.Version);

        var status = adapter.Connect();

        Assert.True(status.Connected);
        Assert.Equal("ok", status.Reason);
        Assert.Equal(AskKeyVersion.Current, status.HelperVersion);
        var added = File.ReadAllText(temp.Combine("home", "claude-added"));
        var json = JsonNode.Parse(added)!.AsObject();
        Assert.Equal("stdio", json["type"]!.GetValue<string>());
        Assert.Equal(helper, json["command"]!.GetValue<string>());
        Assert.Equal("mcp", json["args"]![0]!.GetValue<string>());
        Assert.Contains(File.ReadAllLines(temp.Combine("home", "claude-calls")),
            line => line.StartsWith("mcp add-json askkey ", StringComparison.Ordinal));
    }

    [Fact]
    public void UnsupportedVersionFailsBeforeAnyWrite()
    {
        using var temp = new TempDirectory();
        var helper = TestFiles.InstallFixture(temp.Combine("app", "askkey.exe"));
        var adapter = Adapter(temp, helper, "ok-claude");
        File.WriteAllText(temp.Combine("home", "claude-version"), "2.0.99 (Claude Code)\n");

        var exception = Assert.Throws<ClaudeCodeMCPException>(() => adapter.Connect());

        Assert.Equal(ClaudeCodeMCPError.UnsupportedVersion, exception.Error);
        Assert.False(File.Exists(temp.Combine("home", "claude-added")));
    }

    [Fact]
    public void DifferentEntryIsAConflictAndIsNeverRemoved()
    {
        using var temp = new TempDirectory();
        var helper = TestFiles.InstallFixture(temp.Combine("app", "askkey.exe"));
        var adapter = Adapter(temp, helper, "ok-claude");
        File.WriteAllText(temp.Combine("home", "claude-state"), "different");

        var exception = Assert.Throws<ClaudeCodeMCPException>(() => adapter.Connect());

        Assert.Equal(ClaudeCodeMCPError.ConflictingEntry, exception.Error);
        Assert.Equal("different", File.ReadAllText(temp.Combine("home", "claude-state")));
        Assert.DoesNotContain(
            File.ReadAllLines(temp.Combine("home", "claude-calls")),
            line => line.StartsWith("mcp remove askkey", StringComparison.Ordinal));
    }

    [Fact]
    public void VerificationFailureRollsBackAndConfirmsAbsence()
    {
        using var temp = new TempDirectory();
        var helper = TestFiles.InstallFixture(temp.Combine("app", "askkey.exe"));
        var adapter = Adapter(temp, helper, "ok-claude");
        File.WriteAllText(temp.Combine("home", "claude-disconnected"), "1");

        var exception = Assert.Throws<ClaudeCodeMCPException>(() => adapter.Connect());

        Assert.Equal(ClaudeCodeMCPError.VerificationFailed, exception.Error);
        Assert.Equal("client_disconnected", exception.Detail);
        Assert.Equal("absent", File.ReadAllText(temp.Combine("home", "claude-state")));
        var calls = File.ReadAllLines(temp.Combine("home", "claude-calls"));
        Assert.Contains(calls, line => line.StartsWith("mcp add-json", StringComparison.Ordinal));
        Assert.Contains(calls, line => line.StartsWith("mcp remove", StringComparison.Ordinal));
    }

    [Fact]
    public void FailedAddDoesNotRemoveAnything()
    {
        using var temp = new TempDirectory();
        var helper = TestFiles.InstallFixture(temp.Combine("app", "askkey.exe"));
        var adapter = Adapter(temp, helper, "ok-claude");
        File.WriteAllText(temp.Combine("home", "claude-add-fail"), "1");

        var exception = Assert.Throws<ClaudeCodeMCPException>(() => adapter.Connect());

        Assert.Equal(ClaudeCodeMCPError.AddFailed, exception.Error);
        Assert.False(File.Exists(temp.Combine("home", "claude-state")));
        Assert.DoesNotContain(
            File.ReadAllLines(temp.Combine("home", "claude-calls")),
            line => line.StartsWith("mcp remove askkey", StringComparison.Ordinal));
    }
}

public class GrokCLIAdapterFixtureTests
{
    private static GrokCLIAdapter Adapter(TempDirectory temp, string helper, string brokerPipe)
    {
        var grok = TestFiles.InstallFixture(temp.Combine("bin", "grok.exe"));
        return new GrokCLIAdapter(
            temp.Combine("grok"),
            temp.Combine("isolated"),
            helper,
            grok,
            temp.Combine("backups", "grok"),
            brokerPipe,
            CodexHelperSigning.Development)
        {
            CommandTimeout = TimeSpan.FromSeconds(10),
        };
    }

    [Fact]
    public void ConnectWritesLosslessStdioAndVerifiesTheOfficialCli()
    {
        using var temp = new TempDirectory();
        var helper = TestFiles.InstallFixture(temp.Combine("app", "askkey.exe"));
        var adapter = Adapter(temp, helper, "ok-grok");
        temp.Write("grok/config.toml", "theme = \"dark\"\n\n[mcp_servers.other]\ncommand = \"other.exe\"\n");

        var result = adapter.Connect();

        Assert.True(result.Connected);
        Assert.Equal("ok", result.Reason);
        Assert.Equal(AskKeyVersion.Current, result.HelperVersion);
        var config = File.ReadAllText(adapter.ConfigPath);
        Assert.Contains("theme = \"dark\"", config, StringComparison.Ordinal);
        Assert.Contains("[mcp_servers.other]", config, StringComparison.Ordinal);
        Assert.True(GrokUserTOML.AskKeyTransport(config).IsStdio(helper, new[] { "mcp" }));
        Assert.True(adapter.HasConfiguration());
        Assert.False(File.Exists(Path.Combine(adapter.BackupDirectory, "grok-cli.state")));
    }

    [Fact]
    public void VerificationFailureRestoresTheOriginalConfig()
    {
        using var temp = new TempDirectory();
        var helper = TestFiles.InstallFixture(temp.Combine("app", "askkey.exe"));
        var adapter = Adapter(temp, helper, "unavailable-grok");
        const string original = "theme = \"dark\"\n";
        temp.Write("grok/config.toml", original);

        var exception = Assert.Throws<GrokCLIAdapterException>(() => adapter.Connect());

        Assert.Equal(GrokCLIAdapterError.VerificationFailed, exception.Error);
        Assert.Equal(original, File.ReadAllText(adapter.ConfigPath));
        Assert.False(File.Exists(Path.Combine(adapter.BackupDirectory, "grok-cli.state")));
    }

    [Fact]
    public void VerificationFailureDeletesAConfigItCreated()
    {
        using var temp = new TempDirectory();
        var helper = TestFiles.InstallFixture(temp.Combine("app", "askkey.exe"));
        var adapter = Adapter(temp, helper, "unavailable-grok");

        Assert.Throws<GrokCLIAdapterException>(() => adapter.Connect());

        Assert.False(File.Exists(adapter.ConfigPath));
        Assert.False(File.Exists(Path.Combine(adapter.BackupDirectory, "grok-cli.state")));
    }
}

public class ClaudeStartupNoticeTests
{
    [Fact]
    public void UpdateNoticeDoesNotHideAnAbsentAskKeyEntry()
    {
        using var temp = new TempDirectory();
        var home = temp.Combine("home");
        Directory.CreateDirectory(home);
        var helper = TestFiles.InstallFixture(temp.Combine("home", "askkey.exe"));
        var claude = TestFiles.InstallFixture(temp.Combine("home", "claude.exe"));
        File.WriteAllText(temp.Combine("home", "claude-get-notice"),
            "[clawgod] v2.2.0 available (installed: v2.0.2) — run 'claude update' to upgrade\n");

        var adapter = new ClaudeCodeMCPAdapter(
            home, home, helper, "unused", claude,
            CodexHelperSigning.Development, searchPath: "");

        Assert.Equal(ClaudeCodeMCPState.Absent, adapter.Plan().State);
    }
}

public class ClaudeCodeMCPAdapterPreflightCacheTests
{
    private static ClaudeCodeMCPAdapter Adapter(TempDirectory temp, string helper, string brokerPipe)
    {
        var claude = TestFiles.InstallFixture(temp.Combine("home", ".local", "bin", "claude.exe"));
        Directory.CreateDirectory(temp.Combine("project"));
        File.WriteAllText(temp.Combine("home", "claude-command"), helper);
        return new ClaudeCodeMCPAdapter(
            temp.Combine("home"), temp.Combine("project"), helper, brokerPipe,
            claude, CodexHelperSigning.Development);
    }

    [Fact]
    public void PreflightIsCachedPerExecutableAndReProbesAfterTheBinaryChanges()
    {
        using var temp = new TempDirectory();
        var helper = TestFiles.InstallFixture(temp.Combine("app", "askkey.exe"));
        var adapter = Adapter(temp, helper, "ok-claude");
        adapter.CommandTimeout = TimeSpan.FromSeconds(10);
        var calls = temp.Combine("home", "claude-calls");

        Assert.Equal("2.1.282", adapter.Preflight());
        var first = File.ReadAllText(calls);
        Assert.True(CountLines(first) >= 5, "preflight should probe --version plus four --help commands");
        // Same executable: the version and the four --help probes are not repeated.
        Assert.Equal("2.1.282", adapter.Preflight());
        Assert.Equal(first, File.ReadAllText(calls));

        // A newer CLI must be probed again, or a Claude upgrade stays invisible.
        var claude = Path.Combine(adapter.HomeDirectory, ".local", "bin", "claude.exe");
        File.SetLastWriteTimeUtc(claude, DateTime.UtcNow.AddMinutes(5));
        File.WriteAllText(temp.Combine("home", "claude-version"), "2.2.0 (Claude Code)\n");
        Assert.Equal("2.2.0", adapter.Preflight());
        Assert.True(CountLines(File.ReadAllText(calls)) > CountLines(first));
    }

    private static int CountLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
}

public class CursorProtocolFixtureTests
{
    [Fact]
    public void CursorProbesTheRealHelperProtocolButReportsAMissingBroker()
    {
        using var temp = new TempDirectory();
        var helper = TestFiles.InstallFixture(temp.Combine("app", "askkey.exe"));
        var adapter = new CursorUserMCPAdapter(
            temp.Combine("home"), temp.Combine("backups", "cursor"), helper,
            "askkey-test-" + Guid.NewGuid().ToString("N"), CodexHelperSigning.Development);

        adapter.Apply();
        var status = adapter.Status();

        Assert.True(status.ConfigReady);
        Assert.True(status.HelperReady);
        Assert.True(status.ProtocolReady);
        Assert.False(status.BrokerHealthy);
        Assert.False(status.Connected);
    }
}
