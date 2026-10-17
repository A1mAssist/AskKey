using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AskKey.Broker.Protocol;
using AskKey.Integrations;

namespace AskKey.Helper.Tests;

public sealed class McpServerTests
{
    [Fact]
    public void InitializeToolsListAndPingHaveExpectedShapes()
    {
        using var broker = new BrokerHarness(TestHandlers.Catalog());
        var output = RunMcp(broker,
            Initialize(1, MCPProtocol.Current) +
            Line(new { jsonrpc = "2.0", id = 2, method = "tools/list" }) +
            Line(new { jsonrpc = "2.0", id = 3, method = "ping" }));
        var lines = Lines(output);
        Assert.Equal(3, lines.Count);

        var rawLines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var inspection = MCPHelperContract.Inspect(
            string.Join("\n", rawLines.Take(2)), MCPHelperContract.Identity.AskKeyHelper);
        Assert.Equal("0.1.0", inspection.Version);
        Assert.Equal(19, inspection.Tools.Count);

        var tools = lines[1]["result"]!["tools"]!.AsArray();
        var names = tools
            .Select(tool => tool!["name"]!.GetValue<string>())
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[]
        {
            "append_component_upload",
            "append_file_write",
            "begin_component_upload",
            "begin_file_write",
            "cancel_component_upload",
            "connection_status",
            "create_credential",
            "create_text_credential",
            "credential_discovery_guard",
            "delete_credential",
            "freeze_component_upload",
            "freeze_file_write",
            "list_credentials",
            "modify_credential",
            "modify_text_credential",
            "organize_credentials",
            "request_cancel",
            "request_status",
            "run",
        }, names);
        var readOnly = tools
            .Where(tool => tool!["annotations"] is not null)
            .Select(tool => tool!["name"]!.GetValue<string>())
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "connection_status", "list_credentials", "request_status" }, readOnly);
        foreach (var name in readOnly)
        {
            var annotations = tools.First(tool => tool!["name"]!.GetValue<string>() == name)!["annotations"]!.AsObject();
            Assert.True(annotations["readOnlyHint"]!.GetValue<bool>());
            Assert.False(annotations["openWorldHint"]!.GetValue<bool>());
        }

        var ping = lines[2];
        Assert.Equal("2.0", ping["jsonrpc"]!.GetValue<string>());
        Assert.Equal(3, ping["id"]!.GetValue<int>());
        Assert.Empty(ping["result"]!.AsObject());
    }

    [Theory]
    [InlineData(MCPProtocol.Current, MCPProtocol.Current)]
    [InlineData(MCPProtocol.Previous, MCPProtocol.Previous)]
    [InlineData("2026-07-28", MCPProtocol.Current)]
    public void InitializeNegotiatesProtocolVersion(string requested, string expected)
    {
        using var broker = new BrokerHarness(TestHandlers.Catalog());
        var response = Assert.Single(Lines(RunMcp(broker, Initialize(1, requested))));
        Assert.Equal(expected, response["result"]!["protocolVersion"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public void InitializeFallsBackToLatestProtocolVersion(object? requested)
    {
        using var broker = new BrokerHarness(TestHandlers.Catalog());
        var response = Assert.Single(Lines(RunMcp(broker, Initialize(1, requested))));
        Assert.Equal(MCPProtocol.Current, response["result"]!["protocolVersion"]!.GetValue<string>());
    }

    [Fact]
    public void OversizedFrameReturnsErrorAndLoopContinues()
    {
        using var broker = new BrokerHarness(TestHandlers.Catalog());
        var oversized = new string('x', BrokerLimits.MaximumFrameBytes + 1);
        var output = RunMcp(broker,
            oversized + "\n" +
            Line(new { jsonrpc = "2.0", id = 9, method = "ping" }));
        var lines = Lines(output);
        Assert.Equal(2, lines.Count);
        Assert.Equal(-32600, lines[0]["error"]!["code"]!.GetValue<int>());
        Assert.Equal(9, lines[1]["id"]!.GetValue<int>());
        Assert.Empty(lines[1]["result"]!.AsObject());
    }

    [Fact]
    public void ExactlyMaximumFrameIsAccepted()
    {
        using var broker = new BrokerHarness(TestHandlers.Catalog());
        var request = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}";
        var frame = request + new string(' ', BrokerLimits.MaximumFrameBytes - Encoding.UTF8.GetByteCount(request));
        var output = RunMcp(broker, frame + "\n" + Line(new { jsonrpc = "2.0", id = 2, method = "ping" }));
        var lines = Lines(output);
        Assert.Equal(2, lines.Count);
        Assert.Equal(1, lines[0]["id"]!.GetValue<int>());
        Assert.Equal(2, lines[1]["id"]!.GetValue<int>());
        Assert.Empty(lines[0]["result"]!.AsObject());
        Assert.Empty(lines[1]["result"]!.AsObject());
    }

    [Fact]
    public void MalformedInvalidAndUnknownRequestsReturnProperErrors()
    {
        using var broker = new BrokerHarness(TestHandlers.Catalog());
        var output = RunMcp(broker,
            "{not json}\n" +
            Line(new { jsonrpc = "2.0", id = 1 }) +
            Line(new { jsonrpc = "2.0", id = 2, method = "nope" }) +
            Line(new { jsonrpc = "2.0", method = "nope" }));
        var lines = Lines(output);
        Assert.Equal(3, lines.Count);
        Assert.Equal(-32700, lines[0]["error"]!["code"]!.GetValue<int>());
        Assert.Null(lines[0]["id"]);
        Assert.Equal(-32600, lines[1]["error"]!["code"]!.GetValue<int>());
        Assert.Null(lines[1]["id"]);
        Assert.Equal(-32601, lines[2]["error"]!["code"]!.GetValue<int>());
        Assert.Equal(2, lines[2]["id"]!.GetValue<int>());
    }

    [Fact]
    public void ListCredentialsReturnsCredentialsAndGroups()
    {
        var items = new[]
        {
            new BrokerCatalogItem
            {
                CredentialId = "cred-1",
                Name = "NAS",
                PayloadKind = BrokerCatalogPayloadKind.Text,
                UsageInstructions = "Use it",
                Group = "Servers",
                EnvironmentVariable = "ASKKEY_NAS",
                Expired = false,
            },
            new BrokerCatalogItem
            {
                CredentialId = "cred-2",
                Name = "SSH",
                PayloadKind = BrokerCatalogPayloadKind.File,
                UsageInstructions = "Use the file",
                Group = null,
                Expired = true,
            },
        };
        using var broker = new BrokerHarness(TestHandlers.Catalog(items, new[] { "Servers", "Empty" }));
        var output = RunMcp(broker, ToolCall(1, "list_credentials", new JsonObject()));
        var response = Lines(output).Single();
        var content = response["result"]!["content"]!.AsArray();
        Assert.Equal(2, content.Count);
        Assert.Equal(AgentUsageGuide.CatalogDescription, content[1]!["text"]!.GetValue<string>());
        var catalog = JsonNode.Parse(content[0]!["text"]!.GetValue<string>())!.AsObject();
        var credentials = catalog["credentials"]!.AsArray();
        Assert.Equal(2, credentials.Count);
        Assert.Equal("NAS", credentials[0]!["name"]!.GetValue<string>());
        Assert.Equal("cred-1", credentials[0]!["credentialID"]!.GetValue<string>());
        Assert.Equal("Servers", credentials[0]!["group"]!.GetValue<string>());
        Assert.Equal("Empty", catalog["groups"]!.AsArray()[1]!.GetValue<string>());
    }

    [Fact]
    public void RunApprovalRequiredThenApprovedResume()
    {
        var runs = 0;
        const string sentinel = "TARGET_OUTPUT_SENTINEL";
        var handler = new BrokerRequestHandler(
            catalog: _ => Array.Empty<BrokerCatalogItem>(),
            requestStatus: (requestId, capability) =>
                requestId == "req-run" && capability == "cap-run" ? BrokerRequestState.Approved : null,
            textRun: (_, channel, _) =>
            {
                if (Interlocked.Increment(ref runs) == 1)
                {
                    return BrokerTextRunResult.ApprovalRequired(
                        "op-run",
                        new[] { new BrokerApprovalTicket("req-run", "cap-run", BrokerRequestState.Pending, 0) });
                }
                channel.Stdout.Write(Encoding.UTF8.GetBytes(sentinel));
                channel.Stdout.Flush();
                channel.Stderr.Write(Encoding.UTF8.GetBytes(sentinel));
                channel.Stderr.Flush();
                return BrokerTextRunResult.Exited(0);
            });
        using var broker = new BrokerHarness(handler);
        var arguments = new JsonObject
        {
            ["credentials"] = new JsonArray("token"),
            ["command"] = new JsonArray("cmd"),
            ["operation_id"] = "op-run",
        };
        var output = RunMcp(broker,
            ToolCall(1, "run", arguments) +
            ToolCall(2, "request_status", new JsonObject
            {
                ["request_id"] = "req-run",
                ["capability"] = "cap-run",
            }) +
            ToolCall(3, "run", arguments));
        var lines = Lines(output);
        Assert.Equal(3, lines.Count);

        var approval = JsonNode.Parse(lines[0]["result"]!["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
        Assert.Equal("op-run", approval["approvalRequired"]!["operationID"]!.GetValue<string>());
        var status = JsonNode.Parse(lines[1]["result"]!["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
        var state = status["success"]!["_0"]!["requestStatus"]!["_0"]!.GetValue<string>();
        Assert.Equal("approved", state);
        var resumed = JsonNode.Parse(lines[2]["result"]!["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
        Assert.Equal(0, resumed["exited"]!["_0"]!.GetValue<int>());
        Assert.DoesNotContain(sentinel, lines[2].ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RequestRejectedIncludesBrokerCodeAndNextStep()
    {
        var handler = new BrokerRequestHandler(
            catalog: _ => throw new BrokerProviderException(BrokerProviderError.RequestRejected),
            requestStatus: (_, _) => null);
        using var broker = new BrokerHarness(handler);
        var output = RunMcp(broker, ToolCall(1, "list_credentials", new JsonObject()));
        var response = Lines(output).Single();
        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        var payload = JsonNode.Parse(response["result"]!["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
        Assert.Equal("request_rejected", payload["status"]!.GetValue<string>());
        Assert.Equal("request_rejected", payload["brokerCode"]!.GetValue<string>());
        Assert.Equal(AgentUsageGuide.Rejected, payload["next_step"]!.GetValue<string>());
    }

    [Fact]
    public void RunExitedReturnsOnlyExitStatus()
    {
        using var broker = new BrokerHarness(TestHandlers.Exited(7));
        var output = RunMcp(broker, ToolCall(1, "run", new JsonObject
        {
            ["credentials"] = new JsonArray("token"),
            ["command"] = new JsonArray("cmd"),
            ["operation_id"] = "op-exit",
        }));
        var response = Lines(output).Single();
        var text = JsonNode.Parse(response["result"]!["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
        Assert.Equal(7, text["exited"]!["_0"]!.GetValue<int>());
        Assert.Contains("Target exited with code 7", response["result"]!["content"]![1]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void RunOutcomeUnknownReturnsUnknownStatus()
    {
        using var broker = new BrokerHarness(TestHandlers.OutcomeUnknown());
        var output = RunMcp(broker, ToolCall(1, "run", new JsonObject
        {
            ["credentials"] = new JsonArray("token"),
            ["command"] = new JsonArray("cmd"),
            ["operation_id"] = "op-unknown",
        }));
        var response = Lines(output).Single();
        var text = JsonNode.Parse(response["result"]!["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
        Assert.NotNull(text["outcomeUnknown"]);
        Assert.Contains("Execution outcome is unknown", response["result"]!["content"]![1]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void CredentialDiscoveryGuardBlocksFirstSshAndReleasesAfterCatalog()
    {
        var guard = new CredentialDiscoveryGuard();
        var ssh = new JsonObject
        {
            ["session_id"] = "session",
            ["turn_id"] = "turn",
            ["tool_name"] = "Bash",
            ["tool_input"] = new JsonObject { ["command"] = "ssh host" },
        };
        var blocked = guard.Response(ssh);
        Assert.Equal(
            "deny",
            blocked["hookSpecificOutput"]!["permissionDecision"]!.GetValue<string>());

        var catalog = new JsonObject
        {
            ["session_id"] = "session",
            ["turn_id"] = "turn",
            ["tool_name"] = "mcp__askkey__list_credentials",
            ["tool_input"] = new JsonObject(),
        };
        var allowed = guard.Response(catalog);
        Assert.Equal(
            "allow",
            allowed["hookSpecificOutput"]!["permissionDecision"]!.GetValue<string>());
        var token = allowed["hookSpecificOutput"]!["updatedInput"]!["discovery_token"]!.GetValue<string>();
        guard.CatalogAttemptFinished(token);

        var released = guard.Response(ssh);
        Assert.Empty(released);
        var nonSsh = new JsonObject
        {
            ["session_id"] = "session",
            ["turn_id"] = "turn",
            ["tool_name"] = "Bash",
            ["tool_input"] = new JsonObject { ["command"] = "ls -la" },
        };
        Assert.Empty(guard.Response(nonSsh));
    }

    [Theory]
    [InlineData("ssh host", true)]
    [InlineData("ssh -p 22 host", true)]
    [InlineData("ssh -G host", false)]
    [InlineData("env FOO=1 ssh host", true)]
    [InlineData("bash -lc 'ssh host'", true)]
    [InlineData("echo ssh host", false)]
    [InlineData("ssh host 'echo hi'", true)]
    [InlineData("ssh", false)]
    [InlineData("ssh host > out", true)]
    public void SshDetectionMatchesLiteralShellRules(string command, bool expected)
        => Assert.Equal(expected, CredentialDiscoveryGuard.IsSshConnection(command));

    private static string RunMcp(BrokerHarness broker, string input)
    {
        using var inputStream = new MemoryStream(Encoding.UTF8.GetBytes(input));
        using var outputStream = new MemoryStream();
        McpServer.Run(inputStream, outputStream, broker.Client);
        return Encoding.UTF8.GetString(outputStream.ToArray());
    }

    private static string Initialize(int id, object? protocolVersion = null)
    {
        var parameters = new JsonObject
        {
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "test", ["version"] = "1" },
        };
        if (protocolVersion is not null)
        {
            parameters["protocolVersion"] = JsonSerializer.SerializeToNode(protocolVersion);
        }
        return Line(new { jsonrpc = "2.0", id, method = "initialize", @params = parameters });
    }

    private static string Line(object value) => JsonSerializer.Serialize(value) + "\n";

    private static string ToolCall(int id, string name, JsonObject arguments)
        => Line(new
        {
            jsonrpc = "2.0",
            id,
            method = "tools/call",
            @params = new { name, arguments },
        });

    private static List<JsonObject> Lines(string output)
        => output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)!.AsObject())
            .ToList();
}