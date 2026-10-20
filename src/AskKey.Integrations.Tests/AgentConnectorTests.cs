namespace AskKey.Integrations.Tests;

public sealed class AgentConnectorTests
{
    [Fact]
    public void SetupPreviewNamesTheMcpDiscoveryAndBackupTargets()
    {
        using var temp = new TempDirectory();
        var home = temp.Combine("home");
        var support = temp.Combine("support");
        Directory.CreateDirectory(home);
        var connector = new AgentConnector(
            home, support, temp.Combine("home", ".askkey", "askkey.exe"), "pipe", "pipe-file",
            signing: CodexHelperSigning.Development);

        var claude = connector.SetupPreview(AgentClientKind.ClaudeCode);
        Assert.Null(claude.McpConfiguration);
        Assert.Equal(temp.Combine("home", ".claude", "settings.json"), claude.DiscoveryHook.Path);
        Assert.Contains(claude.Backups, target => target.Path == temp.Combine("support", "client-backups", "claude-discovery"));

        var codex = connector.SetupPreview(AgentClientKind.Codex);
        Assert.Equal(temp.Combine("home", ".codex", "config.toml"), codex.McpConfiguration?.Path);
        Assert.Equal(temp.Combine("home", ".codex", "hooks.json"), codex.DiscoveryHook.Path);
        Assert.Contains(codex.Backups, target => target.Path == temp.Combine("support", "client-backups", "codex"));
        Assert.Contains(codex.Backups, target => target.Path == temp.Combine("support", "client-backups", "codex-discovery"));

        var cursor = connector.SetupPreview(AgentClientKind.Cursor);
        Assert.Equal(temp.Combine("home", ".cursor", "mcp.json"), cursor.McpConfiguration?.Path);
        Assert.Equal(temp.Combine("home", ".cursor", "hooks.json"), cursor.DiscoveryHook.Path);
        Assert.Contains(cursor.Backups, target => target.Path == temp.Combine("support", "client-backups", "cursor"));
        Assert.Contains(cursor.Backups, target => target.Path == temp.Combine("support", "client-backups", "cursor-discovery"));

        var grok = connector.SetupPreview(AgentClientKind.Grok);
        Assert.Equal(temp.Combine("home", ".grok", "config.toml"), grok.McpConfiguration?.Path);
        Assert.Equal(temp.Combine("home", ".grok", "hooks", "askkey-discovery.json"), grok.DiscoveryHook.Path);
        Assert.Contains(grok.Backups, target => target.Path == temp.Combine("support", "client-backups", "grok"));
        Assert.Contains(grok.Backups, target => target.Path == temp.Combine("support", "client-backups", "grok-discovery"));
    }

    [Fact]
    public void ClaudeConnectInstallsDiscoveryAndVerifiesTheConnection()
    {
        using var temp = new TempDirectory();
        var home = temp.Combine("home");
        var support = temp.Combine("support");
        Directory.CreateDirectory(home);
        var helper = TestFiles.InstallFixture(temp.Combine("home", ".askkey", "askkey.exe"));
        var claude = TestFiles.InstallFixture(temp.Combine("home", ".local", "bin", "claude.exe"));
        File.WriteAllText(temp.Combine("home", "claude-command"), helper);

        var connector = new AgentConnector(
            home, support, helper, "unused-pipe", "ok-test", claude,
            signing: CodexHelperSigning.Development);

        var status = connector.Connect(AgentClientKind.ClaudeCode);

        Assert.True(status.Connected, status.Message);
        Assert.True(status.DiscoveryConfigured);
        Assert.True(File.Exists(temp.Combine("home", ".claude", "settings.json")));
        var hook = CommandDiscoveryClient.Claude.Configuration(
            home, helper, temp.Combine("support", "client-backups", "claude-discovery"));
        Assert.True(hook.HasExpectedHook());
    }

    [Fact]
    public void ClaudeFailureRestoresTheDiscoveryHook()
    {
        using var temp = new TempDirectory();
        var home = temp.Combine("home");
        Directory.CreateDirectory(home);
        var helper = TestFiles.InstallFixture(temp.Combine("home", ".askkey", "askkey.exe"));
        var claude = TestFiles.InstallFixture(temp.Combine("home", ".local", "bin", "claude.exe"));
        // No claude-command file: the fixture reports a mismatched command after add.
        var connector = new AgentConnector(
            home, temp.Combine("support"), helper, "unused-pipe", "ok-test", claude,
            signing: CodexHelperSigning.Development);

        var status = connector.Connect(AgentClientKind.ClaudeCode);

        Assert.False(status.Connected);
        Assert.NotEmpty(status.Message);
        var hook = CommandDiscoveryClient.Claude.Configuration(
            home, helper, temp.Combine("support", "client-backups", "claude-discovery"));
        Assert.False(hook.HasExpectedHook());
    }

