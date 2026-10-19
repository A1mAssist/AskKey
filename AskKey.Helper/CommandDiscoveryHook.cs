using System.Text.Json;
using System.Text.Json.Nodes;
using AskKey.Broker.Approval;
using AskKey.Broker.Transport;

namespace AskKey.Helper;

/// <summary>
/// Adapts native command hooks to the same SSH discovery reminder as Codex.
/// It never executes tool input or accesses credentials.
/// </summary>
internal static class CommandDiscoveryHook
{
    public static void Run(string client)
    {
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        Run(client, input, output);
    }

    internal static void Run(
        string client,
        Stream input,
        Stream output,
        DiscoveryTurnStore? store = null)
    {
        var allowed = Allow(client);
        var response = allowed;
        try
        {
            var data = client is "claude" or "codex"
                ? JsonLineFraming.ReadToEnd(input, BrokerLimits.MaximumFrameBytes)
                : ReadFrame(input);
            if (data is not null)
            {
                var parsed = JsonNode.Parse(data) as JsonObject;
                if (parsed is not null)
                {
                    store ??= new DiscoveryTurnStore(DefaultStoreDirectory());
                    response = Response(client, parsed, store);
                }
            }
        }
        catch (Exception)
        {
            // Discovery is a reminder. Invalid input or unavailable ephemeral
            // state cannot authorize a credential or block unrelated work.
            response = allowed;
        }

        if (client is not ("claude" or "codex") || response.Count > 0)
        {
            Write(output, response);
        }
    }

    internal static JsonObject Response(string client, JsonObject input, DiscoveryTurnStore store)
    {
        var allowed = Allow(client);
        string eventName;
        string session;
        string? turn;
        string tool;
        string? call;
        JsonObject arguments;

        if (client == "cursor")
        {
            // Grok can import Cursor definitions, but delivers Grok's native
            // envelope. Ignore that invocation so its own adapter runs once.
            var rawEvent = String(input, "hook_event_name");
            if (rawEvent is not ("preToolUse" or "postToolUse" or "postToolUseFailure")
                || String(input, "conversation_id") is not { } rawSession
                || String(input, "generation_id") is not { } rawTurn
                || String(input, "tool_name") is not { } rawTool)
            {
                return allowed;
            }
            eventName = rawEvent == "preToolUse" ? "pre" : "post";
            session = rawSession;
            turn = rawTurn;
            tool = rawTool;
            call = String(input, "tool_use_id");
            arguments = Object(input, "tool_input") ?? new JsonObject();
        }
        else if (client == "grok")
        {
            var rawEvent = String(input, "hookEventName");
            if (rawEvent is null || String(input, "sessionId") is not { } rawSession) return allowed;
            if (rawEvent == "user_prompt_submit")
            {
                if (String(input, "promptId") is not { } prompt) return allowed;
                store.BeginGrokTurn(rawSession, prompt);
                return allowed;
            }
            if (rawEvent is not ("pre_tool_use" or "post_tool_use" or "post_tool_use_failure")
                || String(input, "toolName") is not { } rawTool)
            {
                return allowed;
            }
            eventName = rawEvent == "pre_tool_use" ? "pre" : "post";
            session = rawSession;
            turn = null;
            tool = rawTool;
            call = String(input, "toolUseId");
            arguments = Object(input, "toolInput") ?? new JsonObject();
        }
        else if (client == "claude")
        {
            var rawEvent = String(input, "hook_event_name");
            if (rawEvent is null || String(input, "session_id") is not { } rawSession) return allowed;
            if (rawEvent == "UserPromptSubmit")
            {
                store.BeginClaudeTurn(rawSession);
                return allowed;
            }
            if (rawEvent is not ("PreToolUse" or "PostToolUse" or "PostToolUseFailure")
                || String(input, "tool_name") is not { } rawTool
                || String(input, "tool_use_id") is not { Length: > 0 } rawCall
                || Object(input, "tool_input") is not { } rawArguments)
            {
                return allowed;
            }
            eventName = rawEvent == "PreToolUse" ? "pre" : "post";
            session = rawSession;
            turn = null;
            tool = rawTool;
            call = rawCall;
            arguments = rawArguments;
        }
        else if (client == "codex")
        {
            // Codex exposes explicit turn IDs and only successful PostToolUse
            // callbacks. Failed calls use the no-progress release.
            var rawEvent = String(input, "hook_event_name");
            if (rawEvent is not ("PreToolUse" or "PostToolUse")
                || String(input, "session_id") is not { Length: > 0 } rawSession
                || String(input, "turn_id") is not { Length: > 0 } rawTurn
                || String(input, "tool_name") is not { } rawTool
                || String(input, "tool_use_id") is not { Length: > 0 } rawCall
                || Object(input, "tool_input") is not { } rawArguments)
            {
                return allowed;
            }
            eventName = rawEvent == "PreToolUse" ? "pre" : "post";
            session = rawSession;
            turn = rawTurn;
            tool = rawTool;
            call = rawCall;
            arguments = rawArguments;
        }
        else
        {
            return allowed;
        }

        // Cursor's generic event exposes MCP:<raw tool name>, without a
        // server identifier. This is a discovery reminder, not authentication.
        var catalog = client == "cursor"
            ? tool == "MCP:list_credentials"
            : tool == (client is "claude" or "codex"
                ? "mcp__askkey__list_credentials"
                : "askkey__list_credentials");
        var shell = client == "cursor"
            ? tool == "Shell"
            : tool == (client is "claude" or "codex" ? "Bash" : "run_terminal_command");
        if (!shell && !catalog) return allowed;
        if (shell)
        {
            if (eventName != "pre"
                || String(arguments, "command") is not { } command
                || !CredentialDiscoveryGuard.IsSshConnection(command))
            {
                return allowed;
            }
        }

        var settled = store.CatalogAttemptSettled(
            client,
            session,
            turn,
            call,
            eventName == "pre" ? DiscoveryTurnStore.Phase.Before : DiscoveryTurnStore.Phase.After,
            catalog);
        if (eventName != "pre" || !shell || settled) return allowed;

        if (client is "grok" or "claude" or "codex")
        {
            return new JsonObject
            {
                ["hookSpecificOutput"] = new JsonObject
                {
                    ["hookEventName"] = "PreToolUse",
                    ["permissionDecision"] = "deny",
                    ["permissionDecisionReason"] = Reminder,
                },
            };
        }
        return new JsonObject
        {
            ["permission"] = "deny",
            ["agent_message"] = Reminder,
            ["user_message"] = HelperLocalization.Localized(HelperLocalization.CatalogReminder),
        };
    }

