using AskKey.Integrations.Tests;

namespace AskKey.Integrations.Tests;

public sealed class AgentConnectorIdempotenceTests
{
    [Fact]
    public void ConnectingAnAlreadyConnectedClaudeStaysConnected()
    {
        using var temp = new TempDirectory();
        var home = temp.Combine("home");
        Directory.CreateDirectory(home);
        var helper = TestFiles.InstallFixture(temp.Combine("home", ".askkey", "askkey.exe"));
        var claude = TestFiles.InstallFixture(temp.Combine("home", ".local", "bin", "claude.exe"));
        File.WriteAllText(temp.Combine("home", "claude-command"), helper);
        var connector = new AgentConnector(
            home, temp.Combine("support"), helper, "unused-pipe", "ok-test", claude,
            signing: CodexHelperSigning.Development);

        Assert.True(connector.Connect(AgentClientKind.ClaudeCode).Connected);
        // A second Connect changes nothing, so the plan is unchanged and the
        // verification rollback must never be reached.
        var second = connector.Connect(AgentClientKind.ClaudeCode);

        Assert.True(second.Connected, second.Message);
        Assert.True(second.DiscoveryConfigured);
        var hook = CommandDiscoveryClient.Claude.Configuration(
            home, helper, temp.Combine("support", "claude-discovery"));
        Assert.True(hook.HasExpectedHook());
    }

    [Fact]
    public void ConnectingAnAlreadyConnectedGrokStaysConnected()
    {
        using var temp = new TempDirectory();
        var home = temp.Combine("home");
        Directory.CreateDirectory(home);
        var helper = TestFiles.InstallFixture(temp.Combine("home", ".askkey", "askkey.exe"));
        var grok = TestFiles.InstallFixture(temp.Combine("home", ".grok", "bin", "grok.exe"));
        var connector = new AgentConnector(
            home, temp.Combine("support"), helper, "unused-pipe", "ok-test",
            grokExecutable: grok, signing: CodexHelperSigning.Development);

        Assert.True(connector.Connect(AgentClientKind.Grok).Connected);
        var second = connector.Connect(AgentClientKind.Grok);

        Assert.True(second.Connected, second.Message);
        Assert.True(second.DiscoveryConfigured);
        Assert.True(CommandDiscoveryClient.Grok.Configuration(
            home, helper, temp.Combine("support", "grok-discovery")).HasExpectedHook());
    }
}
