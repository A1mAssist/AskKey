using System.Security.Cryptography;
using System.Text;

namespace AskKey.Vault;

public enum PassphrasePurpose
{
    Create,
    Unlock,
}

public interface IPassphraseProvider
{
    string? Request(PassphrasePurpose purpose);
}

/// <summary>Supplies one already-collected passphrase for a single unlock.</summary>
public sealed class FixedPassphraseProvider : IPassphraseProvider
{
    private readonly string? _passphrase;

    public FixedPassphraseProvider(string? passphrase) => _passphrase = passphrase;

    public string? Request(PassphrasePurpose purpose) => _passphrase;
}

/// <summary>
/// Opt-in high security mode. The random vault key is unchanged; it is wrapped with a
/// KEK derived from a user passphrase, so possessing the key files and the database is
/// not enough to decrypt the library. DPAPI stays the default store; this one is used
/// only while <c>vault-key.app.wrapped</c> exists.
/// </summary>
public sealed class PassphraseAppKeyStore : IAppKeyStore
{
    public const string PendingFileName = "vault-key.pending.wrapped";
    public const string AppFileName = "vault-key.app.wrapped";

    private const int SaltByteCount = 16;
    private const int NonceByteCount = 12;
    private const int TagByteCount = 16;
    // OWASP 2023 guidance for PBKDF2-HMAC-SHA256. Existing files keep the count they
    // were written with; raise it only by re-wrapping.
    private const int DefaultIterationCount = 600_000;
    // Ceiling for a header we did not write; anything above is corruption, not a choice.
    private const uint MaximumIterationCount = 10_000_000;
    private const byte FormatVersion = 1;
    private static readonly byte[] Magic = "AKPW"u8.ToArray();
    private const int HeaderByteCount = 4 + 1 + SaltByteCount + 4 + NonceByteCount + TagByteCount;

    private readonly string _directory;
    private readonly IPassphraseProvider _provider;
    private string? _cachedPassphrase;

    public PassphraseAppKeyStore(string directory, IPassphraseProvider provider)
    {
        _directory = directory;
        _provider = provider;
    }

    public static bool IsEnabled(string directory) => File.Exists(Path.Combine(directory, AppFileName));

    public void SavePendingKey(byte[] data) => Save(data, PendingFileName);
    public byte[] LoadPendingKey() => Load(PendingFileName, AppKeyStoreError.MissingPendingKey);
    public void PromotePendingKey() => Save(LoadPendingKey(), AppFileName);
    public byte[] LoadAppKey() => Load(AppFileName, AppKeyStoreError.MissingAppKey);

    // The collapsed Windows store has no legacy keychain service to migrate from.
    public void DeleteLegacyKey() { }
    public void DeletePendingKey() => Delete(PendingFileName);
    public void DeleteAppKey() => Delete(AppFileName);

    /// <summary>Drops the cached passphrase once the vault is open or the attempt failed.</summary>
    public void ClearSecret() => _cachedPassphrase = null;

    /// <summary>Wraps <paramref name="key"/> and writes it to the named slot.</summary>
    public static void Write(string directory, string fileName, byte[] key, string passphrase)
    {
        if (key.Length != VaultCrypto.KeyByteCount) throw new AppKeyStoreException(AppKeyStoreError.ConflictingKey);
        WritePayload(directory, fileName, Wrap(key, passphrase), overwrite: true);
    }

