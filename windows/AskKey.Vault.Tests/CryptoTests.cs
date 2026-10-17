using System.Security.Cryptography;
using System.Text;
using AskKey.Vault;

namespace AskKey.Vault.Tests;

public sealed class VaultCryptoTests
{
    [Fact]
    public void RoundTripsUtf8Text()
    {
        var key = TestCredentials.Key();
        var plaintext = "synthetic-秘密-🔐-\n\ttab";
        var combined = VaultCrypto.Encrypt(plaintext, key);
        Assert.Equal(plaintext, VaultCrypto.Decrypt(combined, key));
        Assert.NotEqual(Encoding.UTF8.GetBytes(plaintext), combined);
    }

    [Fact]
    public void UsesCombinedNonceCiphertextTagLayout()
    {
        var key = TestCredentials.Key();
        var plaintext = Encoding.UTF8.GetBytes("12345");
        var combined = VaultCrypto.Encrypt(plaintext, key);
        Assert.Equal(12 + plaintext.Length + 16, combined.Length);
        Assert.Equal(plaintext, VaultCrypto.DecryptData(combined, key));
    }

    [Fact]
    public void RoundTripsEmptyAndBinaryPayloads()
    {
        var key = TestCredentials.Key(7);
        Assert.Equal(Array.Empty<byte>(), VaultCrypto.DecryptData(VaultCrypto.Encrypt(Array.Empty<byte>(), key), key));
        var binary = new byte[] { 0, 1, 2, 255, 128, 0, 9 };
        Assert.Equal(binary, VaultCrypto.DecryptData(VaultCrypto.Encrypt(binary, key), key));
    }

    [Fact]
    public void RejectsWrongKey()
    {
        var combined = VaultCrypto.Encrypt("synthetic", TestCredentials.Key());
        var exception = Assert.Throws<VaultException>(() => VaultCrypto.Decrypt(combined, TestCredentials.Key(2)));
        Assert.Equal(VaultErrorCode.DecryptionFailed, exception.Code);
    }

    [Fact]
    public void RejectsTamperedCiphertextTagAndNonce()
    {
        var key = TestCredentials.Key();
        var combined = VaultCrypto.Encrypt("synthetic-value", key);
        for (var offset = 0; offset < combined.Length; offset++)
        {
            var tampered = (byte[])combined.Clone();
            tampered[offset] ^= 0x01;
            var exception = Assert.Throws<VaultException>(() => VaultCrypto.DecryptData(tampered, key));
            Assert.Equal(VaultErrorCode.DecryptionFailed, exception.Code);
        }
    }

    [Fact]
    public void RejectsShortDataAndInvalidKeyLength()
    {
        var key = TestCredentials.Key();
        foreach (var length in new[] { 0, 1, 27 })
        {
            var exception = Assert.Throws<VaultException>(() =>
                VaultCrypto.DecryptData(new byte[length], key));
            Assert.Equal(VaultErrorCode.DecryptionFailed, exception.Code);
        }
        Assert.Throws<VaultException>(() => VaultCrypto.Encrypt("x", new byte[31]));
    }

    [Fact]
    public void GeneratedKeysAreThirtyTwoRandomBytes()
    {
        var first = VaultCrypto.GenerateKey();
        var second = VaultCrypto.GenerateKey();
        Assert.Equal(32, first.Length);
        Assert.Equal(32, second.Length);
        Assert.NotEqual(first, second);
    }
}

public sealed class CredentialRecordAuthenticationTests
{
    private static readonly byte[] Key = TestCredentials.Key(3);

    [Fact]
    public void SealProducesVersionedThirtyThreeByteTagAndVerifies()
    {
        var record = TestCredentials.Text(Key, "SYNTHETIC-TOKEN", "value");
        var sealedRecord = CredentialRecordAuthentication.Seal(record, Key);
        Assert.Equal(33, sealedRecord.AuthenticationTag!.Length);
        Assert.Equal(1, sealedRecord.AuthenticationTag[0]);
        Assert.Null(record.AuthenticationTag);
        CredentialRecordAuthentication.Verify(sealedRecord, Key);
    }

    [Fact]
    public void MissingTagFailsClosed()
    {
        var record = TestCredentials.Text(Key, "SYNTHETIC-TOKEN", "value");
        var exception = Assert.Throws<CredentialRecordAuthenticationException>(
            () => CredentialRecordAuthentication.Verify(record, Key));
        Assert.Equal(CredentialRecordAuthenticationError.MissingAuthentication, exception.Error);
    }

