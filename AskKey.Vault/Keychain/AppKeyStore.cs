using System.Security.Cryptography;

namespace AskKey.Vault;

public interface IAppKeyStore
{
    void SavePendingKey(byte[] data);
    byte[] LoadPendingKey();
    void PromotePendingKey();
    byte[] LoadAppKey();
    void DeleteLegacyKey();
    void DeletePendingKey();
    void DeleteAppKey();
}

/// <summary>
/// Windows replacement for the three-service Keychain migration chain. One
/// service name owns two DPAPI-protected slots: pending and app. The pending
/// slot lets first creation resume after a crash without ever promoting a key
/// whose database was not made durable.
/// </summary>
public sealed class DpapiAppKeyStore : IAppKeyStore
{
    public const string PendingFileName = "vault-key.pending";
    public const string AppFileName = "vault-key.app";

    private readonly string _directory;

    public DpapiAppKeyStore(string directory) => _directory = directory;

    public static DpapiAppKeyStore ForConfiguration(VaultConfiguration configuration)
        => new(configuration.KeyMaterialDirectory);

    public void SavePendingKey(byte[] data) => Save(data, PendingFileName, AppKeyStoreError.MissingPendingKey);
    public byte[] LoadPendingKey() => Load(PendingFileName, AppKeyStoreError.MissingPendingKey);
    public void PromotePendingKey() => Save(LoadPendingKey(), AppFileName, AppKeyStoreError.MissingAppKey);
    public byte[] LoadAppKey() => Load(AppFileName, AppKeyStoreError.MissingAppKey);

    // The collapsed Windows store has no legacy keychain service to migrate from.
    public void DeleteLegacyKey() { }
    public void DeletePendingKey() => Delete(PendingFileName);
    public void DeleteAppKey() => Delete(AppFileName);

    private void Save(byte[] data, string fileName, AppKeyStoreError missing)
    {
        if (data.Length != VaultCrypto.KeyByteCount) throw new AppKeyStoreException(AppKeyStoreError.ConflictingKey);
        PrivateFileSystem.CreatePrivateDirectory(_directory);
        var path = Path.Combine(_directory, fileName);
        if (File.Exists(path))
        {
            var existing = Load(fileName, missing);
            if (!CryptographicOperations.FixedTimeEquals(existing, data))
            {
                throw new AppKeyStoreException(AppKeyStoreError.ConflictingKey);
            }
            return;
        }
        var protectedBytes = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = PrivateFileSystem.CreatePrivateFile(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None))
            {
                stream.Write(protectedBytes, 0, protectedBytes.Length);
                stream.Flush(flushToDisk: true);
            }
            // Fail closed: never replace an existing key slot.
            File.Move(temporary, path, overwrite: false);
            PrivateFileSystem.ApplyFile(path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private byte[] Load(string fileName, AppKeyStoreError missing)
    {
        var path = Path.Combine(_directory, fileName);
        if (!File.Exists(path)) throw new AppKeyStoreException(missing);
        if (!PrivateFileSystem.IsPrivateDirectory(_directory) || !PrivateFileSystem.IsPrivateFile(path))
        {
            throw new AppKeyStoreException(AppKeyStoreError.SecurityFailure);
        }
        var protectedBytes = File.ReadAllBytes(path);
        byte[] data;
        try
        {
            data = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException exception)
        {
            throw new AppKeyStoreException(AppKeyStoreError.SecurityFailure, inner: exception);
        }
        if (data.Length != VaultCrypto.KeyByteCount) throw new AppKeyStoreException(AppKeyStoreError.ConflictingKey);
        return data;
    }

    private void Delete(string fileName)
    {
        var path = Path.Combine(_directory, fileName);
        if (File.Exists(path)) File.Delete(path);
    }
}

/// <summary>Test/debug store: a plain 32-byte key file inside an owner-only directory.</summary>
public sealed class IsolatedAppKeyStore : IAppKeyStore
{
    private readonly string _directory;

