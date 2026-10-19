using System.Text;
using AskKey.Vault;

namespace AskKey.Vault.Tests;

public sealed class VaultStoreTests
{
    private static readonly string[] BaselineTables =
    {
        "config", "projects", "environments", "secrets", "secret_values", "activity_log",
        "credentials", "agent_write_operations", "credential_access_records",
    };

    [Fact]
    public void NewStoreCreatesCurrentSchemaInWalMode()
    {
        using var root = new TestDirectory();
        using var store = new VaultStore(root.Combine("library.db"));
        Assert.Equal(CurrentLibrarySchema.CurrentIdentifiers,
            store.QueryStrings("SELECT identifier FROM grdb_migrations ORDER BY rowid;"));
        foreach (var table in BaselineTables.Where(name => !CurrentLibrarySchema.LegacyTables.Contains(name)))
        {
            Assert.True(store.TableExists(table), table);
        }
        foreach (var table in CurrentLibrarySchema.LegacyTables) Assert.False(store.TableExists(table), table);
        Assert.Equal("wal", store.QueryStrings("PRAGMA journal_mode;").Single());
        Assert.Single(store.QueryRows("SELECT * FROM config;"));
        Assert.Equal(0, store.ScalarLong("SELECT COUNT(*) FROM credentials;"));
    }

    [Fact]
    public void BaselineOnlyLibraryMigratesToCurrentIdentifiers()
    {
        using var root = new TestDirectory();
        var path = root.Combine("library.db");
        using (var baseline = VaultStore.CreateBaseline(path))
        {
            Assert.Equal(new[] { CurrentLibrarySchema.BaselineIdentifier },
                baseline.QueryStrings("SELECT identifier FROM grdb_migrations ORDER BY rowid;"));
        }
        using var store = new VaultStore(path);
        Assert.Equal(CurrentLibrarySchema.CurrentIdentifiers,
            store.QueryStrings("SELECT identifier FROM grdb_migrations ORDER BY rowid;"));
    }

    [Fact]
    public void ReadOnlyStoreReadsButRejectsWrites()
    {
        using var root = new TestDirectory();
        var path = root.Combine("library.db");
        using (var writable = new VaultStore(path))
        {
            writable.SetConfigValue("synthetic", "value");
        }
        using var readOnly = VaultStore.OpenReadOnly(path);
        Assert.Equal("value", readOnly.ConfigValue("synthetic"));
        Assert.ThrowsAny<Exception>(() => readOnly.SetConfigValue("other", "blocked"));
        Assert.ThrowsAny<Exception>(() => readOnly.InsertCredential(
            TestCredentials.Text(TestCredentials.Key(), "TOKEN", "value")));
    }

    [Fact]
    public void MissingDatabaseWithAuthenticationKeyFailsClosed()
    {
        using var root = new TestDirectory();
        var exception = Assert.Throws<VaultBootstrapException>(() =>
            new VaultStore(root.Combine("missing.db"), TestCredentials.Key()));
        Assert.Equal(VaultBootstrapError.MissingDatabase, exception.Error);
        Assert.False(File.Exists(root.Combine("missing.db")));
    }

    [Fact]
    public void CredentialCrudRoundTripsAndPersists()
    {
        using var root = new TestDirectory();
        var path = root.Combine("library.db");
        var key = TestCredentials.Key();
        var record = TestCredentials.Text(key, "SYNTHETIC-TOKEN", "secret-value",
            usage: "use it", notes: "private", group: "Group A", environment: "SYNTHETIC_TOKEN",
            permission: "allowed", expiresAt: "2030-01-01T00:00:00Z");
        using (var store = TestCredentials.NewStore(path, key))
        {
            store.InsertCredential(record);
            var fetched = store.FetchCredential(record.Id)!;
            Assert.Equal("secret-value", VaultCrypto.Decrypt(fetched.EncryptedPayload, key));
            Assert.Equal("use it", VaultCrypto.Decrypt(fetched.EncryptedUsageInstructions, key));
            Assert.Equal("private", VaultCrypto.Decrypt(fetched.EncryptedPrivateNotes, key));
            Assert.Equal("Group A", VaultCrypto.Decrypt(fetched.EncryptedGroupName!, key));
            Assert.Equal("SYNTHETIC_TOKEN", VaultCrypto.Decrypt(fetched.EncryptedEnvironmentVariable!, key));
            Assert.Equal("allowed", fetched.Permission);
            Assert.Equal("2030-01-01T00:00:00Z", fetched.ExpiresAt);
            Assert.Single(store.FetchAllCredentials());

            fetched.EncryptedPayload = VaultCrypto.Encrypt("updated-value", key);
            fetched.UpdatedAt = CredentialRecord.Timestamp(DateTimeOffset.UtcNow.AddMinutes(1));
            store.UpdateCredential(fetched);
            Assert.Equal("updated-value",
                VaultCrypto.Decrypt(store.FetchCredential(record.Id)!.EncryptedPayload, key));
        }
        using var reopened = TestCredentials.NewStore(path, key);
        Assert.Equal("updated-value",
            VaultCrypto.Decrypt(reopened.FetchCredential(record.Id)!.EncryptedPayload, key));
    }

