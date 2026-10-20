using System.Security.Cryptography;

namespace AskKey.Vault;

/// <summary>
/// The Windows Hello guard copy of the vault key. The guard service releases a
/// per-user 32-byte KEK only after a fresh Hello confirmation; this file is that
/// KEK's AES-GCM wrapping of the unchanged vault key. It never replaces the
/// passphrase copy, which remains the recovery path.
/// </summary>
public sealed class GuardAppKeyStore : IAppKeyStore, IDisposable
{
    public const string AppFileName = "vault-key.app.guard";

    private const byte FormatVersion = 1;
    private const int NonceByteCount = 12;
    private const int TagByteCount = 16;
    private static readonly byte[] Magic = "AKGD"u8.ToArray();

    private readonly string _directory;
    private readonly byte[] _kek;
    private bool _disposed;

    public GuardAppKeyStore(string directory, byte[] kek)
    {
        if (kek is not { Length: VaultCrypto.KeyByteCount })
        {
            throw new AppKeyStoreException(AppKeyStoreError.SecurityFailure);
        }
        _directory = directory;
        _kek = (byte[])kek.Clone();
    }

    public static bool IsEnabled(string directory) => File.Exists(Path.Combine(directory, AppFileName));

    public static void Write(string directory, byte[] key, byte[] kek)
    {
        if (key is not { Length: VaultCrypto.KeyByteCount })
        {
            throw new AppKeyStoreException(AppKeyStoreError.ConflictingKey);
        }
        if (kek is not { Length: VaultCrypto.KeyByteCount })
        {
            throw new AppKeyStoreException(AppKeyStoreError.SecurityFailure);
        }

        var nonce = RandomNumberGenerator.GetBytes(NonceByteCount);
        var tag = new byte[TagByteCount];
        var wrapped = new byte[VaultCrypto.KeyByteCount];
        using (var aes = new AesGcm(kek, TagByteCount))
        {
            aes.Encrypt(nonce, key, wrapped, tag);
        }

        var payload = new byte[Magic.Length + 1 + NonceByteCount + TagByteCount + wrapped.Length];
        Magic.CopyTo(payload, 0);
        payload[4] = FormatVersion;
        var nonceOffset = Magic.Length + 1;
        nonce.CopyTo(payload.AsSpan(nonceOffset));
        tag.CopyTo(payload.AsSpan(nonceOffset + NonceByteCount));
        wrapped.CopyTo(payload.AsSpan(nonceOffset + NonceByteCount + TagByteCount));
        WritePayload(directory, payload);
    }

    public static void Delete(string directory)
    {
        var path = Path.Combine(directory, AppFileName);
        if (File.Exists(path)) File.Delete(path);
    }

    public void SavePendingKey(byte[] data)
        => throw new NotSupportedException("The guard store only wraps an existing vault key.");

    public byte[] LoadPendingKey() => throw new AppKeyStoreException(AppKeyStoreError.MissingPendingKey);

    public void PromotePendingKey() => throw new AppKeyStoreException(AppKeyStoreError.MissingPendingKey);

    public byte[] LoadAppKey()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var payload = ReadPayload(_directory);
        var nonceOffset = Magic.Length + 1;
        var tagOffset = nonceOffset + NonceByteCount;
        var wrappedOffset = tagOffset + TagByteCount;
        var key = new byte[VaultCrypto.KeyByteCount];
        try
        {
            using var aes = new AesGcm(_kek, TagByteCount);
            aes.Decrypt(payload.AsSpan(nonceOffset, NonceByteCount),
                payload.AsSpan(wrappedOffset, VaultCrypto.KeyByteCount),
                payload.AsSpan(tagOffset, TagByteCount), key);
        }
        catch (CryptographicException exception)
        {
            throw new AppKeyStoreException(AppKeyStoreError.SecurityFailure, inner: exception);
        }
        return key;
    }

    public void DeleteLegacyKey() { }

    public void DeletePendingKey() { }

    public void DeleteAppKey() => Delete(_directory);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_kek);
    }

    private static byte[] ReadPayload(string directory)
    {
        var path = Path.Combine(directory, AppFileName);
        if (!File.Exists(path)) throw new AppKeyStoreException(AppKeyStoreError.MissingAppKey);
        if (!PrivateFileSystem.IsPrivateDirectory(directory) || !PrivateFileSystem.IsPrivateFile(path))
        {
            throw new AppKeyStoreException(AppKeyStoreError.SecurityFailure);
        }
        var payload = File.ReadAllBytes(path);
        if (payload.Length != Magic.Length + 1 + NonceByteCount + TagByteCount + VaultCrypto.KeyByteCount
            || !payload.AsSpan(0, Magic.Length).SequenceEqual(Magic)
            || payload[4] != FormatVersion)
        {
            throw new AppKeyStoreException(AppKeyStoreError.SecurityFailure);
        }
        return payload;
    }

    private static void WritePayload(string directory, byte[] payload)
    {
        PrivateFileSystem.CreatePrivateDirectory(directory);
        var path = Path.Combine(directory, AppFileName);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = PrivateFileSystem.CreatePrivateFile(temporary, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None))
            {
                stream.Write(payload, 0, payload.Length);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            PrivateFileSystem.ApplyFile(path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
