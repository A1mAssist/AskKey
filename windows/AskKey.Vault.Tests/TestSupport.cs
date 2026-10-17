using System.Security.Cryptography;
using AskKey.Vault;

namespace AskKey.Vault.Tests;

internal sealed class TestDirectory : IDisposable
{
    public string Path { get; }

    public TestDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "AskKeyVaultTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Combine(params string[] parts)
        => System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal static class TestCredentials
{
    public static byte[] Key(byte seed = 1)
        => Enumerable.Range(0, VaultCrypto.KeyByteCount).Select(index => (byte)(seed + index)).ToArray();

    public static CredentialRecord Text(
        byte[] key,
        string name,
        string value,
        string usage = "",
        string notes = "",
        string? group = null,
        string? environment = null,
        string permission = "ask",
        string? expiresAt = null,
        string? id = null,
        DateTimeOffset? now = null)
    {
        var display = CredentialName.DisplayName(name);
        var timestamp = CredentialRecord.Timestamp(now ?? DateTimeOffset.UtcNow);
        return new CredentialRecord
        {
            Id = id ?? Guid.NewGuid().ToString().ToUpperInvariant(),
            NameIndex = CredentialIndex.Hash(CredentialName.Normalized(display), key),
            EncryptedDisplayName = VaultCrypto.Encrypt(display, key),
            EncryptedPayload = VaultCrypto.Encrypt(value, key),
            EncryptedUsageInstructions = VaultCrypto.Encrypt(usage, key),
            EncryptedPrivateNotes = VaultCrypto.Encrypt(notes, key),
            EncryptedGroupName = group is null ? null : VaultCrypto.Encrypt(group, key),
            EncryptedEnvironmentVariable = environment is null ? null : VaultCrypto.Encrypt(environment, key),
            PayloadKind = "text",
            Permission = permission,
            ExpiresAt = expiresAt,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
        };
    }

    public static VaultStore NewStore(string path, byte[] key)
    {
        var store = new VaultStore(path);
        store.BindCredentialAuthenticationKey(key);
        return store;
    }

    public static bool ContainsBytes(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0) return true;
        for (var index = 0; index <= haystack.Length - needle.Length; index++)
        {
            if (haystack.AsSpan(index, needle.Length).SequenceEqual(needle)) return true;
        }
        return false;
    }

    public static byte[] Sha256(byte[] value) => SHA256.HashData(value);
}
