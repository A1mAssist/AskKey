using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using AskKey.Vault;

namespace AskKey.Vault.Tests;

public sealed class GuardAppKeyStoreTests
{
    private const string HelloKeyName = "AskKey Guard";

    [Fact]
    public void RoundTripsTheVaultKeyAndWritesTheContractFormat()
    {
        using var root = new TestDirectory();
        var directory = root.Combine("key-material");
        var key = TestCredentials.Key(70);
        var kek = TestCredentials.Key(120);

        GuardAppKeyStore.Write(directory, key, kek, HelloKeyName);

        var path = Path.Combine(directory, GuardAppKeyStore.AppFileName);
        var payload = File.ReadAllBytes(path);
        var name = Encoding.UTF8.GetBytes(HelloKeyName);
        Assert.True(payload.AsSpan(0, 4).SequenceEqual("AKGD"u8));
        Assert.Equal(1, payload[4]);
        Assert.Equal((ushort)name.Length, BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(5, 2)));
        Assert.Equal(HelloKeyName, Encoding.UTF8.GetString(payload, 7, name.Length));
        Assert.Equal(7 + name.Length + 12 + 16 + 32, payload.Length);
        Assert.False(TestCredentials.ContainsBytes(payload, key));
        Assert.False(TestCredentials.ContainsBytes(payload, kek));
        Assert.True(PrivateFileSystem.IsPrivateFile(path));

