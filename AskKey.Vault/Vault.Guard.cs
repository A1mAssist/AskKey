using System.Security.Cryptography;

namespace AskKey.Vault;

public sealed partial class Vault
{
    /// <summary>True when a Windows Hello guard copy of the vault key exists.</summary>
    public bool IsGuardUnlockEnabled => GuardAppKeyStore.IsEnabled(_configuration.KeyMaterialDirectory);

    /// <summary>The Hello key name in the guard copy, or null when guard unlock is disabled.</summary>
    public string? GuardHelloKeyName
        => IsGuardUnlockEnabled ? GuardAppKeyStore.ReadHelloKeyName(_configuration.KeyMaterialDirectory) : null;

    /// <summary>True when a passphrase-wrapped copy of the vault key exists.</summary>
    public bool IsPassphraseProtected => PassphraseAppKeyStore.IsEnabled(_configuration.KeyMaterialDirectory);

    /// <summary>Opens the library with the per-user KEK released by the guard service.</summary>
    public void UnlockWithGuard(byte[] kek)
    {
        lock (_lifecycleLock)
        {
            RecoverInterruptedLocalErase();
            if (Key is not null && _store is not null) return;
            var directory = _configuration.KeyMaterialDirectory;
            if (!GuardAppKeyStore.IsEnabled(directory))
            {
                throw new AppKeyStoreException(AppKeyStoreError.MissingAppKey);
            }
            using var guardStore = new GuardAppKeyStore(directory, kek);
            var (store, key) = VaultBootstrap.OpenCurrent(BootstrapPaths, guardStore);
            Adopt(store, key);
        }
    }

    /// <summary>Binds guard enrollment to the current vault key.</summary>
    public byte[] CreateGuardEnrollmentProof()
        => HMACSHA256.HashData(RequireKey(), "AskKey Guard enroll v1"u8);

    /// <summary>Writes the guard copy while leaving the passphrase copy untouched.</summary>
    public void EnableGuardUnlock(byte[] kek, string helloKeyName)
    {
        lock (_lifecycleLock)
        {
            var key = RequireKey();
            GuardAppKeyStore.Write(_configuration.KeyMaterialDirectory, key, kek, helloKeyName);
        }
    }

    /// <summary>Removes the guard copy. The app deletes the Windows Hello key separately.</summary>
    public void DisableGuardUnlock()
    {
        lock (_lifecycleLock)
        {
            GuardAppKeyStore.Delete(_configuration.KeyMaterialDirectory);
        }
    }
}
