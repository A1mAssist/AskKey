using AskKey.Vault;

namespace AskKey.Vault.Tests;

public sealed class PassphraseAppKeyStoreTests
{
    private sealed class ScriptedProvider : IPassphraseProvider
    {
        private readonly Queue<string?> _answers;
        public int Requests { get; private set; }

        public ScriptedProvider(params string?[] answers) => _answers = new Queue<string?>(answers);

        public string? Request(PassphrasePurpose purpose)
        {
            Requests++;
            return _answers.Count > 0 ? _answers.Dequeue() : null;
        }
    }

    private sealed class FailingDeleteKeyStore : IAppKeyStore
    {
        public void SavePendingKey(byte[] data) { }
        public byte[] LoadPendingKey() => throw new AppKeyStoreException(AppKeyStoreError.MissingPendingKey);
        public void PromotePendingKey() { }
        public byte[] LoadAppKey() => throw new AppKeyStoreException(AppKeyStoreError.MissingAppKey);
        public void DeleteLegacyKey() { }
        public void DeletePendingKey() => throw new IOException("synthetic DPAPI delete failure");
        public void DeleteAppKey() { }
    }
    [Fact]
    public void RoundTripsPendingAndAppKeys()
    {
        using var root = new TestDirectory();
        var directory = root.Combine("key-material");
        var key = TestCredentials.Key(11);
        var store = new PassphraseAppKeyStore(directory, new ScriptedProvider("correct horse battery"));

        store.SavePendingKey(key);
        Assert.Equal(key, store.LoadPendingKey());
        store.PromotePendingKey();
        Assert.Equal(key, store.LoadAppKey());
        Assert.True(PassphraseAppKeyStore.IsEnabled(directory));

        // A fresh store re-derives from the passphrase, which is what a restart does.
        var reloaded = new PassphraseAppKeyStore(directory, new ScriptedProvider("correct horse battery"));
        Assert.Equal(key, reloaded.LoadAppKey());
    }

    [Fact]
    public void WrongPassphraseFailsClosed()
    {
        using var root = new TestDirectory();
        var directory = root.Combine("key-material");
        PassphraseAppKeyStore.Write(directory, PassphraseAppKeyStore.AppFileName, TestCredentials.Key(12), "right");

        var store = new PassphraseAppKeyStore(directory, new ScriptedProvider("wrong"));
        Assert.Equal(AppKeyStoreError.SecurityFailure,
            Assert.Throws<AppKeyStoreException>(() => store.LoadAppKey()).Error);
    }

    [Fact]
    public void CancelledPromptIsNotTreatedAsAnEmptyPassphrase()
    {
        using var root = new TestDirectory();
        var directory = root.Combine("key-material");
        var store = new PassphraseAppKeyStore(directory, new ScriptedProvider());

        Assert.Equal(AppKeyStoreError.PassphraseCancelled,
            Assert.Throws<AppKeyStoreException>(() => store.SavePendingKey(TestCredentials.Key(13))).Error);
        Assert.False(File.Exists(Path.Combine(directory, PassphraseAppKeyStore.PendingFileName)));
    }

    [Fact]
    public void WrappedFileDoesNotContainTheRawKey()
    {
        using var root = new TestDirectory();
        var directory = root.Combine("key-material");
        var key = TestCredentials.Key(14);
        PassphraseAppKeyStore.Write(directory, PassphraseAppKeyStore.AppFileName, key, "passphrase-123");

        var bytes = File.ReadAllBytes(Path.Combine(directory, PassphraseAppKeyStore.AppFileName));
        Assert.Equal(-1, IndexOf(bytes, key));
        Assert.True(PrivateFileSystem.IsPrivateFile(Path.Combine(directory, PassphraseAppKeyStore.AppFileName)));
    }

    [Fact]
    public void IsEnabledOnlyTurnsOnWithTheAppSlot()
    {
        using var root = new TestDirectory();
        var directory = root.Combine("key-material");
        Assert.False(PassphraseAppKeyStore.IsEnabled(directory));

        var store = new PassphraseAppKeyStore(directory, new ScriptedProvider("passphrase-123"));
        store.SavePendingKey(TestCredentials.Key(15));
        Assert.False(PassphraseAppKeyStore.IsEnabled(directory));

        store.PromotePendingKey();
        Assert.True(PassphraseAppKeyStore.IsEnabled(directory));

        PassphraseAppKeyStore.Delete(directory);
        Assert.False(PassphraseAppKeyStore.IsEnabled(directory));
    }

