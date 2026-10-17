using System.Text;
using AskKey.Vault;

namespace AskKey.Vault.Tests;

public sealed class VaultBootstrapTests
{
    [Fact]
    public void FreshBootstrapCreatesCurrentLibraryAndPromotesKey()
    {
        using var root = new TestDirectory();
        var paths = new VaultBootstrapPaths(root.Combine("vault"));
        var keys = new MemoryAppKeyStore();
        var (store, key) = VaultBootstrap.OpenCurrent(paths, keys);
        using (store)
        {
            Assert.Equal(CurrentLibrarySchema.CurrentIdentifiers,
                store.QueryStrings("SELECT identifier FROM grdb_migrations ORDER BY rowid;"));
            Assert.False(store.TableExists("projects"));
        }
        Assert.Equal(key, keys.AppKey);
        Assert.Null(keys.PendingKey);
        Assert.Equal(VaultBootstrapState.Current, VaultBootstrap.State(paths));
        Assert.True(File.Exists(paths.CurrentDatabase));
        Assert.False(File.Exists(paths.CreatingDatabase));
        Assert.Equal(new[] { "credentials-v2.db" },
            Directory.GetFileSystemEntries(paths.Directory).Select(Path.GetFileName));
        Assert.True(PrivateFileSystem.IsPrivateDirectory(paths.Directory));
        Assert.True(PrivateFileSystem.IsPrivateFile(paths.CurrentDatabase));
        Assert.Equal(0, VaultBootstrap.CredentialCount(paths));
    }

    [Fact]
    public void BootstrapResumesWithAnExistingPendingKeyWithoutWritingANewOne()
    {
        using var root = new TestDirectory();
        var paths = new VaultBootstrapPaths(root.Combine("vault"));
        var pending = TestCredentials.Key(20);
        var keys = new MemoryAppKeyStore { PendingKey = pending };
        var (store, key) = VaultBootstrap.OpenCurrent(paths, keys);
        store.Dispose();
        Assert.Equal(pending, key);
        Assert.Equal(pending, keys.AppKey);
        Assert.Null(keys.PendingKey);
        Assert.Equal(2, keys.Mutations);
    }

