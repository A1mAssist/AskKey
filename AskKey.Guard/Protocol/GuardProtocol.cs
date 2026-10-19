using System.Text.Json.Serialization;

namespace AskKey.Guard;

/// <summary>Wire contract shared by the guard service and its clients.</summary>
public static class GuardProtocol
{
    public const string ServiceName = "AskKeyGuard";
    public const string PipeName = "askkey-guard";
    public const string DevelopmentPipeName = "askkey-guard-dev";
    public const int Version = 1;
    public const int MaximumFrameBytes = 64 * 1024;

    /// <summary>--dev and ASKKEY_DEVELOPMENT=1 keep local testing off the production pipe.</summary>
    public static bool IsDevelopmentEnvironment
        => string.Equals(Environment.GetEnvironmentVariable("ASKKEY_DEVELOPMENT"), "1", StringComparison.Ordinal);

    public static string SelectPipeName(bool development)
        => development ? DevelopmentPipeName : PipeName;

    public static string DefaultPipeName => SelectPipeName(IsDevelopmentEnvironment);
}

public static class GuardOperations
{
    public const string Status = "status";
    public const string Challenge = "challenge";
    public const string Enroll = "enroll";
    public const string Unlock = "unlock";
    public const string Unenroll = "unenroll";
}

public static class GuardErrors
{
    public const string CallerRejected = "caller_rejected";
    public const string NotEnrolled = "not_enrolled";
    public const string BadNonce = "bad_nonce";
    public const string BadSignature = "bad_signature";
    public const string Protocol = "protocol";
}

/// <summary>One guard request. The caller SID is never part of the body.</summary>
public sealed class GuardRequest
{
    [JsonPropertyName("v")]
    public int Version { get; set; } = GuardProtocol.Version;

    [JsonPropertyName("op")]
    public string? Operation { get; set; }

    [JsonPropertyName("nonce")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public byte[]? Nonce { get; set; }

    [JsonPropertyName("publicKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public byte[]? PublicKey { get; set; }

    [JsonPropertyName("signature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public byte[]? Signature { get; set; }

    [JsonPropertyName("proof")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public byte[]? Proof { get; set; }
}

public sealed class GuardResponse
{
    [JsonPropertyName("v")]
    public int Version { get; init; }

    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }

    [JsonPropertyName("nonce")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public byte[]? Nonce { get; init; }

    [JsonPropertyName("kek")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public byte[]? Kek { get; init; }

    [JsonPropertyName("enrolled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Enrolled { get; init; }

    public static GuardResponse Success(
        byte[]? nonce = null, byte[]? kek = null, bool? enrolled = null)
        => new()
        {
            Version = GuardProtocol.Version,
            Ok = true,
            Nonce = nonce,
            Kek = kek,
            Enrolled = enrolled,
        };

    public static GuardResponse Failure(string error)
        => new() { Version = GuardProtocol.Version, Ok = false, Error = error };
}