    [Fact]
    public void WrongVersionAndWrongLengthFailAsInvalid()
    {
        var sealedRecord = CredentialRecordAuthentication.Seal(
            TestCredentials.Text(Key, "SYNTHETIC-TOKEN", "value"), Key);
        var wrongVersion = sealedRecord.Clone();
        wrongVersion.AuthenticationTag = (byte[])sealedRecord.AuthenticationTag!.Clone();
        wrongVersion.AuthenticationTag[0] = 2;
        Assert.Equal(CredentialRecordAuthenticationError.InvalidAuthentication,
            Assert.Throws<CredentialRecordAuthenticationException>(
                () => CredentialRecordAuthentication.Verify(wrongVersion, Key)).Error);

        var shortTag = sealedRecord.Clone();
        shortTag.AuthenticationTag = sealedRecord.AuthenticationTag![..32];
        Assert.Equal(CredentialRecordAuthenticationError.InvalidAuthentication,
            Assert.Throws<CredentialRecordAuthenticationException>(
                () => CredentialRecordAuthentication.Verify(shortTag, Key)).Error);
    }

    [Fact]
    public void TamperingAnyAuthenticatedFieldFails()
    {
        var sealedRecord = CredentialRecordAuthentication.Seal(
            TestCredentials.Text(Key, "SYNTHETIC-TOKEN", "value"), Key);
        var mutations = new Action<CredentialRecord>[]
        {
            record => record.Permission = "allowed",
            record => record.ExpiresAt = "2030-01-01T00:00:00Z",
            record => record.EncryptedPayload = VaultCrypto.Encrypt("other", Key),
            record => record.NameIndex = new byte[32],
            record => record.DeletedAt = "2020-01-01T00:00:00Z",
            record => record.ByteSize = 12,
        };
        foreach (var mutate in mutations)
        {
            var tampered = sealedRecord.Clone();
            mutate(tampered);
            Assert.Equal(CredentialRecordAuthenticationError.InvalidAuthentication,
                Assert.Throws<CredentialRecordAuthenticationException>(
                    () => CredentialRecordAuthentication.Verify(tampered, Key)).Error);
        }
    }

    [Fact]
    public void RelocatedCiphertextFromAnotherRecordFails()
    {
        var first = CredentialRecordAuthentication.Seal(TestCredentials.Text(Key, "ONE", "a"), Key);
        var second = CredentialRecordAuthentication.Seal(TestCredentials.Text(Key, "TWO", "b"), Key);
        var moved = first.Clone();
        moved.EncryptedPayload = second.EncryptedPayload;
        Assert.Throws<CredentialRecordAuthenticationException>(
            () => CredentialRecordAuthentication.Verify(moved, Key));
    }

    [Fact]
    public void DifferentVaultKeyFails()
    {
        var sealedRecord = CredentialRecordAuthentication.Seal(
            TestCredentials.Text(Key, "SYNTHETIC-TOKEN", "value"), Key);
        Assert.Throws<CredentialRecordAuthenticationException>(
            () => CredentialRecordAuthentication.Verify(sealedRecord, TestCredentials.Key(4)));
    }

    [Fact]
    public void AuthenticationKeyIsHkdfSha256WithDomainInfo()
    {
        var expected = HKDF.DeriveKey(HashAlgorithmName.SHA256, Key, 32, Array.Empty<byte>(),
            Encoding.UTF8.GetBytes("AskKey credential authentication v1"));
        Assert.Equal(expected, CredentialRecordAuthentication.AuthenticationKey(Key));
        Assert.NotEqual(Key, expected);
    }

    [Fact]
    public void CanonicalJsonIsSortedSnakeCaseAndOmitsNil()
    {
        var record = TestCredentials.Text(Key, "SYNTHETIC", "value", notes: "n");
        var json = Encoding.UTF8.GetString(CanonicalJson.CredentialRecord(record));
        Assert.StartsWith("{\"created_at\":", json);
        Assert.Contains("\"encrypted_display_name\":", json);
        Assert.Contains("\"name_index\":", json);
        Assert.DoesNotContain("createdAt", json);
        Assert.DoesNotContain("expires_at", json);
        Assert.DoesNotContain("deleted_at", json);
        Assert.DoesNotContain("byte_size", json);
        var keys = System.Text.Json.JsonDocument.Parse(json).RootElement.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(keys.OrderBy(name => name, StringComparer.Ordinal), keys);
    }
}
