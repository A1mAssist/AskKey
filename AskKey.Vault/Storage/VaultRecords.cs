using System.Globalization;

namespace AskKey.Vault;

/// <summary>Stored row of the credentials table; authenticationTag is never part of the MAC input.</summary>
public sealed class CredentialRecord
{
    public required string Id { get; set; }
    public required byte[] NameIndex { get; set; }
    public required byte[] EncryptedDisplayName { get; set; }
    public required byte[] EncryptedPayload { get; set; }
    public required byte[] EncryptedUsageInstructions { get; set; }
    public required byte[] EncryptedPrivateNotes { get; set; }
    public byte[]? EncryptedGroupName { get; set; }
    public byte[]? EncryptedEnvironmentVariable { get; set; }
    public required string PayloadKind { get; set; }
    public required string Permission { get; set; }
    public string? ExpiresAt { get; set; }
    public required string CreatedAt { get; set; }
    public required string UpdatedAt { get; set; }
    public byte[]? EncryptedOriginalFilename { get; set; }
    public int? ByteSize { get; set; }
    public byte[]? ContentDigest { get; set; }
    public string? DeletedAt { get; set; }
    public byte[]? AuthenticationTag { get; set; }

    public CredentialRecord Clone() => (CredentialRecord)MemberwiseClone();

    public static string Timestamp(DateTimeOffset value)
        => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseTimestamp(string value, string failureMessage)
    {
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            throw VaultException.Database(failureMessage);
        }
        return parsed;
    }
}

internal sealed class ConfigRecord
{
    public required string Key { get; init; }
    public required string Value { get; init; }
}

internal sealed class AgentWriteOperationRecord
{
    public required string OperationId { get; init; }
    public required string PayloadDigest { get; init; }
    public required string CredentialId { get; init; }
    public required string Operation { get; init; }
    public required string CommittedAt { get; init; }
    public required string RequestId { get; init; }
    public required string CapabilityDigest { get; init; }
    public string? ResultDigest { get; init; }
}

internal sealed class CredentialAccessRecord
{
    public required string Id { get; init; }
    public required byte[] EncryptedRecord { get; init; }
}
