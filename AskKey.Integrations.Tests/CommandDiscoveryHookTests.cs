using System.Text.Json.Nodes;

namespace AskKey.Integrations.Tests;

public class CommandDiscoveryHookTests
{
    private static string HelperPath(TempDirectory temp)
        => TestFiles.WriteExecutable(temp.Combine("app", "askkey.exe"));

    private static string CustomizedHook(string helper, bool cursor)
    {
        var command = "\"" + helper + "\" hook " + (cursor ? "cursor" : "claude");
        object root = cursor
            ? new Dictionary<string, object>
            {
                ["version"] = 1,
                ["hooks"] = new Dictionary<string, object>
                {
                    ["preToolUse"] = new[]
                    {
                        new Dictionary<string, object> { ["command"] = command, ["timeout"] = 9 },
                    },
                },
            }
            : new Dictionary<string, object>
            {
                ["hooks"] = new Dictionary<string, object>
                {
                    ["PreToolUse"] = new[]
                    {
                        new Dictionary<string, object>
                        {
                            ["matcher"] = "Edit",
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
        return global::System.Text.Json.JsonSerializer.Serialize(root) + "\n";
    }
    private static CommandDiscoveryHookConfiguration Configuration(
        TempDirectory temp, CommandDiscoveryClient client, string helper)
        => client.Configuration(temp.Combine("home"), helper, temp.Combine("backups"));

    [Fact]
    public void CursorPlanMergesWithoutDroppingOtherServers()
    {
        using var temp = new TempDirectory();
        var helper = HelperPath(temp);
        var path = temp.Write("home/.cursor/hooks.json",
            "{\n  \"version\": 1,\n  \"hooks\": {\n    \"preToolUse\": [{\"command\": \"other.exe\", \"timeout\": 1}]\n  },\n  \"custom\": true\n}\n");
        var configuration = Configuration(temp, CommandDiscoveryClient.Cursor, helper);

        var plan = configuration.Preview();
        configuration.Apply(plan);

        var merged = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        Assert.True(merged["custom"]!.GetValue<bool>());
        var groups = merged["hooks"]!["preToolUse"]!.AsArray();
        Assert.Equal(2, groups.Count);
        Assert.Equal("other.exe", groups[0]!["command"]!.GetValue<string>());
        Assert.Equal("\"" + helper + "\" hook cursor", groups[1]!["command"]!.GetValue<string>());
        Assert.Equal(3, groups[1]!["timeout"]!.GetValue<int>());
    }

    [Fact]
    public void CursorApplyIsIdempotentAndKeepsABackupOfTheOriginal()
    {
        using var temp = new TempDirectory();
        var helper = HelperPath(temp);
        var path = temp.Write("home/.cursor/hooks.json", "{\"version\":1,\"hooks\":{}}\n");
        var configuration = Configuration(temp, CommandDiscoveryClient.Cursor, helper);

        configuration.Apply(configuration.Preview());
        var afterFirst = File.ReadAllBytes(path);
        var plan = configuration.Preview();

        Assert.False(plan.Changed);
        configuration.Apply(plan);
        Assert.Equal(afterFirst, File.ReadAllBytes(path));
        var backup = Assert.Single(Directory.GetFiles(temp.Combine("backups"), "command-discovery*.bak"));
        Assert.Equal("{\"version\":1,\"hooks\":{}}\n", File.ReadAllText(backup));
        Assert.True(configuration.HasExpectedHook());
    }

    [Fact]
    public void RepeatedInstallsKeepOneOwnerBackupAndPruneSupersededOnes()
    {
        using var temp = new TempDirectory();
        var helper = HelperPath(temp);
        var path = temp.Write("home/.cursor/hooks.json", "{\"version\":1,\"hooks\":{}}\n");
        var configuration = Configuration(temp, CommandDiscoveryClient.Cursor, helper);
        // A backup written by an older build with the per-install GUID name.
        var legacy = temp.Combine("backups", "command-discovery-stale.bak");
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        File.WriteAllText(legacy, "{\"old\":true}");

        for (var attempt = 0; attempt < 3; attempt++)
        {
            File.WriteAllText(path, "{\"version\":1,\"hooks\":{\"preToolUse\":[]}}\n");
            configuration.Apply(configuration.Preview());
        }

        var owner = Assert.Single(Directory.GetFiles(temp.Combine("backups"), "command-discovery*.bak"));
        Assert.Equal("command-discovery.bak", Path.GetFileName(owner));
        Assert.False(File.Exists(legacy));
        Assert.Equal("{\"version\":1,\"hooks\":{\"preToolUse\":[]}}\n", File.ReadAllText(owner));
    }

    [Fact]
    public void CursorRejectsACustomizedAskKeyHook()
    {
        using var temp = new TempDirectory();
        var helper = HelperPath(temp);
        temp.Write("home/.cursor/hooks.json",
            CustomizedHook(helper, cursor: true));
        var configuration = Configuration(temp, CommandDiscoveryClient.Cursor, helper);

        var exception = Assert.Throws<CommandDiscoveryHookConfigurationException>(() => configuration.Preview());

        Assert.Equal(CommandDiscoveryHookConfigurationError.CustomHookMismatch, exception.Error);
    }

    [Fact]
    public void CursorRejectsConcurrentModificationAfterPreview()
    {
        using var temp = new TempDirectory();
        var helper = HelperPath(temp);
        var path = temp.Write("home/.cursor/hooks.json", "{\"version\":1,\"hooks\":{}}\n");
        var configuration = Configuration(temp, CommandDiscoveryClient.Cursor, helper);
        var plan = configuration.Preview();
        File.WriteAllText(path, "{\"version\":1,\"hooks\":{},\"changed\":true}\n");

        var exception = Assert.Throws<CommandDiscoveryHookConfigurationException>(() => configuration.Apply(plan));

        Assert.Equal(CommandDiscoveryHookConfigurationError.ConcurrentModification, exception.Error);
        Assert.Contains("changed", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void GrokOwnedFileIsCreatedAndThenLeftAlone()
    {
        using var temp = new TempDirectory();
        var helper = HelperPath(temp);
        var path = temp.Combine("home/.grok/hooks/askkey-discovery.json");
        var configuration = Configuration(temp, CommandDiscoveryClient.Grok, helper);

        var plan = configuration.Preview();
        Assert.True(plan.Changed);
        Assert.Null(plan.Before);
        configuration.Apply(plan);

        Assert.True(File.Exists(path));
        Assert.True(configuration.HasExpectedHook());
        Assert.False(configuration.Preview().Changed);
    }

    [Fact]
    public void GrokRefusesToOverwriteAForeignFile()
    {
        using var temp = new TempDirectory();
        var helper = HelperPath(temp);
        temp.Write("home/.grok/hooks/askkey-discovery.json", "{\"hooks\":{\"PreToolUse\":[]}}\n");
        var configuration = Configuration(temp, CommandDiscoveryClient.Grok, helper);

        var exception = Assert.Throws<CommandDiscoveryHookConfigurationException>(() => configuration.Preview());

        Assert.Equal(CommandDiscoveryHookConfigurationError.OwnedFileConflict, exception.Error);
    }

    [Fact]
    public void ClaudeMergeKeepsUnrelatedSettingsAndOtherHandlers()
    {
        using var temp = new TempDirectory();
        var helper = HelperPath(temp);
        var path = temp.Write("home/.claude/settings.json",
            "{\n  \"model\": \"opus\",\n  \"hooks\": {\n    \"PreToolUse\": [{\"matcher\": \"Edit\", \"hooks\": [{\"type\": \"command\", \"command\": \"other.exe\"}]}]\n  }\n}\n");
        var configuration = Configuration(temp, CommandDiscoveryClient.Claude, helper);

        configuration.Apply(configuration.Preview());

        var document = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        Assert.Equal("opus", document["model"]!.GetValue<string>());
        var groups = document["hooks"]!["PreToolUse"]!.AsArray();
        Assert.Equal(2, groups.Count);
        Assert.Equal("other.exe", groups[0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Equal("\"" + helper + "\" hook claude", groups[1]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.True(configuration.HasExpectedHook());
    }

    [Fact]
    public void ClaudeRemovalDeletesOnlyTheOwnedHandler()
    {
        using var temp = new TempDirectory();
        var helper = HelperPath(temp);
        var path = temp.Write("home/.claude/settings.json",
            "{\"model\":\"opus\",\"hooks\":{\"PreToolUse\":[{\"matcher\":\"Edit\",\"hooks\":[{\"type\":\"command\",\"command\":\"other.exe\"}]}]}}\n");
        var configuration = Configuration(temp, CommandDiscoveryClient.Claude, helper);
        configuration.Apply(configuration.Preview());

        configuration.RemoveClaudeHooks();

        var document = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        Assert.Equal("opus", document["model"]!.GetValue<string>());
        var groups = document["hooks"]!["PreToolUse"]!.AsArray();
        var remaining = Assert.Single(groups);
        Assert.Equal("other.exe", remaining!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.False(configuration.HasExpectedHook());
    }

    [Fact]
    public void ClaudeRemovalRefusesACustomizedHandler()
    {
        using var temp = new TempDirectory();
        var helper = HelperPath(temp);
        temp.Write("home/.claude/settings.json",
            CustomizedHook(helper, cursor: false));
        var configuration = Configuration(temp, CommandDiscoveryClient.Claude, helper);

        var exception = Assert.Throws<CommandDiscoveryHookConfigurationException>(
            () => configuration.RemoveClaudeHooks());

        Assert.Equal(CommandDiscoveryHookConfigurationError.CustomHookMismatch, exception.Error);
    }

    [Fact]
    public void UnrelatedHookFilesStayUntouched()
    {
        using var temp = new TempDirectory();
        var helper = HelperPath(temp);
        var path = temp.Write("home/.cursor/hooks.json", "{\"version\":1,\"hooks\":{}}\n");
        var before = File.ReadAllBytes(path);
        var configuration = Configuration(temp, CommandDiscoveryClient.Claude, helper);

        configuration.Apply(configuration.Preview());

        Assert.Equal(before, File.ReadAllBytes(path));
    }
}
public sealed class CommandDiscoveryHelperTrustOrderTests
{
    [Fact]
    public void AnUntrustedHelperIsRejectedWithoutEverRunning()
    {
        // The fixture helper writes a marker whenever it executes, so the marker proves
        // whether the probe reached the helper at all.
        using var temp = new TempDirectory();
        var helper = TestFiles.InstallFixture(temp.Combine("app", "askkey.exe"));
        var marker = temp.Combine("app", "helper-ran");

        var error = Assert.Throws<CommandDiscoveryHookConfigurationException>(() =>
            CommandDiscoveryClient.Claude.VerifyHelper(helper, temp.Root, _ => false));

        Assert.Equal(CommandDiscoveryHookConfigurationError.UntrustedHelper, error.Error);
        Assert.False(File.Exists(marker), "the helper must not run before trust is settled");
        Assert.Contains("not trusted", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ANonExecutableHelperIsRejectedWithoutEverRunning()
    {
        using var temp = new TempDirectory();
        var helper = temp.Write("app/askkey.exe", "this is not a PE image");

        var error = Assert.Throws<CommandDiscoveryHookConfigurationException>(() =>
            CommandDiscoveryClient.Grok.VerifyHelper(helper, temp.Root, _ => true));

        Assert.Equal(CommandDiscoveryHookConfigurationError.UntrustedHelper, error.Error);
    }
    [Fact]
    public void ATrustedHelperStillHasToReportItsCapabilities()
    {
        // Trust passing is necessary, not sufficient: the probe result still decides.
        using var temp = new TempDirectory();
        var helper = TestFiles.InstallFixture(temp.Combine("app", "askkey.exe"));
        var marker = temp.Combine("app", "helper-ran");

        CommandDiscoveryClient.Cursor.VerifyHelper(helper, temp.Root, _ => true);

        // A trusted helper does run, so the same marker proves the check is meaningful.
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public void AgentConnectRefusesAnUntrustedHelperBeforeTouchingTheClientConfiguration()
    {
        using var temp = new TempDirectory();
        var home = temp.Combine("home");
        Directory.CreateDirectory(home);
        var helper = TestFiles.WriteExecutable(temp.Combine("home", ".askkey", "askkey.exe"));
        var claude = TestFiles.InstallFixture(temp.Combine("home", ".local", "bin", "claude.exe"));
        var connector = new AgentConnector(
            home, temp.Combine("support"), helper, "unused-pipe", "ok-test", claude,
            signing: new CodexHelperSigning(_ => false));

        var status = connector.Connect(AgentClientKind.ClaudeCode);

        Assert.False(status.Connected);
        Assert.Contains("not trusted", status.Message, StringComparison.OrdinalIgnoreCase);
        // No discovery hook and no MCP configuration may be written.
        Assert.False(File.Exists(temp.Combine("home", ".claude", "settings.json")));
    }
}