    public IsolatedAppKeyStore(string directory)
    {
        _directory = Path.Combine(directory, "key-material");
        if (Directory.Exists(_directory) && !PrivateFileSystem.IsPrivateDirectory(_directory))
        {
            throw new AppKeyStoreException(AppKeyStoreError.SecurityFailure);
        }
    }

    public void SavePendingKey(byte[] data) => Save(data, DpapiAppKeyStore.PendingFileName);
    public byte[] LoadPendingKey() => Load(DpapiAppKeyStore.PendingFileName, AppKeyStoreError.MissingPendingKey);
    public void PromotePendingKey() => Save(LoadPendingKey(), DpapiAppKeyStore.AppFileName);
    public byte[] LoadAppKey() => Load(DpapiAppKeyStore.AppFileName, AppKeyStoreError.MissingAppKey);
    public void DeleteLegacyKey() { }
    public void DeletePendingKey() => Delete(DpapiAppKeyStore.PendingFileName);
    public void DeleteAppKey() => Delete(DpapiAppKeyStore.AppFileName);

    private void Save(byte[] data, string fileName)
    {
        if (data.Length != VaultCrypto.KeyByteCount) throw new AppKeyStoreException(AppKeyStoreError.ConflictingKey);
        PrivateFileSystem.CreatePrivateDirectory(_directory);
        var path = Path.Combine(_directory, fileName);
        if (File.Exists(path))
        {
            if (!Load(fileName, AppKeyStoreError.ConflictingKey).AsSpan().SequenceEqual(data))
            {
                throw new AppKeyStoreException(AppKeyStoreError.ConflictingKey);
            }
            return;
        }
        using var stream = PrivateFileSystem.CreatePrivateFile(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(data, 0, data.Length);
        stream.Flush(flushToDisk: true);
    }

    private byte[] Load(string fileName, AppKeyStoreError missing)
    {
        var path = Path.Combine(_directory, fileName);
        if (!File.Exists(path)) throw new AppKeyStoreException(missing);
        if (!PrivateFileSystem.IsPrivateDirectory(_directory) || !PrivateFileSystem.IsPrivateFile(path))
        {
            throw new AppKeyStoreException(AppKeyStoreError.SecurityFailure);
        }
        var data = File.ReadAllBytes(path);
        if (data.Length != VaultCrypto.KeyByteCount) throw new AppKeyStoreException(AppKeyStoreError.ConflictingKey);
        return data;
    }

    private void Delete(string fileName)
    {
        var path = Path.Combine(_directory, fileName);
        if (File.Exists(path)) File.Delete(path);
    }
}

/// <summary>In-memory key store used by tests and synthetic bootstrap fixtures.</summary>
public sealed class MemoryAppKeyStore : IAppKeyStore
{
    public byte[]? PendingKey { get; set; }
    public byte[]? AppKey { get; set; }
    public int Mutations { get; set; }
    public Exception? PromotionFailure { get; set; }

    public void SavePendingKey(byte[] data)
    {
        if (PendingKey is not null && !PendingKey.AsSpan().SequenceEqual(data))
        {
            throw new AppKeyStoreException(AppKeyStoreError.ConflictingKey);
        }
        PendingKey = data;
        Mutations++;
    }

    public byte[] LoadPendingKey()
        => PendingKey ?? throw new AppKeyStoreException(AppKeyStoreError.MissingPendingKey);

    public void PromotePendingKey()
    {
        if (PromotionFailure is not null) throw PromotionFailure;
        AppKey = LoadPendingKey();
        Mutations++;
    }

    public byte[] LoadAppKey()
        => AppKey ?? throw new AppKeyStoreException(AppKeyStoreError.MissingAppKey);

    public void DeleteLegacyKey() { }

    public void DeletePendingKey()
    {
        if (PendingKey is null) return;
        PendingKey = null;
        Mutations++;
    }

    public void DeleteAppKey()
    {
        if (AppKey is null) return;
        AppKey = null;
        Mutations++;
    }
}
