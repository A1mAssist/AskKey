using System.Security.Cryptography;
using System.Text;

namespace AskKey.Vault;

/// <summary>
/// HMAC-SHA256 over a domain prefix plus canonical JSON, keyed by an HKDF-derived
/// authentication key. The tag is [version 1] + 32 bytes.
/// </summary>
public static class CredentialRecordAuthentication
{
    private const byte Version = 1;
    private static readonly byte[] DomainPrefix = Encoding.UTF8.GetBytes("AskKey credential record v1\0");
    private static readonly byte[] Info = Encoding.UTF8.GetBytes("AskKey credential authentication v1");

    public static CredentialRecord Seal(CredentialRecord record, ReadOnlySpan<byte> vaultKey)
    {
        var tag = ComputeTag(record, vaultKey);
        var sealedRecord = record.Clone();
        sealedRecord.AuthenticationTag = new byte[1 + tag.Length];
        sealedRecord.AuthenticationTag[0] = Version;
        tag.CopyTo(sealedRecord.AuthenticationTag, 1);
        return sealedRecord;
    }

    public static void Verify(CredentialRecord record, ReadOnlySpan<byte> vaultKey)
    {
        if (record.AuthenticationTag is not { } tag)
        {
            throw new CredentialRecordAuthenticationException(CredentialRecordAuthenticationError.MissingAuthentication);
        }
        if (tag.Length != 33 || tag[0] != Version)
        {
            throw new CredentialRecordAuthenticationException(CredentialRecordAuthenticationError.InvalidAuthentication);
        }
        var expected = ComputeTag(record, vaultKey);
        if (!CryptographicOperations.FixedTimeEquals(tag.AsSpan(1), expected))
        {
            throw new CredentialRecordAuthenticationException(CredentialRecordAuthenticationError.InvalidAuthentication);
        }
    }

    public static byte[] AuthenticationKey(ReadOnlySpan<byte> vaultKey)
        => HKDF.DeriveKey(HashAlgorithmName.SHA256, vaultKey.ToArray(), 32, Array.Empty<byte>(), Info);

    private static byte[] ComputeTag(CredentialRecord record, ReadOnlySpan<byte> vaultKey)
    {
        var unsealed = record.Clone();
        unsealed.AuthenticationTag = null;
        var json = CanonicalJson.CredentialRecord(unsealed);
        var bytes = new byte[DomainPrefix.Length + json.Length];
        DomainPrefix.CopyTo(bytes, 0);
        json.CopyTo(bytes, DomainPrefix.Length);
        using var hmac = new HMACSHA256(AuthenticationKey(vaultKey));
        return hmac.ComputeHash(bytes);
    }
}