    [Fact]
    public void VerifyRejectsAMismatchedKey()
    {
        using var root = new TestDirectory();
        var directory = root.Combine("key-material");
        var key = TestCredentials.Key(16);
        PassphraseAppKeyStore.Write(directory, PassphraseAppKeyStore.AppFileName, key, "passphrase-123");

        PassphraseAppKeyStore.Verify(directory, key, "passphrase-123");
        Assert.Equal(AppKeyStoreError.ConflictingKey,
            Assert.Throws<AppKeyStoreException>(() =>
                PassphraseAppKeyStore.Verify(directory, TestCredentials.Key(17), "passphrase-123")).Error);
        Assert.Equal(AppKeyStoreError.SecurityFailure,
            Assert.Throws<AppKeyStoreException>(() =>
                PassphraseAppKeyStore.Verify(directory, key, "wrong")).Error);
    }

    [Fact]
    public void HighSecurityModeSurvivesRestartAndRequiresThePassphrase()
    {
        using var root = new TestDirectory();
        var config = new VaultConfiguration(root.Combine("appdata"), root.Combine("local"));
        PrivateFileSystem.CreatePrivateDirectory(config.ApplicationSupportDirectory);
        var key = TestCredentials.Key(60);

        var first = new Vault(TestCredentials.NewStore(config.VaultFile, key), key, null, null,
            root.Combine("deliveries"), config, new DpapiAppKeyStore(config.KeyMaterialDirectory));
        first.EnableHighSecurity("correct horse battery");
        Assert.True(first.IsHighSecurityEnabled);
        first.Dispose();

        Assert.False(File.Exists(Path.Combine(config.KeyMaterialDirectory, DpapiAppKeyStore.AppFileName)));
        Assert.True(File.Exists(Path.Combine(config.KeyMaterialDirectory, PassphraseAppKeyStore.AppFileName)));

        // A restart has no passphrase yet, so the vault must stay closed.
        var restarted = new Vault(TestCredentials.NewStore(root.Combine("dummy.db"), key), null, null, null,
            root.Combine("deliveries2"), config, new DpapiAppKeyStore(config.KeyMaterialDirectory));
        Assert.Equal(AppKeyStoreError.PassphraseRequired,
            Assert.Throws<AppKeyStoreException>(() => restarted.Unlock()).Error);
        Assert.True(restarted.IsLocked);

        restarted.Unlock("correct horse battery");
        Assert.False(restarted.IsLocked);

        restarted.DisableHighSecurity("correct horse battery");
        Assert.False(restarted.IsHighSecurityEnabled);
        Assert.True(File.Exists(Path.Combine(config.KeyMaterialDirectory, DpapiAppKeyStore.AppFileName)));
        restarted.Dispose();
    }
    [Fact]
    public void DisablingHighSecurityRemovesTheGuardCopyToo()
    {
        using var root = new TestDirectory();
        var config = new VaultConfiguration(root.Combine("appdata"), root.Combine("local"));
        PrivateFileSystem.CreatePrivateDirectory(config.ApplicationSupportDirectory);
        var key = TestCredentials.Key(61);
        var vault = new Vault(TestCredentials.NewStore(config.VaultFile, key), key, null, null,
            root.Combine("deliveries"), config, new DpapiAppKeyStore(config.KeyMaterialDirectory));

        vault.EnableHighSecurity("correct horse battery");
        vault.EnableGuardUnlock(new byte[32]);
        var guardPath = Path.Combine(config.KeyMaterialDirectory, GuardAppKeyStore.AppFileName);
        Assert.True(File.Exists(guardPath));

        vault.DisableHighSecurity("correct horse battery");
        Assert.False(File.Exists(guardPath));
        Assert.False(File.Exists(Path.Combine(config.KeyMaterialDirectory, PassphraseAppKeyStore.AppFileName)));
        Assert.True(File.Exists(Path.Combine(config.KeyMaterialDirectory, DpapiAppKeyStore.AppFileName)));
        Assert.False(vault.IsHighSecurityEnabled);
        vault.Dispose();
    }

