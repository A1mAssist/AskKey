using System.Text.Json;
using System.Text.Json.Nodes;
using AskKey.Broker.Approval;
using AskKey.Broker.Protocol;
using AskKey.Broker.Transport;
using AskKey.Integrations;

namespace AskKey.Helper;

internal static class McpServer
{
    public const string ProtocolVersion = "2024-11-05";

    public static void Run()
    {
        BrokerSocketClient client;
        try
        {
            client = BrokerSocketClient.FromConfiguration();
        }
        catch (BrokerSocketException)
        {
            // MCP initialize/tools/list remain available while the App starts.
            // Tool calls then report broker_unavailable through the normal path.
            client = new BrokerSocketClient(BrokerPipeName.Create());
        }
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        Run(input, output, client);
    }

    internal static void Run(Stream input, Stream output, BrokerSocketClient client)
    {
        var discoveryGuard = new CredentialDiscoveryGuard();
        while (true)
        {
            var frame = JsonLineFraming.ReadFrame(input, BrokerLimits.MaximumFrameBytes);
            switch (frame.Kind)
            {
                case JsonLineFraming.Kind.End:
                    return;
                case JsonLineFraming.Kind.Failure:
                    return;
                case JsonLineFraming.Kind.TooLarge:
                    if (!Write(output, McpResponses.Failure(
                            null, -32600, "Request exceeds the fixed size limit")))
                    {
                        return;
                    }
                    continue;
            }

            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(frame.Data!);
            }
            catch (JsonException)
            {
                if (!Write(output, McpResponses.Failure(null, -32700, "Parse error"))) return;
                continue;
            }

            if (parsed is not JsonObject request
                || !TryString(request, "jsonrpc", out var jsonrpc)
                || jsonrpc != "2.0"
                || !TryString(request, "method", out var method)
                || method.Length == 0)
            {
                if (!Write(output, McpResponses.Failure(null, -32600, "Invalid Request"))) return;
                continue;
            }

            var hasId = request.TryGetPropertyValue("id", out var id);
            if (hasId && !ValidId(id))
            {
                if (!Write(output, McpResponses.Failure(null, -32600, "Invalid Request"))) return;
                continue;
            }
            if (!hasId) continue;

            JsonObject response;
            switch (method)
            {
                case "initialize":
                    response = McpResponses.Success(id, new JsonObject
                    {
                        ["protocolVersion"] = ProtocolVersion,
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject
                        {
                            ["name"] = "askkey",
                            ["version"] = AskKeyVersion.Current,
                        },
                        ["instructions"] = AgentUsageGuide.Instructions,
                    });
                    break;
                case "tools/list":
                    response = McpResponses.Success(id, new JsonObject
                    {
                        ["tools"] = McpToolDefinitions.All.DeepClone(),
                    });
                    break;
                case "tools/call":
                    var parameters = request["params"] as JsonObject;
                    var name = parameters is not null && TryString(parameters, "name", out var toolName)
                        ? toolName
                        : string.Empty;
                    var arguments = parameters?["arguments"] as JsonObject ?? new JsonObject();
                    try
                    {
                        response = McpTools.Call(id, name, arguments, client, discoveryGuard);
                    }
                    catch (Exception error)
                    {
                        response = McpResponses.ToolFailure(id, error);
                    }
                    break;
                case "ping":
                    response = McpResponses.Success(id, new JsonObject());
                    break;
                default:
                    response = McpResponses.Failure(id, -32601, "Method not found");
                    break;
            }
            if (!Write(output, response)) return;
        }
    }

    private static bool Write(Stream output, JsonObject response)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(response);
            output.Write(bytes);
            output.WriteByte((byte)'\n');
            output.Flush();
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static bool ValidId(JsonNode? id)
    {
        if (id is null) return true;
        if (id is not JsonValue value) return false;
        if (value.TryGetValue<string>(out _)) return true;
        if (value.TryGetValue<bool>(out _)) return false;
        return value.TryGetValue<int>(out _)
            || value.TryGetValue<long>(out _)
            || value.TryGetValue<double>(out _);
    }

    private static bool TryString(JsonObject node, string name, out string value)
    {
        value = string.Empty;
        return node[name] is JsonValue json && json.TryGetValue<string>(out value!);
    }
}

