using System.Text.Json.Serialization;

namespace AskKey.Guard;

/// <summary>Wire contract shared by the guard service and its clients.</summary>
public static class GuardProtocol
{
    public const string ServiceName = "AskKeyGuard";
    public const string PipeName = "askkey-guard";
    public const string DevelopmentPipeName = "askkey-guard-dev";
    public const int Version = 2;
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
    public const string Unenroll = "unenroll";

    /// <summary>Releases the KEK after the app verified the user with Windows Hello.
    /// The desktop Hello APIs cannot sign from a packaged app, so the caller identity
    /// and the fresh nonce carry the request instead of a key signature.</summary>
    public const string Consume = "consume";

    /// <summary>First enrollment: the app proves the vault key it wraps, and the
    /// service stores that proof so a later replacement cannot point at another vault.</summary>
    public const string ConsumeEnroll = "consume-enroll";
}

public static class GuardErrors
{
    public const string CallerRejected = "caller_rejected";
    public const string NotEnrolled = "not_enrolled";
    public const string BadNonce = "bad_nonce";

    /// <summary>The enrollment proof did not match the one this SID already holds.</summary>
    public const string BadProof = "bad_proof";
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
