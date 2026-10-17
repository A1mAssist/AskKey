using System.Security.Cryptography;
using System.Text;

namespace AskKey.Vault;

/// <summary>AES-GCM-256 with CryptoKit's combined layout: nonce(12) || ciphertext || tag(16).</summary>
public static class VaultCrypto
{
    public const int KeyByteCount = 32;
    private const int NonceByteCount = 12;
    private const int TagByteCount = 16;

    public static byte[] GenerateKey() => RandomNumberGenerator.GetBytes(KeyByteCount);

    public static byte[] Encrypt(string value, ReadOnlySpan<byte> key) => Encrypt(Encoding.UTF8.GetBytes(value), key);

    public static byte[] Encrypt(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key)
    {
        RequireKey(key);
        var nonce = RandomNumberGenerator.GetBytes(NonceByteCount);
        var ciphertext = new byte[data.Length];
        var tag = new byte[TagByteCount];
        using var aes = new AesGcm(key, TagByteCount);
        aes.Encrypt(nonce, data, ciphertext, tag);
        var combined = new byte[NonceByteCount + ciphertext.Length + TagByteCount];
        nonce.CopyTo(combined, 0);
        ciphertext.CopyTo(combined, NonceByteCount);
        tag.CopyTo(combined, NonceByteCount + ciphertext.Length);
        return combined;
    }

    public static string Decrypt(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key)
    {
        var plaintext = DecryptData(data, key);
        try
        {
            return new UTF8Encoding(false, true).GetString(plaintext);
        }
        catch (DecoderFallbackException exception)
        {
            throw new VaultException(VaultErrorCode.DecryptionFailed,
                "Failed to decrypt secret value. The vault key may have changed.", inner: exception);
        }
    }

    public static byte[] DecryptData(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key)
    {
        RequireKey(key);
        if (data.Length < NonceByteCount + TagByteCount)
        {
            throw new VaultException(VaultErrorCode.DecryptionFailed,
                "Failed to decrypt secret value. The vault key may have changed.");
        }
        var nonce = data[..NonceByteCount];
        var tag = data[^TagByteCount..];
        var ciphertext = data[NonceByteCount..^TagByteCount];
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagByteCount);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return plaintext;
        }
        catch (CryptographicException exception)
        {
            throw new VaultException(VaultErrorCode.DecryptionFailed,
                "Failed to decrypt secret value. The vault key may have changed.", inner: exception);
        }
    }

    private static void RequireKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeyByteCount)
        {
            throw new VaultException(VaultErrorCode.DecryptionFailed,
                "Failed to decrypt secret value. The vault key may have changed.");
        }
    }
}
