using System.Text;
using System.Text.Json;

namespace AskKey.Integrations;

/// <summary>MCP revisions supported by the helper and its connection checks.</summary>
public static class MCPProtocol
{
    public const string Previous = "2024-11-05";
    public const string Current = "2025-11-25";

    // Echo a supported request; otherwise offer the latest revision.
    public static string Negotiate(string? requestedVersion)
        => requestedVersion == Previous ? Previous : Current;
}

/// <summary>Shared initialize + tools/list request and response checks.</summary>
public static class MCPHelperContract
{
    public sealed record Identity(
        string ClientName,
        string ClientVersion,
        string ServerName,
        string ServerVersion,
        string ProtocolVersion = MCPProtocol.Current,
        IReadOnlySet<string>? RequiredTools = null)
    {
        public IReadOnlySet<string> Tools => RequiredTools ?? DefaultTools;

        public static IReadOnlySet<string> DefaultTools { get; }
            = new HashSet<string>(StringComparer.Ordinal) { "list_credentials", "run" };

        public static Identity AskKeyHelper { get; } = new(
            "askkey", AskKeyVersion.Current, "askkey", AskKeyVersion.Current);

        public static Identity GrokClient { get; } = new("askkey", "0", "askkey", AskKeyVersion.Current);

        public static Identity CursorClient { get; } = new("cursor", "0", "askkey", AskKeyVersion.Current);

        public static Identity ClaudeClient { get; } = new("claude-code", "0", "askkey", AskKeyVersion.Current);
    }

    public sealed record Inspection(string Version, IReadOnlyList<string> Tools);

    public enum Failure
    {
        Malformed,
        UnexpectedIdentity,
        MissingTools,
    }

    public sealed class MCPHelperContractException : Exception
    {
        public MCPHelperContractException(Failure failure) : base(failure.ToString()) => Error = failure;

        public Failure Error { get; }
    }

    public static byte[] RequestPayload(Identity identity)
    {
        using var stream = new MemoryStream();
        var initialize = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = identity.ProtocolVersion,
                capabilities = new Dictionary<string, object>(),
                clientInfo = new { name = identity.ClientName, version = identity.ClientVersion },
            },
        };
        var tools = new { jsonrpc = "2.0", id = 2, method = "tools/list", @params = new Dictionary<string, object>() };
        stream.Write(JsonSerializer.SerializeToUtf8Bytes(initialize));
        stream.WriteByte(0x0A);
        stream.Write(JsonSerializer.SerializeToUtf8Bytes(tools));
        stream.WriteByte(0x0A);
        return stream.ToArray();
    }

    public static Inspection Inspect(string response, Identity identity)
    {
        var lines = response.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        if (lines.Length < 2) throw Malformed();
        var initialize = ResultObject(lines[0], 1);
        if (!initialize.TryGetProperty("protocolVersion", out var protocolVersion)
            || protocolVersion.ValueKind != JsonValueKind.String
            || !initialize.TryGetProperty("serverInfo", out var serverInfo)
            || serverInfo.ValueKind != JsonValueKind.Object
            || !serverInfo.TryGetProperty("name", out var name)
            || name.ValueKind != JsonValueKind.String
            || !serverInfo.TryGetProperty("version", out var version)
            || version.ValueKind != JsonValueKind.String)
        {
            throw Malformed();
        }
        if (protocolVersion.GetString() != identity.ProtocolVersion
            || name.GetString() != identity.ServerName
            || version.GetString() != identity.ServerVersion)
        {
            throw new MCPHelperContractException(Failure.UnexpectedIdentity);
        }
        var listed = ResultObject(lines[1], 2);
        if (!listed.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
        {
            throw Malformed();
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in tools.EnumerateArray())
        {
            if (tool.ValueKind == JsonValueKind.Object
                && tool.TryGetProperty("name", out var toolName)
                && toolName.ValueKind == JsonValueKind.String)
            {
                names.Add(toolName.GetString()!);
            }
        }
        if (!identity.Tools.IsSubsetOf(names)) throw new MCPHelperContractException(Failure.MissingTools);
        return new Inspection(version.GetString()!, names.Order(StringComparer.Ordinal).ToArray());
    }

    private static JsonElement ResultObject(string line, int id)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(line); }
        catch (JsonException) { throw Malformed(); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("jsonrpc", out var jsonrpc)
                || jsonrpc.ValueKind != JsonValueKind.String
                || jsonrpc.GetString() != "2.0"
                || !root.TryGetProperty("id", out var rawId)
                || rawId.ValueKind != JsonValueKind.Number
                || !rawId.TryGetInt32(out var actualId)
                || actualId != id
                || root.TryGetProperty("error", out _)
                || !root.TryGetProperty("result", out var result)
                || result.ValueKind != JsonValueKind.Object)
            {
                throw Malformed();
            }
            return result.Clone();
        }
    }

    private static MCPHelperContractException Malformed() => new(Failure.Malformed);
}