    [Fact]
    public void GrokConnectInstallsDiscoveryAndVerifiesTheConnection()
    {
        using var temp = new TempDirectory();
        var home = temp.Combine("home");
        Directory.CreateDirectory(home);
        var helper = TestFiles.InstallFixture(temp.Combine("home", ".askkey", "askkey.exe"));
        var grok = TestFiles.InstallFixture(temp.Combine("home", ".grok", "bin", "grok.exe"));

        var connector = new AgentConnector(
            home, temp.Combine("support"), helper, "unused-pipe", "ok-test",
            grokExecutable: grok, signing: CodexHelperSigning.Development);

        var status = connector.Connect(AgentClientKind.Grok);

        Assert.True(status.Connected, status.Message);
        Assert.True(status.DiscoveryConfigured);
        Assert.True(File.Exists(temp.Combine("home", ".grok", "config.toml")));
        var hook = CommandDiscoveryClient.Grok.Configuration(
            home, helper, temp.Combine("support", "client-backups", "grok-discovery"));
        Assert.True(hook.HasExpectedHook());
    }

    [Fact]
    public void UnverifiedConnectRestoresTheDiscoveryHook()
    {
        using var temp = new TempDirectory();
        var home = temp.Combine("home");
        Directory.CreateDirectory(home);
        var helper = TestFiles.InstallFixture(temp.Combine("home", ".askkey", "askkey.exe"));
        var claude = TestFiles.InstallFixture(temp.Combine("home", ".local", "bin", "claude.exe"));
        // The entry already matches, so the adapter's own Connect returns normally,
        // but the client cannot reach the server. Check then reports a failed
        // verification, which must roll the discovery hook back like a write error.
        File.WriteAllText(temp.Combine("home", "claude-state"), "matching");
        File.WriteAllText(temp.Combine("home", "claude-command"), helper);
        File.WriteAllText(temp.Combine("home", "claude-disconnected"), "1");
        var connector = new AgentConnector(
            home, temp.Combine("support"), helper, "unused-pipe", "ok-test", claude,
            signing: CodexHelperSigning.Development);

        var status = connector.Connect(AgentClientKind.ClaudeCode);

        Assert.False(status.Connected);
        Assert.Equal("The client configuration was found, but the connection is not verified.",
            status.Message);
        var hook = CommandDiscoveryClient.Claude.Configuration(
            home, helper, temp.Combine("support", "claude-discovery"));
        Assert.False(hook.HasExpectedHook());
    }

    [Fact]
    public void CheckingAnAbsentClaudeEntryDoesNotSpawnTheSlowCli()
    {
        using var temp = new TempDirectory();
        var home = temp.Combine("home");
        Directory.CreateDirectory(home);
        var helper = TestFiles.InstallFixture(temp.Combine("home", ".askkey", "askkey.exe"));
        var claude = TestFiles.InstallFixture(temp.Combine("home", ".local", "bin", "claude.exe"));
        // A present config file without the name is conclusive: no entry can exist.
        File.WriteAllText(temp.Combine("home", ".claude.json"), "{\"mcpServers\":{}}");
        var connector = new AgentConnector(
            home, temp.Combine("support"), helper, "unused-pipe", "ok-test", claude,
            signing: CodexHelperSigning.Development);

        var status = connector.Check(AgentClientKind.ClaudeCode);

        Assert.True(status.Installed);
        Assert.False(status.McpConfigured);
        Assert.False(File.Exists(temp.Combine("home", "claude-calls")));
    }
    [Fact]
    public void ConnectionRequiresTheCredentialDiscoveryHook()
    {
        using var temp = new TempDirectory();
        var home = temp.Combine("home");
        Directory.CreateDirectory(home);
        var helper = TestFiles.InstallFixture(temp.Combine("home", ".askkey", "askkey.exe"));
        var grok = TestFiles.InstallFixture(temp.Combine("home", ".grok", "bin", "grok.exe"));
        var connector = new AgentConnector(
            home, temp.Combine("support"), helper, "unused-pipe", "ok-test",
            grokExecutable: grok, signing: CodexHelperSigning.Development);

        var connected = connector.Connect(AgentClientKind.Grok);
        Assert.True(connected.Connected, connected.Message);

        File.Delete(CommandDiscoveryClient.Grok.Configuration(
            home, helper, temp.Combine("support", "client-backups", "grok-discovery")).HooksPath);
        var status = connector.Check(AgentClientKind.Grok);

        Assert.False(status.DiscoveryConfigured);
        Assert.False(status.Connected);
        Assert.Equal("The MCP server is configured, but the credential discovery hook is missing.", status.Message);
    }
}
