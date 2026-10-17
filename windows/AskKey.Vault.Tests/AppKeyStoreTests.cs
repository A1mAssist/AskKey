using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using AskKey.Vault;

namespace AskKey.Vault.Tests;

public sealed class AppKeyStoreTests
{
    [Fact]
    public void DpapiStoreRoundTripsPendingAndAppKeysWithOwnerOnlyFiles()
    {
        using var root = new TestDirectory();
        var directory = root.Combine("key-material");
        var store = new DpapiAppKeyStore(directory);
        var pending = TestCredentials.Key(5);
        store.SavePendingKey(pending);
        Assert.Equal(pending, store.LoadPendingKey());
        Assert.Equal(AppKeyStoreError.MissingAppKey,
            Assert.Throws<AppKeyStoreException>(() => store.LoadAppKey()).Error);
        Assert.True(PrivateFileSystem.IsPrivateDirectory(directory));
        var pendingPath = Path.Combine(directory, DpapiAppKeyStore.PendingFileName);
        Assert.True(PrivateFileSystem.IsPrivateFile(pendingPath));
        Assert.NotEqual(pending, File.ReadAllBytes(pendingPath));

        store.PromotePendingKey();
        Assert.Equal(pending, store.LoadAppKey());
        Assert.True(PrivateFileSystem.IsPrivateFile(Path.Combine(directory, DpapiAppKeyStore.AppFileName)));
        store.DeletePendingKey();
        Assert.Throws<AppKeyStoreException>(() => store.LoadPendingKey());

        var reloaded = new DpapiAppKeyStore(directory);
        Assert.Equal(pending, reloaded.LoadAppKey());
        reloaded.DeleteAppKey();
        Assert.Throws<AppKeyStoreException>(() => reloaded.LoadAppKey());
    }

    [Fact]
    public void DpapiStoreFailsClosedOnAConflictingKey()
    {
        using var root = new TestDirectory();
        var store = new DpapiAppKeyStore(root.Combine("key-material"));
        var key = TestCredentials.Key(6);
        store.SavePendingKey(key);
        var original = File.ReadAllBytes(Path.Combine(root.Combine("key-material"), DpapiAppKeyStore.PendingFileName));

        Assert.Equal(AppKeyStoreError.ConflictingKey,
            Assert.Throws<AppKeyStoreException>(() => store.SavePendingKey(TestCredentials.Key(7))).Error);
        Assert.Equal(key, store.LoadPendingKey());
        Assert.Equal(original,
            File.ReadAllBytes(Path.Combine(root.Combine("key-material"), DpapiAppKeyStore.PendingFileName)));
        store.SavePendingKey(key);
    }

    [Fact]
    public void DpapiStoreRejectsNonThirtyTwoByteKeys()
    {
        using var root = new TestDirectory();
        var store = new DpapiAppKeyStore(root.Combine("key-material"));
        Assert.Equal(AppKeyStoreError.ConflictingKey,
            Assert.Throws<AppKeyStoreException>(() => store.SavePendingKey(new byte[31])).Error);
        Assert.False(File.Exists(Path.Combine(root.Combine("key-material"), DpapiAppKeyStore.PendingFileName)));
    }

    [Fact]
    public void DpapiStoreRefusesFilesWithForeignAcl()
    {
        using var root = new TestDirectory();
        var directory = root.Combine("key-material");
        var store = new DpapiAppKeyStore(directory);
        store.SavePendingKey(TestCredentials.Key(8));
        var path = Path.Combine(directory, DpapiAppKeyStore.PendingFileName);
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);

        Assert.Equal(AppKeyStoreError.SecurityFailure,
            Assert.Throws<AppKeyStoreException>(() => store.LoadPendingKey()).Error);
    }

    [Fact]
    public void DpapiProtectedBytesCannotBeUnprotectedWithDifferentEntropy()
    {
        using var root = new TestDirectory();
        var store = new DpapiAppKeyStore(root.Combine("key-material"));
        store.SavePendingKey(TestCredentials.Key(9));
        var protectedBytes = File.ReadAllBytes(Path.Combine(root.Combine("key-material"),
            DpapiAppKeyStore.PendingFileName));
        Assert.Throws<CryptographicException>(() =>
            ProtectedData.Unprotect(protectedBytes, new byte[] { 1, 2, 3 }, DataProtectionScope.CurrentUser));
    }

    [Fact]
    public void IsolatedStoreRoundTripsAndPromotesPlainKeyFile()
    {
        using var root = new TestDirectory();
        var store = new IsolatedAppKeyStore(root.Path);
        var key = TestCredentials.Key(10);
        store.SavePendingKey(key);
        Assert.Equal(key, store.LoadPendingKey());
        store.PromotePendingKey();
        Assert.Equal(key, store.LoadAppKey());
        store.DeletePendingKey();
        store.DeleteAppKey();
        Assert.Throws<AppKeyStoreException>(() => store.LoadAppKey());
    }

    [Fact]
    public void IsolatedStoreRejectsAKeyDirectoryWithForeignAcl()
    {
        using var root = new TestDirectory();
        var directory = root.Combine("key-material");
        Directory.CreateDirectory(directory);
        var exception = Assert.Throws<AppKeyStoreException>(() => new IsolatedAppKeyStore(root.Path));
        Assert.Equal(AppKeyStoreError.SecurityFailure, exception.Error);
    }

    [Fact]
    public void MemoryStoreModelsPromotionAndConflict()
    {
        var store = new MemoryAppKeyStore();
        var key = TestCredentials.Key(11);
        store.SavePendingKey(key);
        Assert.Throws<AppKeyStoreException>(() => store.SavePendingKey(TestCredentials.Key(12)));
        store.PromotePendingKey();
        Assert.Equal(key, store.LoadAppKey());
        store.DeletePendingKey();
        Assert.Throws<AppKeyStoreException>(() => store.LoadPendingKey());
    }
}
