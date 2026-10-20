using System.Security.Cryptography;
using System.Text;

namespace AskKey.Vault;

public enum CredentialAccessOperation
{
    Catalog,
    RuntimeRead,
    Create,
    Modify,
    Delete,
}

public enum CredentialAccessResult
{
    Allowed,
    Denied,
    Expired,
    Failed,
    HiddenNameRejected,
}

public static class CredentialAccessRecordPolicy
{
    public const int MaximumEntries = 256;
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);
}

public sealed record CredentialAccessEvent
{
    public required DateTimeOffset Timestamp { get; init; }
    public string? CredentialId { get; init; }
    public CredentialAccessOperation Operation { get; init; }
    public CredentialAccessResult Result { get; init; }
    public string? CallerHint { get; init; }
    public string? DeclaredPurpose { get; init; }
    public string? ExecutableBasename { get; init; }

    public static CredentialAccessEvent Create(DateTimeOffset timestamp, string? credentialId,
        CredentialAccessOperation operation, CredentialAccessResult result, string? callerHint,
        string? declaredPurpose, string? executableBasename = null)
        => new()
        {
            Timestamp = timestamp,
            CredentialId = credentialId,
            Operation = operation,
            Result = result,
            CallerHint = callerHint,
            DeclaredPurpose = declaredPurpose,
            ExecutableBasename = executableBasename is null ? null : Path.GetFileName(executableBasename),
        };

    public static string OperationValue(CredentialAccessOperation operation) => operation switch
    {
        CredentialAccessOperation.Catalog => "catalog",
        CredentialAccessOperation.RuntimeRead => "runtimeRead",
        CredentialAccessOperation.Create => "create",
        CredentialAccessOperation.Modify => "modify",
        _ => "delete",
    };

    public static string ResultValue(CredentialAccessResult result) => result switch
    {
        CredentialAccessResult.Allowed => "allowed",
        CredentialAccessResult.Denied => "denied",
        CredentialAccessResult.Expired => "expired",
        CredentialAccessResult.Failed => "failed",
        _ => "hiddenNameRejected",
    };

    public static bool TryParseOperation(string value, out CredentialAccessOperation operation)
    {
        switch (value)
        {
            case "catalog": operation = CredentialAccessOperation.Catalog; return true;
            case "runtimeRead": operation = CredentialAccessOperation.RuntimeRead; return true;
            case "create": operation = CredentialAccessOperation.Create; return true;
            case "modify": operation = CredentialAccessOperation.Modify; return true;
            case "delete": operation = CredentialAccessOperation.Delete; return true;
            default: operation = CredentialAccessOperation.Catalog; return false;
        }
    }

    public static bool TryParseResult(string value, out CredentialAccessResult result)
    {
        switch (value)
        {
            case "allowed": result = CredentialAccessResult.Allowed; return true;
            case "denied": result = CredentialAccessResult.Denied; return true;
            case "expired": result = CredentialAccessResult.Expired; return true;
            case "failed": result = CredentialAccessResult.Failed; return true;
            case "hiddenNameRejected": result = CredentialAccessResult.HiddenNameRejected; return true;
            default: result = CredentialAccessResult.Failed; return false;
        }
    }

    /// <summary>Swift JSONEncoder default: camelCase keys, ISO8601 dates, nil omitted.</summary>
    public static string ToJson(CredentialAccessEvent value)
    {
        var fields = new List<string>
        {
            "\"timestamp\":" + JsonString(Iso8601(value.Timestamp)),
            "\"operation\":" + JsonString(OperationValue(value.Operation)),
            "\"result\":" + JsonString(ResultValue(value.Result)),
        };
        if (value.CredentialId is { } id) fields.Add("\"credentialID\":" + JsonString(id));
        if (value.CallerHint is { } hint) fields.Add("\"callerHint\":" + JsonString(hint));
        if (value.DeclaredPurpose is { } purpose) fields.Add("\"declaredPurpose\":" + JsonString(purpose));
        if (value.ExecutableBasename is { } executable) fields.Add("\"executableBasename\":" + JsonString(executable));
        return "{" + string.Join(",", fields) + "}";
    }

    public static CredentialAccessEvent FromJson(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;
        var timestamp = DateTimeOffset.Parse(root.GetProperty("timestamp").GetString()!,
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
        TryParseOperation(root.GetProperty("operation").GetString()!, out var operation);
        TryParseResult(root.GetProperty("result").GetString()!, out var result);
        return new CredentialAccessEvent
        {
            Timestamp = timestamp,
            CredentialId = Optional(root, "credentialID"),
            Operation = operation,
            Result = result,
            CallerHint = Optional(root, "callerHint"),
            DeclaredPurpose = Optional(root, "declaredPurpose"),
            ExecutableBasename = Optional(root, "executableBasename"),
        };
    }

    private static string? Optional(System.Text.Json.JsonElement root, string name)
        => root.TryGetProperty(name, out var element) && element.ValueKind == System.Text.Json.JsonValueKind.String
            ? element.GetString()
            : null;

    private static string Iso8601(DateTimeOffset value)
        => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static string JsonString(string value)
    {
        var builder = new StringBuilder("\"");
        foreach (var rune in value.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (rune.Value < 0x20) builder.Append("\\u").Append(rune.Value.ToString("x4"));
                    else builder.Append(rune.ToString());
                    break;
            }
        }
        return builder.Append('"').ToString();
    }
}

public sealed record CredentialExpirySnapshot(string Id, DateTimeOffset? ExpiresAt);

public sealed class ExpiryReminderLedgerEntry
{
    public required DateTimeOffset ExpiresAt { get; init; }
    public bool Delivered { get; init; }
}

public abstract record ExpiryReminderWork
{
    public sealed record Cancel(string Id) : ExpiryReminderWork;
    public sealed record Deliver(string Id) : ExpiryReminderWork;
    public sealed record Schedule(string Id, DateTimeOffset At) : ExpiryReminderWork;
}

public static class CredentialExpiryReminderCopy
{
    public const string AuthorizationDeniedKey =
        "Ask Key could not schedule expiry reminders because notifications are turned off.";
    public const string DeliveryFailedKey =
        "Ask Key could not deliver an expiry reminder. The reminder will be retried.";
}

public static class CredentialExpiryReminderPolicy
{
    public static readonly TimeSpan LeadTime = TimeSpan.FromDays(7);

    public static DateTimeOffset RemindAt(DateTimeOffset expiresAt) => expiresAt - LeadTime;

    public static ExpiryReminderWork? Work(CredentialExpirySnapshot snapshot, ExpiryReminderLedgerEntry? ledger,
        DateTimeOffset now)
    {
        if (snapshot.ExpiresAt is not { } expiresAt || expiresAt <= now)
        {
            return ledger is null ? null : new ExpiryReminderWork.Cancel(snapshot.Id);
        }
        if (ledger is not null && ledger.ExpiresAt == expiresAt) return null;
        var remindAt = RemindAt(expiresAt);
        return now >= remindAt
            ? new ExpiryReminderWork.Deliver(snapshot.Id)
            : new ExpiryReminderWork.Schedule(snapshot.Id, remindAt);
    }
}
