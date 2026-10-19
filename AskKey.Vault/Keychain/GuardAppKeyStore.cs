using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AskKey.Vault;

/// <summary>
/// The Windows Hello guard copy of the vault key. The guard service releases a
/// per-user 32-byte KEK only after a fresh Hello signature; this file is that
/// KEK's AES-GCM wrapping of the unchanged vault key. It never replaces the
/// passphrase copy, which remains the recovery path.
/// </summary>
public sealed class GuardAppKeyStore : IAppKeyStore, IDisposable
{
    public const string AppFileName = "vault-key.app.guard";

    private const byte FormatVersion = 1;
    private const int NonceByteCount = 12;
    private const int TagByteCount = 16;
    private const int KeyNameLengthOffset = 5;
    private const int KeyNameOffset = 7;
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

    /// <summary>Reads the Hello key name without decrypting the vault key.</summary>
    public static string ReadHelloKeyName(string directory) => Parse(ReadPayload(directory)).KeyName;

    public static void Write(string directory, byte[] key, byte[] kek, string helloKeyName)
    {
        if (key is not { Length: VaultCrypto.KeyByteCount })
        {
            throw new AppKeyStoreException(AppKeyStoreError.ConflictingKey);
        }
        if (kek is not { Length: VaultCrypto.KeyByteCount })
        {
            throw new AppKeyStoreException(AppKeyStoreError.SecurityFailure);
        }
        if (string.IsNullOrEmpty(helloKeyName))
        {
            throw new ArgumentException("A Windows Hello key name is required.", nameof(helloKeyName));
        }
        var name = Encoding.UTF8.GetBytes(helloKeyName);
        if (name.Length > ushort.MaxValue)
        {
            throw new ArgumentException("The Windows Hello key name is too long.", nameof(helloKeyName));
        }

        var nonce = RandomNumberGenerator.GetBytes(NonceByteCount);
        var tag = new byte[TagByteCount];
        var wrapped = new byte[VaultCrypto.KeyByteCount];
        using (var aes = new AesGcm(kek, TagByteCount))
        {
            aes.Encrypt(nonce, key, wrapped, tag);
        }

        var payload = new byte[KeyNameOffset + name.Length + NonceByteCount + TagByteCount + wrapped.Length];
        Magic.CopyTo(payload, 0);
        payload[4] = FormatVersion;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(KeyNameLengthOffset, 2), (ushort)name.Length);
        name.CopyTo(payload.AsSpan(KeyNameOffset));
        var nonceOffset = KeyNameOffset + name.Length;
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
        var (_, keyNameLength) = Parse(payload);
        var nonceOffset = KeyNameOffset + keyNameLength;
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
        return File.ReadAllBytes(path);
    }

    private static (string KeyName, int KeyNameByteCount) Parse(byte[] payload)
    {
        if (payload.Length < KeyNameOffset + NonceByteCount + TagByteCount + VaultCrypto.KeyByteCount
            || !payload.AsSpan(0, Magic.Length).SequenceEqual(Magic)
            || payload[4] != FormatVersion)
        {
            throw SecurityFailure();
        }
        var keyNameLength = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(KeyNameLengthOffset, 2));
        if (payload.Length != KeyNameOffset + keyNameLength + NonceByteCount + TagByteCount
            + VaultCrypto.KeyByteCount)
        {
            throw SecurityFailure();
        }
        string keyName;
        try
        {
            keyName = new UTF8Encoding(false, true).GetString(payload, KeyNameOffset, keyNameLength);
        }
        catch (DecoderFallbackException)
        {
            throw SecurityFailure();
        }
        if (keyName.Length == 0) throw SecurityFailure();
        return (keyName, keyNameLength);
    }

    private static AppKeyStoreException SecurityFailure(Exception? inner = null)
        => new(AppKeyStoreError.SecurityFailure, inner: inner);

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