    [Fact]
    public void NameIndexLookupDistinguishesActiveFromRecycled()
    {
        using var root = new TestDirectory();
        var key = TestCredentials.Key();
        using var store = TestCredentials.NewStore(root.Combine("library.db"), key);
        var record = TestCredentials.Text(key, "SYNTHETIC-TOKEN", "value");
        store.InsertCredential(record);
        Assert.NotNull(store.FetchCredentialByNameIndex(record.NameIndex));
        store.RecycleCredential(record.Id, "2026-01-01T00:00:00Z");
        Assert.Null(store.FetchCredentialByNameIndex(record.NameIndex));
        Assert.NotNull(store.FetchCredentialIncludingRecycledByNameIndex(record.NameIndex));
        Assert.Empty(store.FetchAllCredentials());
        Assert.Single(store.FetchRecycledCredentials());
    }

    [Fact]
    public async Task FetchAllIncludingRecycledWaitsForAnAtomicTransaction()
    {
        using var root = new TestDirectory();
        var key = TestCredentials.Key();
        using var store = TestCredentials.NewStore(root.Combine("library.db"), key);
        var record = TestCredentials.Text(key, "TRANSACTIONAL", "value");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var writer = Task.Run(() =>
        {
            Assert.Throws<InvalidOperationException>(() => store.RunTransaction<int>(() =>
            {
                store.InsertCredentialInTransaction(record);
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(2)));
                throw new InvalidOperationException("rollback");
            }));
        });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));

        var reader = Task.Run(store.FetchAllCredentialsIncludingRecycled);
        Assert.NotSame(reader, await Task.WhenAny(reader, Task.Delay(100)));
        release.Set();
        await writer.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(await reader.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void DuplicateNameIndexIsRejectedByUniqueConstraint()
    {
        using var root = new TestDirectory();
        var key = TestCredentials.Key();
        using var store = TestCredentials.NewStore(root.Combine("library.db"), key);
        store.InsertCredential(TestCredentials.Text(key, "SYNTHETIC-TOKEN", "one"));
        Assert.ThrowsAny<Exception>(() => store.InsertCredential(
            TestCredentials.Text(key, "  synthetic-token  ", "two")));
    }

    [Fact]
    public void RecycleRestoreAndPermanentDeleteFollowTheTrashContract()
    {
        using var root = new TestDirectory();
        var key = TestCredentials.Key();
        using var store = TestCredentials.NewStore(root.Combine("library.db"), key);
        var record = TestCredentials.Text(key, "SYNTHETIC-TOKEN", "value");
        store.InsertCredential(record);

        store.RecycleCredential(record.Id, "2026-01-01T00:00:00Z");
        Assert.Equal("2026-01-01T00:00:00Z", store.FetchRecycledCredentials().Single().DeletedAt);
        Assert.Throws<VaultException>(() => store.RecycleCredential(record.Id, "2026-01-02T00:00:00Z"));
        Assert.Throws<VaultException>(() => store.RestoreCredential(record.Id + "-missing"));

        store.RestoreCredential(record.Id);
        Assert.Null(store.FetchCredential(record.Id)!.DeletedAt);
        Assert.Empty(store.FetchRecycledCredentials());
        Assert.Throws<VaultException>(() => store.RestoreCredential(record.Id));

        store.RecycleCredential(record.Id, "2026-01-01T00:00:00Z");
        store.DeleteRecycledCredential(record.Id);
        Assert.Null(store.FetchCredentialIncludingRecycledByNameIndex(record.NameIndex));
        Assert.Throws<VaultException>(() => store.DeleteRecycledCredential(record.Id));
    }

    [Fact]
    public void PurgeDeletesOnlyRecycledCredentialsAtOrBeforeTheCutoff()
    {
        using var root = new TestDirectory();
        var key = TestCredentials.Key();
        using var store = TestCredentials.NewStore(root.Combine("library.db"), key);
        var old = TestCredentials.Text(key, "OLD", "1");
        var recent = TestCredentials.Text(key, "RECENT", "2");
        var active = TestCredentials.Text(key, "ACTIVE", "3");
        store.InsertCredential(old);
        store.InsertCredential(recent);
        store.InsertCredential(active);
        store.RecycleCredential(old.Id, "2025-12-01T00:00:00Z");
        store.RecycleCredential(recent.Id, "2026-02-01T00:00:00Z");

        Assert.Equal(1, store.PurgeRecycledCredentials("2026-01-01T00:00:00Z"));
        Assert.Null(store.FetchCredentialIncludingRecycledByNameIndex(old.NameIndex));
        Assert.NotNull(store.FetchCredentialIncludingRecycledByNameIndex(recent.NameIndex));
        Assert.NotNull(store.FetchCredential(active.Id));
    }

    [Fact]
    public void GroupEditIsEncryptedAndDeletingAGroupOnlyUngroups()
    {
        using var root = new TestDirectory();
        var key = TestCredentials.Key();
        using var store = TestCredentials.NewStore(root.Combine("library.db"), key);
        var first = TestCredentials.Text(key, "ONE", "1", group: "Work");
        var second = TestCredentials.Text(key, "TWO", "2", group: "Personal");
        store.InsertCredential(first);
        store.InsertCredential(second);

        store.EditCredentialGroup("Work", key, deleting: false, "2026-01-01T00:00:00Z");
        store.EditCredentialGroup("Personal", key, deleting: false, "2026-01-01T00:00:00Z");
        Assert.Equal(new[] { "Personal", "Work" },
            VaultGroups.StoredGroups(store.ConfigValue(VaultGroups.ConfigKey), key).OrderBy(x => x));
        var rawConfig = store.ConfigValue(VaultGroups.ConfigKey)!;
        Assert.DoesNotContain("Work", Encoding.UTF8.GetString(Convert.FromBase64String(rawConfig)));

        var cleared = store.EditCredentialGroup("Work", key, deleting: true, "2026-01-02T00:00:00Z");
        Assert.Equal(new[] { first.Id }, cleared);
        Assert.Null(store.FetchCredential(first.Id)!.EncryptedGroupName);
        Assert.NotNull(store.FetchCredential(second.Id));
        Assert.Equal(new[] { "Personal" }, VaultGroups.StoredGroups(store.ConfigValue(VaultGroups.ConfigKey), key));
    }

    [Fact]
    public void AuthenticationFailureBlocksReadsAndWrites()
    {
        using var root = new TestDirectory();
        var key = TestCredentials.Key();
        var path = root.Combine("library.db");
        CredentialRecord record;
        using (var store = TestCredentials.NewStore(path, key))
        {
            record = TestCredentials.Text(key, "TOKEN", "value");
            store.InsertCredential(record);
            var tampered = record.Clone();
            tampered.Permission = "allowed";
            store.Execute("UPDATE credentials SET permission = $permission WHERE id = $id;",
                ("$permission", "allowed"), ("$id", record.Id));
        }
        using var reopened = TestCredentials.NewStore(path, key);
        Assert.Throws<CredentialRecordAuthenticationException>(() => reopened.FetchCredential(record.Id));
        Assert.Throws<CredentialRecordAuthenticationException>(() => reopened.FetchAllCredentials());
        Assert.Throws<CredentialRecordAuthenticationException>(() => reopened.UpdateCredential(record));
        Assert.Throws<CredentialRecordAuthenticationException>(() => reopened.RecycleCredential(record.Id,
            "2026-01-01T00:00:00Z"));
    }

    [Fact]
    public void LockedStoreRejectsCredentialOperations()
    {
        using var root = new TestDirectory();
        using var store = new VaultStore(root.Combine("library.db"));
        var exception = Assert.Throws<VaultException>(() => store.InsertCredential(
            TestCredentials.Text(TestCredentials.Key(), "TOKEN", "value")));
        Assert.Equal(VaultErrorCode.VaultLocked, exception.Code);
    }

    [Fact]
    public void CredentialBytesAtRestContainNoPlaintextAfterCheckpoint()
    {
        using var root = new TestDirectory();
        var path = root.Combine("library.db");
        var key = TestCredentials.Key();
        using (var store = TestCredentials.NewStore(path, key))
        {
            store.InsertCredential(TestCredentials.Text(key, "SYNTHETIC-UNIQUE-NAME", "SYNTHETIC-UNIQUE-VALUE",
                usage: "SYNTHETIC-UNIQUE-USAGE", notes: "SYNTHETIC-UNIQUE-NOTES", group: "SYNTHETIC-UNIQUE-GROUP"));
            store.EditCredentialGroup("SYNTHETIC-UNIQUE-GROUP", key, deleting: false, "2026-01-01T00:00:00Z");
            store.Checkpoint();
        }
        var bytes = File.ReadAllBytes(path);
        foreach (var plaintext in new[]
                 {
                     "SYNTHETIC-UNIQUE-NAME", "SYNTHETIC-UNIQUE-VALUE", "SYNTHETIC-UNIQUE-USAGE",
                     "SYNTHETIC-UNIQUE-NOTES", "SYNTHETIC-UNIQUE-GROUP",
                 })
        {
            Assert.False(TestCredentials.ContainsBytes(bytes, Encoding.UTF8.GetBytes(plaintext)), plaintext);
        }
    }
}