        using var store = new GuardAppKeyStore(directory, kek);
        Assert.Equal(key, store.LoadAppKey());
        Assert.Equal(HelloKeyName, GuardAppKeyStore.ReadHelloKeyName(directory));
    }

    [Fact]
    public void WrongKekFailsClosed()
    {
        using var root = new TestDirectory();
        var directory = root.Combine("key-material");
        GuardAppKeyStore.Write(directory, TestCredentials.Key(71), TestCredentials.Key(121), HelloKeyName);

        using var store = new GuardAppKeyStore(directory, TestCredentials.Key(122));
        Assert.Equal(AppKeyStoreError.SecurityFailure,
            Assert.Throws<AppKeyStoreException>(() => store.LoadAppKey()).Error);
    }

    [Fact]
    public void MissingFileIsReportedAsMissingAppKey()
    {
        using var root = new TestDirectory();
        var directory = root.Combine("key-material");
        using var store = new GuardAppKeyStore(directory, TestCredentials.Key(120));

        Assert.Equal(AppKeyStoreError.MissingAppKey,
            Assert.Throws<AppKeyStoreException>(() => store.LoadAppKey()).Error);
        Assert.Equal(AppKeyStoreError.MissingAppKey,
            Assert.Throws<AppKeyStoreException>(() => GuardAppKeyStore.ReadHelloKeyName(directory)).Error);
    }

    [Fact]
    public void UnlockWithGuardDoesNotCreateALibraryWhenTheGuardFileIsMissing()
    {
        using var root = new TestDirectory();
        var config = new VaultConfiguration(root.Combine("appdata"), root.Combine("local"));
        PrivateFileSystem.CreatePrivateDirectory(config.ApplicationSupportDirectory);
        var key = TestCredentials.Key(77);
        using var vault = new Vault(TestCredentials.NewStore(root.Combine("dummy.db"), key), null, null, null,
            root.Combine("deliveries"), config, new MemoryAppKeyStore { AppKey = key });

        Assert.Equal(AppKeyStoreError.MissingAppKey,
            Assert.Throws<AppKeyStoreException>(() =>
                vault.UnlockWithGuard(TestCredentials.Key(129))).Error);
        Assert.True(vault.IsLocked);
        Assert.False(File.Exists(config.VaultFile));
    }

    [Fact]
    public void HelloKeyNameIsUtf8AndReadableWithoutTheKek()
    {
        using var root = new TestDirectory();
        var directory = root.Combine("key-material");
        const string name = "AskKey Guard · 你好";
        var key = TestCredentials.Key(72);
        var kek = TestCredentials.Key(123);
        GuardAppKeyStore.Write(directory, key, kek, name);

        Assert.Equal(name, GuardAppKeyStore.ReadHelloKeyName(directory));
        using var store = new GuardAppKeyStore(directory, kek);
        Assert.Equal(key, store.LoadAppKey());
    }

    [Fact]
    public void VaultUnlocksWithTheGuardCopyAndFallsBackToThePassphrase()
    {
        using var root = new TestDirectory();
        var config = new VaultConfiguration(root.Combine("appdata"), root.Combine("local"));
        PrivateFileSystem.CreatePrivateDirectory(config.ApplicationSupportDirectory);
        var key = TestCredentials.Key(73);
        var kek = TestCredentials.Key(124);
        const string passphrase = "correct horse battery";

        using (var first = new Vault(TestCredentials.NewStore(config.VaultFile, key), key, null, null,
                   root.Combine("deliveries"), config, new DpapiAppKeyStore(config.KeyMaterialDirectory)))
        {
            first.EnableHighSecurity(passphrase);
            first.EnableGuardUnlock(kek, HelloKeyName);
            Assert.True(first.IsGuardUnlockEnabled);
            Assert.True(first.IsPassphraseProtected);
            Assert.True(first.IsHighSecurityEnabled);
            Assert.Equal(HelloKeyName, first.GuardHelloKeyName);
            Assert.True(File.Exists(Path.Combine(config.KeyMaterialDirectory, PassphraseAppKeyStore.AppFileName)));
        }

        using var restarted = new Vault(TestCredentials.NewStore(root.Combine("dummy.db"), key), null, null, null,
            root.Combine("deliveries2"), config, new DpapiAppKeyStore(config.KeyMaterialDirectory));
        Assert.True(restarted.IsLocked);

        Assert.Equal(AppKeyStoreError.SecurityFailure,
            Assert.Throws<AppKeyStoreException>(() =>
                restarted.UnlockWithGuard(TestCredentials.Key(125))).Error);
        Assert.True(restarted.IsLocked);

        restarted.UnlockWithGuard(kek);
        Assert.False(restarted.IsLocked);

        restarted.Lock();
        restarted.Unlock(passphrase);
        Assert.False(restarted.IsLocked);
    }

    [Fact]
    public void FlagsFollowTheCopiesAndDisableKeepsThePassphraseCopy()
    {
        using var root = new TestDirectory();
        var config = new VaultConfiguration(root.Combine("appdata"), root.Combine("local"));
        PrivateFileSystem.CreatePrivateDirectory(config.ApplicationSupportDirectory);
        var key = TestCredentials.Key(74);
        var kek = TestCredentials.Key(126);
        using var vault = new Vault(TestCredentials.NewStore(config.VaultFile, key), key, null, null,
            root.Combine("deliveries"), config, new MemoryAppKeyStore { AppKey = key });

        Assert.False(vault.IsGuardUnlockEnabled);
        Assert.False(vault.IsPassphraseProtected);
        Assert.False(vault.IsHighSecurityEnabled);
        Assert.Null(vault.GuardHelloKeyName);

        vault.EnableHighSecurity("correct horse battery");
        Assert.True(vault.IsPassphraseProtected);
        Assert.True(vault.IsHighSecurityEnabled);
        Assert.False(vault.IsGuardUnlockEnabled);

        vault.EnableGuardUnlock(kek, HelloKeyName);
        Assert.True(vault.IsGuardUnlockEnabled);
        Assert.True(vault.IsPassphraseProtected);
        Assert.True(vault.IsHighSecurityEnabled);
        Assert.Equal(HelloKeyName, vault.GuardHelloKeyName);

        vault.DisableGuardUnlock();
        Assert.False(vault.IsGuardUnlockEnabled);
        Assert.Null(vault.GuardHelloKeyName);
        Assert.True(vault.IsPassphraseProtected);
        Assert.True(vault.IsHighSecurityEnabled);
        Assert.True(File.Exists(Path.Combine(config.KeyMaterialDirectory, PassphraseAppKeyStore.AppFileName)));
        Assert.False(File.Exists(Path.Combine(config.KeyMaterialDirectory, GuardAppKeyStore.AppFileName)));
    }

    [Fact]
    public void EnableRequiresAnUnlockedVault()
    {
        using var root = new TestDirectory();
        var config = new VaultConfiguration(root.Combine("appdata"), root.Combine("local"));
        PrivateFileSystem.CreatePrivateDirectory(config.ApplicationSupportDirectory);
        var key = TestCredentials.Key(75);
        using var vault = new Vault(TestCredentials.NewStore(config.VaultFile, key), null, null, null,
            root.Combine("deliveries"), config, new MemoryAppKeyStore { AppKey = key });

        var exception = Assert.Throws<VaultException>(() =>
            vault.EnableGuardUnlock(TestCredentials.Key(127), HelloKeyName));
        Assert.Equal(VaultErrorCode.VaultLocked, exception.Code);
        Assert.False(GuardAppKeyStore.IsEnabled(config.KeyMaterialDirectory));
    }

    [Fact]
    public void EnrollmentProofIsStableKeyBoundAndRequiresUnlock()
    {
        using var root = new TestDirectory();
        var key = TestCredentials.Key(78);
        var otherKey = TestCredentials.Key(79);
        using var vault = new Vault(TestCredentials.NewStore(root.Combine("library.db"), key), key,
            deliveryRoot: root.Combine("deliveries"));
        using var other = new Vault(TestCredentials.NewStore(root.Combine("other.db"), otherKey), otherKey,
            deliveryRoot: root.Combine("deliveries-other"));
        using var locked = new Vault(TestCredentials.NewStore(root.Combine("locked.db"), key), null,
            deliveryRoot: root.Combine("deliveries-locked"));

        var proof = vault.CreateGuardEnrollmentProof();

        Assert.Equal(32, proof.Length);
        Assert.Equal(proof, vault.CreateGuardEnrollmentProof());
        Assert.NotEqual(proof, other.CreateGuardEnrollmentProof());
        var exception = Assert.Throws<VaultException>(() => locked.CreateGuardEnrollmentProof());
        Assert.Equal(VaultErrorCode.VaultLocked, exception.Code);
    }

    [Fact]
    public void DisposeZeroesTheCachedKekAndRejectsFurtherLoads()
    {
        using var root = new TestDirectory();
        var directory = root.Combine("key-material");
        var key = TestCredentials.Key(76);
        var kek = TestCredentials.Key(128);
        GuardAppKeyStore.Write(directory, key, kek, HelloKeyName);
        var store = new GuardAppKeyStore(directory, kek);
        Assert.Equal(key, store.LoadAppKey());
        var cached = Assert.IsType<byte[]>(typeof(GuardAppKeyStore)
            .GetField("_kek", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store));

        store.Dispose();

        Assert.All(cached, value => Assert.Equal(0, value));
        Assert.Throws<ObjectDisposedException>(() => store.LoadAppKey());
        Assert.Equal(TestCredentials.Key(128), kek);
    }
}
