using AskKey.Vault;

namespace AskKey.Vault.Tests;

public sealed class CurrentLibrarySnapshotTests
{
    [Fact]
    public void HotRollbackJournalFailsClosedWithoutTouchingSourceFiles()
    {
        using var root = new TestDirectory();
        var paths = new VaultBootstrapPaths(root.Combine("vault"));
        var keys = new MemoryAppKeyStore();
        var (store, _) = VaultBootstrap.OpenCurrent(paths, keys);
        store.Dispose();

        var databaseBefore = File.ReadAllBytes(paths.CurrentDatabase);
        var journal = paths.CurrentDatabase + "-journal";
        var journalBefore = new byte[512];
        journalBefore[0] = 0xD9;
        journalBefore[1] = 0xD5;
        journalBefore[2] = 0x05;
        journalBefore[3] = 0xF9;
        File.WriteAllBytes(journal, journalBefore);

        var exception = Assert.Throws<VaultBootstrapException>(() => VaultBootstrap.CredentialCount(paths));

        Assert.Equal(VaultBootstrapError.InvalidState, exception.Error);
        Assert.Equal(databaseBefore, File.ReadAllBytes(paths.CurrentDatabase));
        Assert.Equal(journalBefore, File.ReadAllBytes(journal));
    }

    [Fact]
    public void PreflightUsesPrivateCopyAndLeavesCurrentLibraryUnchanged()
    {
        using var root = new TestDirectory();
        var paths = new VaultBootstrapPaths(root.Combine("vault"));
        var keys = new MemoryAppKeyStore();
        var (store, key) = VaultBootstrap.OpenCurrent(paths, keys);
        using (store)
        {
            store.BindCredentialAuthenticationKey(key);
            store.InsertCredential(TestCredentials.Text(key, "SYNTHETIC-SNAPSHOT", "value"));
        }

        var before = Directory.GetFiles(paths.Directory)
            .ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);

        Assert.Equal(1, VaultBootstrap.CredentialCount(paths));

        var after = Directory.GetFiles(paths.Directory)
            .ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);
        Assert.Equal(before.Keys.OrderBy(name => name), after.Keys.OrderBy(name => name));
        foreach (var (name, bytes) in before)
        {
            Assert.Equal(bytes, after[name]);
        }
    }

    [Fact]
    public void SourceChangesDuringValidationFailClosed()
    {
        using var root = new TestDirectory();
        var path = root.Combine("library.db");
        using (var store = new VaultStore(path))
        {
            store.Checkpoint();
        }

        var original = File.ReadAllBytes(path);
        var exception = Assert.Throws<VaultBootstrapException>(() =>
            CurrentLibrarySnapshot.WithCopy(path, copy =>
            {
                File.AppendAllText(path, "SYNTHETIC-SOURCE-CHANGE");
                return copy;
            }));

        Assert.Equal(VaultBootstrapError.InvalidState, exception.Error);
        Assert.Equal(original.Length + "SYNTHETIC-SOURCE-CHANGE".Length, File.ReadAllBytes(path).Length);
    }
}
