using System.Text;
using System.Text.Json.Nodes;
using AskKey.Broker.Transport;

namespace AskKey.Helper.Tests;

public sealed class HookTests
{
    [Fact]
    public void CodexSshIsDeniedThenReleasedAfterCatalogAttempt()
    {
        using var temp = new TempDirectory();
        var store = new DiscoveryTurnStore(temp.Combine("credential-discovery"));
        var first = RunHook("codex", CodexInput("PreToolUse", "Bash", "ssh-call-1", "ssh host"), store);
        var firstResponse = Single(first);
        Assert.Equal(
            "deny",
            firstResponse["hookSpecificOutput"]!["permissionDecision"]!.GetValue<string>());

        Assert.Equal(string.Empty, RunHook("codex", CodexInput(
            "PreToolUse", "mcp__askkey__list_credentials", "catalog-1", "{}"), store));
        Assert.Equal(string.Empty, RunHook("codex", CodexInput(
            "PostToolUse", "mcp__askkey__list_credentials", "catalog-1", "{}"), store));
        Assert.Equal(string.Empty, RunHook("codex", CodexInput(
            "PreToolUse", "Bash", "ssh-call-2", "ssh host"), store));
    }

    [Fact]
    public void CursorSshDenialHasCursorShapeAndNonSshAllows()
    {
        using var temp = new TempDirectory();
        var store = new DiscoveryTurnStore(temp.Combine("credential-discovery"));
        var denied = RunHook("cursor", CursorInput("Shell", "ssh host"), store);
        var response = Single(denied);
        Assert.Equal("deny", response["permission"]!.GetValue<string>());
        Assert.Contains("This SSH command has NOT run", response["agent_message"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(response["user_message"]!.GetValue<string>()));

        var allowed = RunHook("cursor", CursorInput("Shell", "ls -la"), store);
        Assert.Equal("allow", Single(allowed)["permission"]!.GetValue<string>());
    }

    [Fact]
    public void ClaudeAndGrokSshDenialsUsePreToolUseShape()
    {
        using var temp = new TempDirectory();
        var store = new DiscoveryTurnStore(temp.Combine("credential-discovery"));
        var claude = Single(RunHook("claude", new JsonObject
        {
            ["hook_event_name"] = "PreToolUse",
            ["session_id"] = "claude-session",
            ["tool_name"] = "Bash",
            ["tool_use_id"] = "claude-call",
            ["tool_input"] = new JsonObject { ["command"] = "ssh host" },
        }, store));
        Assert.Equal(
            "deny",
            claude["hookSpecificOutput"]!["permissionDecision"]!.GetValue<string>());

        var grok = Single(RunHook("grok", new JsonObject
        {
            ["hookEventName"] = "pre_tool_use",
            ["sessionId"] = "grok-session",
            ["toolName"] = "run_terminal_command",
            ["toolUseId"] = "grok-call",
            ["toolInput"] = new JsonObject { ["command"] = "ssh host" },
        }, store));
        Assert.Equal(
            "deny",
            grok["hookSpecificOutput"]!["permissionDecision"]!.GetValue<string>());
    }

    [Fact]
    public void ThirtySecondsWithoutCatalogProgressReleasesTheReminder()
    {
        using var temp = new TempDirectory();
        var now = 1000d;
        var store = new DiscoveryTurnStore(temp.Combine("credential-discovery"), () => now);
        var blocked = Single(RunHook("codex", CodexInput(
            "PreToolUse", "Bash", "ssh-call-1", "ssh host"), store));
        Assert.Equal("deny", blocked["hookSpecificOutput"]!["permissionDecision"]!.GetValue<string>());

        now += 31;
        Assert.Equal(string.Empty, RunHook("codex", CodexInput(
            "PreToolUse", "Bash", "ssh-call-2", "ssh host"), store));
    }

    [Fact]
    public void DiscoveryStateStoresOnlyHashesTimestampsAndCompletion()
    {
        using var temp = new TempDirectory();
        var store = new DiscoveryTurnStore(temp.Combine("credential-discovery"), () => 1000d);
        _ = RunHook("codex", CodexInput(
            "PreToolUse", "mcp__askkey__list_credentials", "catalog-1", "{}", "session-secret", "turn-secret"), store);
        _ = RunHook("codex", CodexInput(
            "PostToolUse", "mcp__askkey__list_credentials", "catalog-1", "{}", "session-secret", "turn-secret"), store);

        var statePath = Path.Combine(store.Directory, "state.json");
        Assert.True(File.Exists(statePath));
        Assert.True(BrokerAcl.IsPrivateDirectory(store.Directory));
        Assert.True(HelperProcess.IsPrivateFile(statePath));
        var state = File.ReadAllText(statePath);
        Assert.True(Encoding.UTF8.GetByteCount(state) <= 1_048_576);
        Assert.DoesNotContain("session-secret", state, StringComparison.Ordinal);
        Assert.DoesNotContain("turn-secret", state, StringComparison.Ordinal);
        Assert.Contains("\"completed\":true", state, StringComparison.Ordinal);
    }

    [Fact]
    public void HelperLocalizationSelectsChineseAndEnglish()
    {
        Assert.Equal(
            "Ask Key：连接前先查询凭证目录。",
            HelperLocalization.Localized(HelperLocalization.CatalogReminder, new[] { "zh-CN" }));
        Assert.Equal(
            HelperLocalization.CatalogReminder,
            HelperLocalization.Localized(HelperLocalization.CatalogReminder, new[] { "en-US" }));
    }

    [Fact]
    public void InvalidHookInputAndStateFailOpen()
    {
        using var temp = new TempDirectory();
        var stateDirectory = temp.Combine("credential-discovery");
        Directory.CreateDirectory(stateDirectory);
        File.WriteAllText(Path.Combine(stateDirectory, "state.json"), "{bad json");
        var store = new DiscoveryTurnStore(stateDirectory);
        var output = RunHook("codex", CodexInput(
            "PreToolUse", "Bash", "call", "ssh host"), store);
        Assert.Equal(string.Empty, output);

        var cursor = RunHookRaw("cursor", "{bad json");
        Assert.Equal("allow", Single(cursor)["permission"]!.GetValue<string>());
    }

    private static string RunHook(string client, JsonObject input, DiscoveryTurnStore store)
        => RunHookRaw(client, input.ToJsonString(), store);

    private static string RunHookRaw(string client, string input, DiscoveryTurnStore? store = null)
    {
        using var inputStream = new MemoryStream(Encoding.UTF8.GetBytes(input));
        using var outputStream = new MemoryStream();
        CommandDiscoveryHook.Run(client, inputStream, outputStream, store);
        return Encoding.UTF8.GetString(outputStream.ToArray());
    }

    private static JsonObject CodexInput(
        string eventName,
        string tool,
        string call,
        string command,
        string session = "session",
        string turn = "turn")
        => new()
        {
            ["hook_event_name"] = eventName,
            ["session_id"] = session,
            ["turn_id"] = turn,
            ["tool_name"] = tool,
            ["tool_use_id"] = call,
            ["tool_input"] = tool == "Bash"
                ? new JsonObject { ["command"] = command }
                : new JsonObject(),
        };

    private static JsonObject CursorInput(string tool, string command)
        => new()
        {
            ["hook_event_name"] = "preToolUse",
            ["conversation_id"] = "conversation",
            ["generation_id"] = "generation",
            ["tool_name"] = tool,
            ["tool_use_id"] = "cursor-call",
            ["tool_input"] = new JsonObject { ["command"] = command },
        };

    private static JsonObject Single(string output)
        => JsonNode.Parse(output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Single())!.AsObject();
}
