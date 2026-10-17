using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AskKey.Broker.Protocol;

namespace AskKey.Vault;

/// <summary>
/// Deterministic JSON for approval bindings. Swift uses JSONEncoder(.sortedKeys);
/// objects are sorted ordinally here so the same logical value always hashes the same.
/// </summary>
internal static class VaultJson
{
    private static readonly JsonSerializerOptions ValueOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Canonical(JsonNode? node)
    {
        var builder = new StringBuilder();
        Write(builder, node);
        return builder.ToString();
    }

    public static string Digest(JsonNode? node)
        => Sha256Hex(Encoding.UTF8.GetBytes(Canonical(node)));

    /// <summary>
    /// Swift's synthesized Encodable omits nil optionals. BrokerJson keeps some
    /// explicit null properties for wire decoding, so approval hashes strip them.
    /// </summary>
    public static JsonNode? WithoutNulls(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
            {
                var result = new JsonObject();
                foreach (var (name, value) in obj)
                {
                    if (value is null) continue;
                    result[name] = WithoutNulls(value);
                }
                return result;
            }
            case JsonArray array:
            {
                var result = new JsonArray();
                foreach (var value in array)
                {
                    if (value is null) continue;
                    result.Add(WithoutNulls(value));
                }
                return result;
            }
            default:
                return node.DeepClone();
        }
    }

    public static string Sha256Hex(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static JsonNode RecordNode(CredentialRecord record)
        => JsonNode.Parse(Encoding.UTF8.GetString(CanonicalJson.CredentialRecord(record)))!;

    public static JsonNode DeliveryNode(CredentialComponentDelivery delivery) => delivery.Kind switch
    {
        CredentialDeliveryKind.EnvironmentVariable => new JsonObject
        {
            ["environmentVariable"] = new JsonObject { ["_0"] = delivery.Variable },
        },
        CredentialDeliveryKind.TemporaryFile => new JsonObject
        {
            ["temporaryFile"] = new JsonObject { ["_0"] = delivery.Variable },
        },
        _ => new JsonObject { ["none"] = new JsonObject() },
    };

    public static JsonObject ComponentInputNode(CredentialComponentInput input)
    {
        JsonNode value = input.Value switch
        {
            CredentialComponentValue.Text text => new JsonObject
            {
                ["text"] = new JsonObject { ["_0"] = text.Value },
            },
            CredentialComponentValue.File file => new JsonObject
            {
                ["file"] = new JsonObject
                {
                    ["filename"] = file.Filename,
                    ["bytes"] = Convert.ToBase64String(file.Bytes),
                },
            },
            _ => throw VaultException.Database("Credential has an unknown payload kind."),
        };
        return new JsonObject
        {
            ["name"] = input.Name,
            ["value"] = value,
            ["delivery"] = DeliveryNode(input.Delivery),
            ["masked"] = input.Masked,
        };
    }

    public static JsonArray ComponentInputNodes(IEnumerable<CredentialComponentInput> inputs)
        => new(inputs.Select(input => (JsonNode)ComponentInputNode(input)).ToArray());

    public static JsonObject ComponentSummaryNode(BrokerCredentialComponentSummary summary) => new()
    {
        ["name"] = summary.Name,
        ["payloadKind"] = BrokerWire.ToWire(summary.PayloadKind),
        ["byteCount"] = summary.ByteCount,
        ["delivery"] = DeliveryNode(summary.Delivery),
        ["masked"] = summary.Masked,
        ["valueDigest"] = summary.ValueDigest,
    };

    public static JsonObject OrganizationSummaryNode(BrokerOrganizationSummary summary)
    {
        var operations = new JsonArray();
        foreach (var operation in summary.Operations)
        {
            var node = new JsonObject
            {
                ["operationKind"] = operation.OperationKind switch
                {
                    BrokerOrganizationSummary.Kind.Move => "move",
                    BrokerOrganizationSummary.Kind.CreateGroup => "createGroup",
                    BrokerOrganizationSummary.Kind.ExistingGroup => "existingGroup",
                    BrokerOrganizationSummary.Kind.RenameGroup => "renameGroup",
                    BrokerOrganizationSummary.Kind.MergeGroup => "mergeGroup",
                    _ => "deleteGroup",
                },
            };
            Put(node, "credential", operation.Credential);
            Put(node, "from", operation.From);
            Put(node, "to", operation.To);
            Put(node, "name", operation.Name);
            node["members"] = operation.Members;
            node["nonvisible"] = operation.Nonvisible;
            node["targetMembers"] = operation.TargetMembers;
            node["targetNonvisible"] = operation.TargetNonvisible;
            operations.Add(node);
        }
        return new JsonObject { ["operations"] = operations };
    }

    public static JsonObject GroupStateNode(AgentOrganizationGroupState state) => new()
    {
        ["normalizedName"] = state.NormalizedName,
        ["names"] = new JsonArray(state.Names.Select(value => (JsonNode)JsonValue.Create(value)!).ToArray()),
        ["storedNames"] = new JsonArray(state.StoredNames.Select(value => (JsonNode)JsonValue.Create(value)!).ToArray()),
        ["memberIDs"] = new JsonArray(state.MemberIds.Select(value => (JsonNode)JsonValue.Create(value)!).ToArray()),
        ["memberDigests"] = new JsonArray(state.MemberDigests.Select(value => (JsonNode)JsonValue.Create(value)!).ToArray()),
    };

    public static void Put(JsonObject node, string name, string? value)
    {
        if (value is not null) node[name] = value;
    }

    private static void Write(StringBuilder builder, JsonNode? node)
    {
        switch (node)
        {
            case null:
                builder.Append("null");
                return;
            case JsonObject obj:
                builder.Append('{');
                var first = true;
                foreach (var (name, value) in obj.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    if (!first) builder.Append(',');
                    first = false;
                    builder.Append(JsonSerializer.Serialize(name, ValueOptions));
                    builder.Append(':');
                    Write(builder, value);
                }
                builder.Append('}');
                return;
            case JsonArray array:
                builder.Append('[');
                for (var index = 0; index < array.Count; index++)
                {
                    if (index > 0) builder.Append(',');
                    Write(builder, array[index]);
                }
                builder.Append(']');
                return;
            default:
                builder.Append(node.ToJsonString(ValueOptions));
                return;
        }
    }
}