    [Fact]
    public void ACorruptIterationCountIsRejectedInsteadOfDeriving()
    {
        using var root = new TestDirectory();
        var directory = root.Combine("key-material");
        var key = TestCredentials.Key(63);
        PassphraseAppKeyStore.Write(directory, PassphraseAppKeyStore.AppFileName, key, "passphrase-123");

        // Rewrite the iteration field to a value no writer would produce.
        var path = Path.Combine(directory, PassphraseAppKeyStore.AppFileName);
        var bytes = File.ReadAllBytes(path);
        BitConverter.GetBytes(uint.MaxValue).CopyTo(bytes, 21);
        File.WriteAllBytes(path, bytes);

        var store = new PassphraseAppKeyStore(directory, new ScriptedProvider("passphrase-123"));
        Assert.Equal(AppKeyStoreError.SecurityFailure,
            Assert.Throws<AppKeyStoreException>(() => store.LoadAppKey()).Error);
    }

    [Fact]
    public void AForgottenPassphraseLeavesTheLibraryUnreachable()
    {
        using var root = new TestDirectory();
        var config = new VaultConfiguration(root.Combine("appdata"), root.Combine("local"));
        PrivateFileSystem.CreatePrivateDirectory(config.ApplicationSupportDirectory);
        var key = TestCredentials.Key(61);

        using (var vault = new Vault(TestCredentials.NewStore(config.VaultFile, key), key, null, null,
            root.Combine("deliveries"), config, new DpapiAppKeyStore(config.KeyMaterialDirectory)))
        {
            vault.EnableHighSecurity("correct horse battery");
        }

        // There is no DPAPI copy left to fall back to: the library is gone for good.
        var restarted = new Vault(TestCredentials.NewStore(root.Combine("dummy.db"), key), null, null, null,
            root.Combine("deliveries2"), config, new DpapiAppKeyStore(config.KeyMaterialDirectory));
        Assert.Equal(AppKeyStoreError.SecurityFailure,
            Assert.Throws<AppKeyStoreException>(() => restarted.Unlock("a different passphrase")).Error);
        Assert.True(restarted.IsLocked);
        restarted.Dispose();
    }

    [Fact]
    public void FailedDpapiRemovalLeavesHighSecurityOff()
    {
        using var root = new TestDirectory();
        var config = new VaultConfiguration(root.Combine("appdata"), root.Combine("local"));
        PrivateFileSystem.CreatePrivateDirectory(config.ApplicationSupportDirectory);
        var key = TestCredentials.Key(64);
        using var vault = new Vault(TestCredentials.NewStore(config.VaultFile, key), key, null, null,
            root.Combine("deliveries"), config, new FailingDeleteKeyStore());

        Assert.Throws<IOException>(() => vault.EnableHighSecurity("correct horse battery"));

        Assert.False(vault.IsHighSecurityEnabled);
        Assert.False(File.Exists(Path.Combine(config.KeyMaterialDirectory,
            PassphraseAppKeyStore.AppFileName)));
    }

    [Fact]
    public void HighSecurityUnlockRemovesLeftoverDpapiCopies()
    {
        using var root = new TestDirectory();
        var config = new VaultConfiguration(root.Combine("appdata"), root.Combine("local"));
        PrivateFileSystem.CreatePrivateDirectory(config.ApplicationSupportDirectory);
        var key = TestCredentials.Key(65);
        using (var first = new Vault(TestCredentials.NewStore(config.VaultFile, key), key, null, null,
                   root.Combine("deliveries"), config, new DpapiAppKeyStore(config.KeyMaterialDirectory)))
        {
            first.EnableHighSecurity("correct horse battery");
        }

        var dpapi = new DpapiAppKeyStore(config.KeyMaterialDirectory);
        dpapi.SavePendingKey(key);
        dpapi.PromotePendingKey();

        using var restarted = new Vault(TestCredentials.NewStore(root.Combine("dummy.db"), key), null, null, null,
            root.Combine("deliveries2"), config, dpapi);
        restarted.Unlock("correct horse battery");

        Assert.False(File.Exists(Path.Combine(config.KeyMaterialDirectory, DpapiAppKeyStore.PendingFileName)));
        Assert.False(File.Exists(Path.Combine(config.KeyMaterialDirectory, DpapiAppKeyStore.AppFileName)));
    }
    [Fact]
    public void LockRemovesRuntimeFileDeliveries()
    {
        using var root = new TestDirectory();
        var config = new VaultConfiguration(root.Combine("appdata"), root.Combine("local"));
        PrivateFileSystem.CreatePrivateDirectory(config.ApplicationSupportDirectory);
        var key = TestCredentials.Key(62);
        var store = TestCredentials.NewStore(config.VaultFile, key);

        using var vault = new Vault(store, key, null, null, root.Combine("deliveries"), config,
            new MemoryAppKeyStore { AppKey = key });
        var delivery = vault.FileDeliveryManager.Get().Materialize("credential-1", new byte[] { 65, 66, 67 });
        var path = delivery.Path;
        Assert.True(File.Exists(path));

        vault.Lock();

        // The Allow path leaves plaintext on disk; locking must take it away.
        Assert.False(File.Exists(path));
        Assert.Empty(vault.FileDeliveryManager.Get().CleanupFailures);
    }

