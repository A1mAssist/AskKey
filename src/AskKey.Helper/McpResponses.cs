using System.Text.Json;
using System.Text.Json.Nodes;
using AskKey.Broker.Protocol;

namespace AskKey.Helper;

internal static class McpResponses
{
    public static JsonObject Success(JsonNode? id, JsonObject result)
        => new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = result,
        };

    public static JsonObject Failure(JsonNode? id, int code, string message)
        => new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
            },
        };

    public static JsonObject ToolText(JsonNode? id, string text, string? guidance = null)
    {
        var content = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = text,
            },
        };
        if (guidance is not null)
        {
            content.Add(new JsonObject
            {
                ["type"] = "text",
                ["text"] = guidance,
            });
        }
        return Success(id, new JsonObject { ["content"] = content });
    }

    public static JsonObject ToolError(JsonNode? id, string message)
        => Success(id, new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = message },
            },
            ["isError"] = true,
        });

    public static JsonObject ToolFailure(JsonNode? id, Exception error)
    {
        if (error is not BrokerSocketException socket)
        {
            return StatusError(id, "request_failed");
        }
        var failure = ConnectionFailure(socket);
        return StatusError(id, failure.Status, failure.BrokerCode);
    }

    public static JsonObject TextWriteResponse(JsonNode? id, BrokerResponse response)
    {
        if (response.ResponseKind == BrokerResponse.Kind.Success
            && response.Payload?.PayloadKind is BrokerPayload.Kind.TextWriteRequest
                or BrokerPayload.Kind.TextWriteResult
                or BrokerPayload.Kind.OrganizationWriteResult)
        {
            return ToolText(id, Encoded(response));
        }
        if (response.ResponseKind == BrokerResponse.Kind.Failure)
        {
            return BrokerFailure(id, response.Error);
        }
        return StatusError(id, "unexpected_response");
    }

    public static JsonObject RequestStateResponse(JsonNode? id, BrokerResponse response)
    {
        if (response.ResponseKind == BrokerResponse.Kind.Success
            && response.Payload?.PayloadKind == BrokerPayload.Kind.RequestStatus)
        {
            return ToolText(id, Encoded(response));
        }
        if (response.ResponseKind == BrokerResponse.Kind.Failure)
        {
            return BrokerFailure(id, response.Error);
        }
        return StatusError(id, "unexpected_response");
    }

    public static JsonObject BrokerResponseFor(
        JsonNode? id,
        BrokerResponse response,
        ExpectedFileWriteResponse expected)
    {
        if (response.ResponseKind == BrokerResponse.Kind.Success
            && response.Payload?.PayloadKind == BrokerPayload.Kind.FileWrite
            && response.Payload.FileWrite is { } payload
            && expected.Matches(payload))
        {
            return ToolText(id, Encoded(response));
        }
        if (response.ResponseKind == BrokerResponse.Kind.Failure)
        {
            return BrokerFailure(id, response.Error);
        }
        return StatusError(id, "unexpected_response");
    }

    public static JsonObject BrokerFailure(JsonNode? id, BrokerErrorCode code)
        => StatusError(id, BrokerFailureStatus(code), BrokerWire.ToWire(code));

    public static JsonObject StatusError(JsonNode? id, string status, string? brokerCode = null)
    {
        var payload = new JsonObject { ["status"] = status };
        if (brokerCode is not null) payload["brokerCode"] = brokerCode;
        if (brokerCode == BrokerWire.ToWire(BrokerErrorCode.RequestRejected))
        {
            payload["next_step"] = AgentUsageGuide.Rejected;
        }
        return ToolError(id, payload.ToJsonString());
    }

    public static (string Status, string? BrokerCode) ConnectionFailure(BrokerSocketException error)
        => error.Error switch
        {
            BrokerSocketError.NotRunning => ("broker_unavailable", null),
            BrokerSocketError.NoResponse or BrokerSocketError.MalformedResponse or BrokerSocketError.SystemError
                => ("broker_disconnected", null),
            BrokerSocketError.BrokerFailure
                => (BrokerFailureStatus(error.BrokerFailure), BrokerWire.ToWire(error.BrokerFailure)),
            _ => ("request_failed", null),
        };

    private static string BrokerFailureStatus(BrokerErrorCode code)
        => code == BrokerErrorCode.UnsupportedVersion ? "protocol_incompatible" : "request_rejected";

    private static string Encoded(BrokerResponse response)
        => global::System.Text.Encoding.UTF8.GetString(BrokerJson.Encode(response));
}

internal enum ExpectedFileWriteResponse
{
    Upload,
    Chunk,
    Approval,
    Component,
    Cancelled,
}

internal static class ExpectedFileWriteResponseExtensions
{
    public static bool Matches(this ExpectedFileWriteResponse expected, BrokerFileWritePayload payload)
        => (expected, payload.PayloadKind) switch
        {
            (ExpectedFileWriteResponse.Upload, BrokerFileWritePayload.Kind.Upload) => true,
            (ExpectedFileWriteResponse.Chunk, BrokerFileWritePayload.Kind.ChunkAccepted) => true,
            (ExpectedFileWriteResponse.Approval, BrokerFileWritePayload.Kind.Approval) => true,
            (ExpectedFileWriteResponse.Component, BrokerFileWritePayload.Kind.ComponentFrozen) => true,
            (ExpectedFileWriteResponse.Cancelled, BrokerFileWritePayload.Kind.UploadCancelled) => true,
            _ => false,
        };
}