internal static class CredentialComponentsCodec
{
    public static byte[] Encode(IReadOnlyList<CredentialComponentInput> components)
        => Encoding.UTF8.GetBytes(VaultJson.Canonical(VaultJson.ComponentInputNodes(components)));

    public static IReadOnlyList<CredentialComponentInput> Decode(ReadOnlySpan<byte> payload)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(Encoding.UTF8.GetString(payload));
        }
        catch (JsonException exception)
        {
            throw VaultException.Database("Credential payload is invalid.", exception);
        }
        if (root is not JsonArray array)
        {
            throw VaultException.Database("Credential payload is invalid.");
        }
        var result = new List<CredentialComponentInput>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonObject component) throw VaultException.Database("Credential payload is invalid.");
            var name = component["name"]?.GetValue<string>()
                ?? throw VaultException.Database("Credential payload is invalid.");
            var valueNode = component["value"] as JsonObject
                ?? throw VaultException.Database("Credential payload is invalid.");
            CredentialComponentValue value;
            if (valueNode["text"] is JsonObject text)
            {
                value = new CredentialComponentValue.Text(text["_0"]?.GetValue<string>()
                    ?? throw VaultException.Database("Credential payload is invalid."));
            }
            else if (valueNode["file"] is JsonObject file)
            {
                var filename = file["filename"]?.GetValue<string>()
                    ?? throw VaultException.Database("Credential payload is invalid.");
                var bytes = file["bytes"]?.GetValue<string>()
                    ?? throw VaultException.Database("Credential payload is invalid.");
                value = new CredentialComponentValue.File(filename, Convert.FromBase64String(bytes));
            }
            else
            {
                throw VaultException.Database("Credential has an unknown payload kind.");
            }
            var delivery = component["delivery"] is JsonObject deliveryNode
                ? DecodeDelivery(deliveryNode, name, value)
                : DefaultDelivery(name, value);
            var masked = component["masked"]?.GetValue<bool>() ?? true;
            result.Add(new CredentialComponentInput(name, value, delivery, masked));
        }
        return result;
    }

    private static CredentialComponentDelivery DecodeDelivery(JsonObject node, string name,
        CredentialComponentValue value)
    {
        if (node["environmentVariable"] is JsonObject variable)
        {
            return CredentialComponentDelivery.EnvironmentVariable(variable["_0"]?.GetValue<string>()
                ?? throw VaultException.Database("Credential payload is invalid."));
        }
        if (node["temporaryFile"] is JsonObject file)
        {
            return CredentialComponentDelivery.TemporaryFile(file["_0"]?.GetValue<string>()
                ?? throw VaultException.Database("Credential payload is invalid."));
        }
        if (node["none"] is not null) return CredentialComponentDelivery.None;
        return DefaultDelivery(name, value);
    }

    private static CredentialComponentDelivery DefaultDelivery(string name, CredentialComponentValue value)
        => value is CredentialComponentValue.File
            ? CredentialComponentDelivery.TemporaryFile(name)
            : CredentialComponentDelivery.EnvironmentVariable(name);
}

