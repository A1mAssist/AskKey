using System.Text.Json;

namespace AskKey.Guard;

public static class GuardJson
{
    public static byte[] EncodeRequest(GuardRequest request)
        => JsonSerializer.SerializeToUtf8Bytes(request);

    public static GuardRequest? DecodeRequest(byte[] data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("v", out var version) || !version.TryGetInt32(out var value)
                || value != GuardProtocol.Version)
            {
                return null;
            }
            if (!root.TryGetProperty("op", out var operation) || operation.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var name = operation.GetString();
            if (string.IsNullOrEmpty(name)) return null;
            return new GuardRequest
            {
                Version = GuardProtocol.Version,
                Operation = name,
                Nonce = ReadBytes(root, "nonce"),
                PublicKey = ReadBytes(root, "publicKey"),
                Signature = ReadBytes(root, "signature"),
                Proof = ReadBytes(root, "proof"),
            };
        }
        catch (JsonException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public static byte[] EncodeResponse(GuardResponse response)
        => JsonSerializer.SerializeToUtf8Bytes(response);

    public static GuardResponse? DecodeResponse(byte[] data)
    {
        try
        {
            var response = JsonSerializer.Deserialize<GuardResponse>(data);
            return response is { Version: GuardProtocol.Version } ? response : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static byte[]? ReadBytes(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element)) return null;
        if (element.ValueKind == JsonValueKind.Null) return null;
        if (element.ValueKind != JsonValueKind.String) throw new FormatException(name);
        return element.GetBytesFromBase64();
    }
}