    [Fact]
    public void RewriteChangesThePassphraseButKeepsTheSameKey()
    {
        using var root = new TestDirectory();
        var directory = root.Combine("key-material");
        var key = TestCredentials.Key(66);
        PassphraseAppKeyStore.Write(directory, PassphraseAppKeyStore.AppFileName, key, "old-pass-123");

        PassphraseAppKeyStore.Rewrite(directory, key, "old-pass-123", "new-pass-456");

        var oldStore = new PassphraseAppKeyStore(directory, new ScriptedProvider("old-pass-123"));
        Assert.Equal(AppKeyStoreError.SecurityFailure,
            Assert.Throws<AppKeyStoreException>(() => oldStore.LoadAppKey()).Error);
        var newStore = new PassphraseAppKeyStore(directory, new ScriptedProvider("new-pass-456"));
        Assert.Equal(key, newStore.LoadAppKey());
    }

    [Fact]
    public void RewriteWithTheWrongCurrentPassphraseLeavesTheFileUntouched()
    {
        using var root = new TestDirectory();
        var directory = root.Combine("key-material");
        var key = TestCredentials.Key(67);
        PassphraseAppKeyStore.Write(directory, PassphraseAppKeyStore.AppFileName, key, "old-pass-123");
        var path = Path.Combine(directory, PassphraseAppKeyStore.AppFileName);
        var before = File.ReadAllBytes(path);

        Assert.Equal(AppKeyStoreError.SecurityFailure,
            Assert.Throws<AppKeyStoreException>(() =>
                PassphraseAppKeyStore.Rewrite(directory, key, "wrong-pass", "new-pass-456")).Error);

        Assert.Equal(before, File.ReadAllBytes(path));
        var store = new PassphraseAppKeyStore(directory, new ScriptedProvider("old-pass-123"));
        Assert.Equal(key, store.LoadAppKey());
    }

    [Fact]
    public void VaultChangesThePassphraseAndPreservesTheGuardCopy()
    {
        using var root = new TestDirectory();
        var config = new VaultConfiguration(root.Combine("appdata"), root.Combine("local"));
        PrivateFileSystem.CreatePrivateDirectory(config.ApplicationSupportDirectory);
        var key = TestCredentials.Key(68);

        using (var vault = new Vault(TestCredentials.NewStore(config.VaultFile, key), key, null, null,
                   root.Combine("deliveries"), config, new DpapiAppKeyStore(config.KeyMaterialDirectory)))
        {
            vault.EnableHighSecurity("old-pass-123");
            vault.EnableGuardUnlock(TestCredentials.Key(123));
            vault.ChangePassphrase("old-pass-123", "new-pass-456");
            Assert.True(File.Exists(Path.Combine(config.KeyMaterialDirectory, GuardAppKeyStore.AppFileName)));
        }

        var restarted = new Vault(TestCredentials.NewStore(root.Combine("dummy.db"), key), null, null, null,
            root.Combine("deliveries2"), config, new DpapiAppKeyStore(config.KeyMaterialDirectory));
        Assert.Equal(AppKeyStoreError.SecurityFailure,
            Assert.Throws<AppKeyStoreException>(() => restarted.Unlock("old-pass-123")).Error);
        restarted.Unlock("new-pass-456");
        Assert.False(restarted.IsLocked);
        restarted.Dispose();
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        }
        return -1;
    }
}