    [Fact]
    public void ReopeningACurrentLibraryIsIdempotentAndUsesTheAppKey()
    {
        using var root = new TestDirectory();
        var paths = new VaultBootstrapPaths(root.Combine("vault"));
        var keys = new MemoryAppKeyStore();
        var (first, key) = VaultBootstrap.OpenCurrent(paths, keys);
        first.Dispose();
        var before = Directory.GetFiles(paths.Directory)
            .ToDictionary(path => Path.GetFileName(path)!, path => File.ReadAllBytes(path));
        var (second, reopenedKey) = VaultBootstrap.OpenCurrent(paths, keys);
        second.Dispose();
        Assert.Equal(key, reopenedKey);
        Assert.Equal(before.Keys.OrderBy(x => x), Directory.GetFiles(paths.Directory)
            .Select(Path.GetFileName).OrderBy(x => x));
        foreach (var (name, bytes) in before)
        {
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(paths.Directory, name!)));
        }
    }

    [Fact]
    public void AppKeyWithoutDatabaseFailsClosedAndLeavesFilesAndKeysUntouched()
    {
        using var root = new TestDirectory();
        var paths = new VaultBootstrapPaths(root.Combine("vault"));
        Directory.CreateDirectory(paths.Directory);
        File.WriteAllText(Path.Combine(paths.Directory, "opaque"), "SYNTHETIC");
        var appKey = TestCredentials.Key(21);
        var keys = new MemoryAppKeyStore { AppKey = appKey };
        var exception = Assert.Throws<VaultBootstrapException>(() => VaultBootstrap.OpenCurrent(paths, keys));
        Assert.Equal(VaultBootstrapError.MissingDatabase, exception.Error);
        Assert.Equal(appKey, keys.AppKey);
        Assert.Null(keys.PendingKey);
        Assert.Equal(0, keys.Mutations);
        Assert.False(File.Exists(paths.CurrentDatabase));
        Assert.Equal("SYNTHETIC", File.ReadAllText(Path.Combine(paths.Directory, "opaque")));
    }

    [Fact]
    public void DatabaseWithoutAnyKeyFailsClosedAndLeavesTheFileUntouched()
    {
        using var root = new TestDirectory();
        var paths = new VaultBootstrapPaths(root.Combine("vault"));
        var keys = new MemoryAppKeyStore();
        var (store, _) = VaultBootstrap.OpenCurrent(paths, keys);
        store.Dispose();
        var before = File.ReadAllBytes(paths.CurrentDatabase);
        var emptyKeys = new MemoryAppKeyStore();
        var exception = Assert.Throws<VaultBootstrapException>(() => VaultBootstrap.OpenCurrent(paths, emptyKeys));
        Assert.Equal(VaultBootstrapError.MissingKey, exception.Error);
        Assert.Equal(before, File.ReadAllBytes(paths.CurrentDatabase));
        Assert.Equal(0, emptyKeys.Mutations);
    }

    [Fact]
    public void MalformedPendingKeyIsRejectedBeforeAnyFileOrKeyMutation()
    {
        using var root = new TestDirectory();
        var paths = new VaultBootstrapPaths(root.Combine("vault"));
        Directory.CreateDirectory(paths.Directory);
        File.WriteAllText(Path.Combine(paths.Directory, "sibling"), "SYNTHETIC");
        var keys = new MemoryAppKeyStore { PendingKey = new byte[31] };
        var exception = Assert.Throws<VaultBootstrapException>(() => VaultBootstrap.OpenCurrent(paths, keys));
        Assert.Equal(VaultBootstrapError.InvalidKey, exception.Error);
        Assert.Equal(31, keys.PendingKey!.Length);
        Assert.Null(keys.AppKey);
        Assert.Equal(0, keys.Mutations);
        Assert.False(File.Exists(paths.CurrentDatabase));
        Assert.False(File.Exists(paths.CreatingDatabase));
        Assert.Equal(new[] { "sibling" }, Directory.GetFiles(paths.Directory).Select(Path.GetFileName));
    }

    [Fact]
    public void OrphanedDatabaseSidecarsFailClosed()
    {
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            using var root = new TestDirectory();
            var paths = new VaultBootstrapPaths(root.Combine("vault"));
            Directory.CreateDirectory(paths.Directory);
            File.WriteAllText(paths.CurrentDatabase + suffix, "SYNTHETIC-orphan");
            var keys = new MemoryAppKeyStore { PendingKey = TestCredentials.Key(22) };
            try
            {
                var opened = VaultBootstrap.OpenCurrent(paths, keys);
                try
                {
                    Assert.Fail(suffix + " did not fail");
                }
                finally
                {
                    opened.Store.Dispose();
                }
            }
            catch (VaultBootstrapException exception)
            {
                Assert.True(exception.Error == VaultBootstrapError.InvalidState, suffix + ": " + exception.Message);
                Assert.Null(keys.AppKey);
                Assert.Equal(0, keys.Mutations);
                continue;
            }
        }
    }

    [Fact]
    public void FailureBeforeCreationRenameLeavesNoCurrentDatabaseAndReusesPendingKey()
    {
        using var root = new TestDirectory();
        var paths = new VaultBootstrapPaths(root.Combine("vault"));
        var keys = new MemoryAppKeyStore();
        var observed = false;
        Assert.Throws<InvalidOperationException>(() => VaultBootstrap.OpenCurrent(paths, keys,
            beforeCreationRename: creating =>
            {
                observed = true;
                Assert.Equal(paths.CreatingDatabase, creating);
                Assert.True(File.Exists(creating));
                Assert.False(File.Exists(paths.CurrentDatabase));
                throw new InvalidOperationException("injected");
            }));
        Assert.True(observed);
        Assert.False(File.Exists(paths.CurrentDatabase));
        Assert.False(File.Exists(paths.CreatingDatabase));
        Assert.Null(keys.AppKey);
        var pending = keys.PendingKey!;
        Assert.Equal(32, pending.Length);
        Assert.Equal(VaultBootstrapState.Fresh, VaultBootstrap.State(paths));

        var (store, key) = VaultBootstrap.OpenCurrent(paths, keys);
        store.Dispose();
        Assert.Equal(pending, key);
        Assert.Equal(pending, keys.AppKey);
        Assert.Null(keys.PendingKey);
    }

    [Fact]
    public void CurrentDatabaseAppearingBeforeRenameIsNeverReplaced()
    {
        using var root = new TestDirectory();
        var paths = new VaultBootstrapPaths(root.Combine("vault"));
        var keys = new MemoryAppKeyStore();
        var marker = Encoding.UTF8.GetBytes("SYNTHETIC-current-marker");
        var exception = Assert.Throws<VaultBootstrapException>(() => VaultBootstrap.OpenCurrent(paths, keys,
            beforeCreationRename: _ => File.WriteAllBytes(paths.CurrentDatabase, marker)));
        Assert.Equal(VaultBootstrapError.InvalidState, exception.Error);
        Assert.Equal(marker, File.ReadAllBytes(paths.CurrentDatabase));
        Assert.False(File.Exists(paths.CreatingDatabase));
        Assert.Equal(32, keys.PendingKey!.Length);
        Assert.Null(keys.AppKey);
    }

    [Fact]
    public void SyncFailureBeforeRenameKeepsPendingKeyAndNextLaunchRecovers()
    {
        using var root = new TestDirectory();
        var paths = new VaultBootstrapPaths(root.Combine("vault"));
        var keys = new MemoryAppKeyStore();
        Assert.Throws<InvalidOperationException>(() => VaultBootstrap.OpenCurrent(paths, keys,
            synchronize: target =>
            {
                if (target.Path == paths.CreatingDatabase) throw new InvalidOperationException("injected");
                VaultBootstrap.Synchronize(target);
            }));
        Assert.Null(keys.AppKey);
        var pending = keys.PendingKey!;
        Assert.Equal(VaultBootstrapState.Fresh, VaultBootstrap.State(paths));

        var (store, key) = VaultBootstrap.OpenCurrent(paths, keys);
        store.Dispose();
        Assert.Equal(pending, key);
        Assert.Equal(pending, keys.AppKey);
    }

    [Fact]
    public void SyncFailureAfterRenameKeepsPendingKeyAndNextLaunchResumesTheCreation()
    {
        using var root = new TestDirectory();
        var paths = new VaultBootstrapPaths(root.Combine("vault"));
        var keys = new MemoryAppKeyStore();
        Assert.Throws<InvalidOperationException>(() => VaultBootstrap.OpenCurrent(paths, keys,
            synchronize: target =>
            {
                if (target.Path == paths.Directory) throw new InvalidOperationException("injected");
                VaultBootstrap.Synchronize(target);
            }));
        Assert.Null(keys.AppKey);
        var pending = keys.PendingKey!;
        Assert.Equal(VaultBootstrapState.Current, VaultBootstrap.State(paths));

        var (store, key) = VaultBootstrap.OpenCurrent(paths, keys);
        store.Dispose();
        Assert.Equal(pending, key);
        Assert.Equal(CurrentLibrarySchema.CurrentIdentifiers,
            VaultStore.OpenReadOnly(paths.CurrentDatabase).QueryStrings(
                "SELECT identifier FROM grdb_migrations ORDER BY rowid;"));
    }

    [Fact]
    public void PromotionFailureLeavesAnUnfinishedLibraryThatRecoversWithThePendingKey()
    {
        using var root = new TestDirectory();
        var paths = new VaultBootstrapPaths(root.Combine("vault"));
        var keys = new MemoryAppKeyStore { PromotionFailure = new InvalidOperationException("injected") };
        Assert.Throws<InvalidOperationException>(() => VaultBootstrap.OpenCurrent(paths, keys));
        Assert.Null(keys.AppKey);
        var pending = keys.PendingKey!;
        keys.PromotionFailure = null;

        var (store, key) = VaultBootstrap.OpenCurrent(paths, keys);
        store.Dispose();
        Assert.Equal(pending, key);
        Assert.Equal(pending, keys.AppKey);
        Assert.Null(keys.PendingKey);
    }

    [Fact]
    public void StateRejectsADirectoryOrSidecarDirectoryAtTheDatabasePath()
    {
        using var root = new TestDirectory();
        var paths = new VaultBootstrapPaths(root.Combine("vault"));
        Directory.CreateDirectory(paths.CurrentDatabase);
        var exception = Assert.Throws<VaultBootstrapException>(() => VaultBootstrap.State(paths));
        Assert.Equal(VaultBootstrapError.InvalidState, exception.Error);
    }

    [Fact]
    public void RequireUnfinishedFirstCreationAcceptsRealBaselineAndCurrentLibraries()
    {
        using var root = new TestDirectory();
        var baselinePath = root.Combine("baseline.db");
        using (var baseline = VaultStore.CreateBaseline(baselinePath))
        {
            VaultBootstrap.RequireUnfinishedFirstCreation(baseline);
        }
        var currentPath = root.Combine("current.db");
        using var current = new VaultStore(currentPath);
        VaultBootstrap.RequireUnfinishedFirstCreation(current);
    }

    [Fact]
    public void RequireUnfinishedFirstCreationRejectsAddedUserRows()
    {
        using var root = new TestDirectory();
        using var store = new VaultStore(root.Combine("library.db"));
        store.SetConfigValue("synthetic", "value");
        var exception = Assert.Throws<VaultBootstrapException>(
            () => VaultBootstrap.RequireUnfinishedFirstCreation(store));
        Assert.Equal(VaultBootstrapError.MissingKey, exception.Error);
    }

    [Fact]
    public void CredentialCountCountsActiveAndRecycledCredentials()
    {
        using var root = new TestDirectory();
        var paths = new VaultBootstrapPaths(root.Combine("vault"));
        var keys = new MemoryAppKeyStore();
        var (store, key) = VaultBootstrap.OpenCurrent(paths, keys);
        using (store)
        {
            store.BindCredentialAuthenticationKey(key);
            var first = TestCredentials.Text(key, "ONE", "1");
            var second = TestCredentials.Text(key, "TWO", "2");
            store.InsertCredential(first);
            store.InsertCredential(second);
            store.RecycleCredential(second.Id, "2026-01-01T00:00:00Z");
        }
        Assert.Equal(2, VaultBootstrap.CredentialCount(paths));
    }

    [Fact]
    public void NormalizeSqlIgnoresCaseAndWhitespaceButNotQuotedValues()
    {
        Assert.Equal(
            VaultStore.NormalizeSql("CREATE  TABLE \"x\" (v TEXT DEFAULT 'a b')"),
            VaultStore.NormalizeSql("create table \"x\"(v text default 'a b')"));
        Assert.NotEqual(
            VaultStore.NormalizeSql("DEFAULT 'a b'"),
            VaultStore.NormalizeSql("DEFAULT 'ab'"));
        Assert.NotEqual(
            VaultStore.NormalizeSql("DEFAULT 'A'"),
            VaultStore.NormalizeSql("DEFAULT 'a'"));
        Assert.NotEqual(
            VaultStore.NormalizeSql("v TEXT NOT NULL"),
            VaultStore.NormalizeSql("v TEXTNOT NULL"));
    }
}