    /// <summary>
    /// Replaces the passphrase wrapping the App key. The current passphrase is checked
    /// first, and the new payload is verified before the old file is replaced.
    /// </summary>
    public static void Rewrite(string directory, byte[] key, string currentPassphrase, string newPassphrase)
    {
        if (key.Length != VaultCrypto.KeyByteCount) throw new AppKeyStoreException(AppKeyStoreError.ConflictingKey);
        if (string.IsNullOrEmpty(currentPassphrase) || string.IsNullOrEmpty(newPassphrase))
        {
            throw new ArgumentException("Both passphrases are required.", nameof(currentPassphrase));
        }

        Verify(directory, key, currentPassphrase);
        var payload = Wrap(key, newPassphrase);
        var check = Unwrap(payload, newPassphrase);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(check, key))
            {
                throw new AppKeyStoreException(AppKeyStoreError.ConflictingKey);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(check);
        }
        WritePayload(directory, AppFileName, payload, overwrite: true);
    }

    /// <summary>Confirms the passphrase opens the stored App key and that it matches.</summary>
    public static void Verify(string directory, byte[] expectedKey, string passphrase)
    {
        var store = new PassphraseAppKeyStore(directory, new FixedPassphraseProvider(passphrase));
        try
        {
            var key = store.LoadAppKey();
            if (!CryptographicOperations.FixedTimeEquals(key, expectedKey))
            {
                throw new AppKeyStoreException(AppKeyStoreError.ConflictingKey);
            }
        }
        finally
        {
            store.ClearSecret();
        }
    }

    public static void Delete(string directory)
    {
        foreach (var name in new[] { PendingFileName, AppFileName })
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private void Save(byte[] data, string fileName)
    {
        if (data.Length != VaultCrypto.KeyByteCount) throw new AppKeyStoreException(AppKeyStoreError.ConflictingKey);
        PrivateFileSystem.CreatePrivateDirectory(_directory);
        var path = Path.Combine(_directory, fileName);
        if (File.Exists(path))
        {
            // Fail closed: never replace a slot that already holds a different key.
            var existing = Load(fileName, AppKeyStoreError.MissingPendingKey);
            if (!CryptographicOperations.FixedTimeEquals(existing, data))
            {
                throw new AppKeyStoreException(AppKeyStoreError.ConflictingKey);
            }
            return;
        }

        WritePayload(_directory, fileName, Wrap(data, RequirePassphrase(PassphrasePurpose.Create)),
            overwrite: false);
    }

    private byte[] Load(string fileName, AppKeyStoreError missing)
    {
        var path = Path.Combine(_directory, fileName);
        if (!File.Exists(path)) throw new AppKeyStoreException(missing);
        if (!PrivateFileSystem.IsPrivateDirectory(_directory) || !PrivateFileSystem.IsPrivateFile(path))
        {
            throw new AppKeyStoreException(AppKeyStoreError.SecurityFailure);
        }

        return Unwrap(File.ReadAllBytes(path), RequirePassphrase(PassphrasePurpose.Unlock));
    }

    private static byte[] Unwrap(byte[] payload, string passphrase)
    {
        if (payload.Length != HeaderByteCount + VaultCrypto.KeyByteCount
            || !payload.AsSpan(0, Magic.Length).SequenceEqual(Magic)
            || payload[4] != FormatVersion)
        {
            throw new AppKeyStoreException(AppKeyStoreError.SecurityFailure);
        }

        var salt = payload.AsSpan(5, SaltByteCount);
        var iterations = BitConverter.ToUInt32(payload, 21);
        // A hand-edited or corrupt header must not turn into an int overflow or a
        // denial of service from an implausible iteration count.
        if (iterations is < 1 or > MaximumIterationCount)
        {
            throw new AppKeyStoreException(AppKeyStoreError.SecurityFailure);
        }
        var nonce = payload.AsSpan(25, NonceByteCount);
        var tag = payload.AsSpan(37, TagByteCount);
        var wrapped = payload.AsSpan(HeaderByteCount, VaultCrypto.KeyByteCount);

        var derived = DeriveKey(passphrase, salt, iterations);
        var key = new byte[VaultCrypto.KeyByteCount];
        try
        {
            using var aes = new AesGcm(derived, TagByteCount);
            aes.Decrypt(nonce, wrapped, tag, key);
        }
        catch (CryptographicException exception)
        {
            throw new AppKeyStoreException(AppKeyStoreError.SecurityFailure, inner: exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derived);
        }
        return key;
    }

    private static void WritePayload(string directory, string fileName, byte[] payload, bool overwrite)
    {
        PrivateFileSystem.CreatePrivateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = PrivateFileSystem.CreatePrivateFile(temporary, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None))
            {
                stream.Write(payload, 0, payload.Length);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite);
            PrivateFileSystem.ApplyFile(path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private string RequirePassphrase(PassphrasePurpose purpose)
    {
        if (_cachedPassphrase is not null) return _cachedPassphrase;
        var passphrase = _provider.Request(purpose);
        if (string.IsNullOrEmpty(passphrase)) throw new AppKeyStoreException(AppKeyStoreError.PassphraseCancelled);
        _cachedPassphrase = passphrase;
        return passphrase;
    }

    private static byte[] DeriveKey(string passphrase, ReadOnlySpan<byte> salt, uint iterations)
        => Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, (int)iterations,
            HashAlgorithmName.SHA256, VaultCrypto.KeyByteCount);

    private static byte[] Wrap(byte[] key, string passphrase)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltByteCount);
        var derived = DeriveKey(passphrase, salt, DefaultIterationCount);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceByteCount);
            var wrapped = new byte[key.Length];
            var tag = new byte[TagByteCount];
            using (var aes = new AesGcm(derived, TagByteCount))
            {
                aes.Encrypt(nonce, key, wrapped, tag);
            }

            var payload = new byte[HeaderByteCount + key.Length];
            Magic.CopyTo(payload, 0);
            payload[4] = FormatVersion;
            salt.CopyTo(payload.AsSpan(5));
            BitConverter.TryWriteBytes(payload.AsSpan(21, 4), DefaultIterationCount);
            nonce.CopyTo(payload.AsSpan(25));
            tag.CopyTo(payload.AsSpan(37));
            wrapped.CopyTo(payload.AsSpan(HeaderByteCount));
            return payload;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derived);
        }
    }
}