    internal static JsonObject Allow(string client)
        => client == "cursor"
            ? new JsonObject { ["permission"] = "allow" }
            : new JsonObject();

    internal static string DefaultStoreDirectory()
    {
        var pipeFile = BrokerConfiguration.ResolvePipeNameFile();
        var runtime = Path.GetDirectoryName(pipeFile)
            ?? throw new IOException("Broker runtime directory is unavailable");
        return Path.Combine(runtime, "credential-discovery");
    }

    public const string Reminder =
        "This SSH command has NOT run. Before the first SSH connection in this turn, call AskKey's MCP list_credentials tool to query the credential catalog. " +
        "The catalog contains metadata, not secret values. If a credential matches the user's authorized task, follow its usage metadata and use AskKey's CLI with --wait-for-approval when command output is needed. " +
        "After the catalog attempt finishes, this reminder lets the original connection continue; if the catalog is unavailable or no credential matches, use an appropriate existing method. " +
        "If the tool cannot be called or its callback is lost, retry after 30 seconds without catalog progress; do not repeatedly retry the blocked SSH command. " +
        "Do not ask the user to paste a password before checking the catalog. " +
        "This reminder does not authorize a connection or grant credential access.";

    private static byte[]? ReadFrame(Stream input)
    {
        var frame = JsonLineFraming.ReadFrame(input, BrokerLimits.MaximumFrameBytes);
        return frame.Kind == JsonLineFraming.Kind.Frame ? frame.Data : null;
    }

    private static void Write(Stream output, JsonObject response)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(response);
        output.Write(bytes);
        output.WriteByte((byte)'\n');
        output.Flush();
    }

    private static string? String(JsonObject? node, string name)
        => node?[name] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;

    private static JsonObject? Object(JsonObject node, string name)
        => node[name] as JsonObject;
}
