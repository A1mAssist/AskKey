using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AskKey.Vault;

public enum VaultBootstrapState
{
    Fresh,
    Current,
}

public sealed class VaultBootstrapPaths
{
    public string Directory { get; }
    public string DurabilityRoot { get; }
    public string CurrentDatabase => Path.Combine(Directory, "credentials-v2.db");
    public string CreatingDatabase => CurrentDatabase + ".creating";

    public VaultBootstrapPaths(string directory, string? durabilityRoot = null)
    {
        Directory = directory;
        DurabilityRoot = durabilityRoot ?? Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar))
            ?? directory;
    }
}

public enum VaultBootstrapSyncTargetKind
{
    File,
    Directory,
}

public sealed record VaultBootstrapSyncTarget(VaultBootstrapSyncTargetKind Kind, string Path);

public static class VaultBootstrap
{
    public static VaultBootstrapState State(VaultBootstrapPaths paths)
    {
        if (CurrentLibrarySnapshot.RegularFileExists(paths.CurrentDatabase)) return VaultBootstrapState.Current;
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            // A sidecar without its database is orphaned state: any entry,
            // including a regular file, fails closed instead of being adopted.
            if (CurrentLibrarySnapshot.RegularFileExists(paths.CurrentDatabase + suffix))
            {
                throw new VaultBootstrapException(VaultBootstrapError.InvalidState);
            }
        }
        return VaultBootstrapState.Fresh;
    }

    public static (VaultStore Store, byte[] Key) OpenCurrent(
        VaultBootstrapPaths paths,
        IAppKeyStore keyStore,
        Action<string>? beforeCreationRename = null,
        Action<VaultBootstrapSyncTarget>? synchronize = null)
    {
        synchronize ??= Synchronize;
        // Reject an invalid boundary before any file or key-store mutation.
        _ = DurableEntryChain(paths);
        var state = State(paths);

        if (TryLoad(keyStore.LoadAppKey, AppKeyStoreError.MissingAppKey) is { } appKey)
        {
            if (state != VaultBootstrapState.Current)
            {
                throw new VaultBootstrapException(VaultBootstrapError.MissingDatabase);
            }
            ValidateKey(appKey);
            var authenticated = ValidateCurrentLibrary(paths, appKey);
            var deletePending = authenticated > 0 || !HasDifferentPendingKey(keyStore, appKey);
            var store = new VaultStore(paths.CurrentDatabase, appKey);
            try
            {
                store.BindCredentialAuthenticationKey(appKey);
                TightenPermissions(paths);
            }
            catch
            {
                store.Dispose();
                throw;
            }
            if (deletePending)
            {
                try { keyStore.DeletePendingKey(); } catch (Exception) { }
            }
            return (store, appKey);
        }

        var pending = TryLoad(keyStore.LoadPendingKey, AppKeyStoreError.MissingPendingKey);
        switch (state)
        {
            case VaultBootstrapState.Fresh:
                return CreateNewLibrary(paths, keyStore, pending is null ? null : ValidateKey(pending),
                    beforeCreationRename, synchronize);
            case VaultBootstrapState.Current:
                if (pending is null) throw new VaultBootstrapException(VaultBootstrapError.MissingKey);
                return ResumeUnfinishedFirstCreation(paths, keyStore, ValidateKey(pending), synchronize);
            default:
                throw new VaultBootstrapException(VaultBootstrapError.InvalidState);
        }
    }

    private static byte[]? TryLoad(Func<byte[]> load, AppKeyStoreError missing)
    {
        try
        {
            return load();
        }
        catch (AppKeyStoreException exception) when (exception.Error == missing)
        {
            return null;
        }
    }

    private static bool HasDifferentPendingKey(IAppKeyStore keyStore, byte[] appKey)
    {
        try
        {
            var pending = TryLoad(keyStore.LoadPendingKey, AppKeyStoreError.MissingPendingKey);
            if (pending is null) return false;
            return !pending.AsSpan().SequenceEqual(appKey);
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static byte[] ValidateKey(byte[] data)
    {
        if (data.Length != VaultCrypto.KeyByteCount) throw new VaultBootstrapException(VaultBootstrapError.InvalidKey);
        return data;
    }

    private static (VaultStore Store, byte[] Key) CreateNewLibrary(VaultBootstrapPaths paths, IAppKeyStore keyStore,
        byte[]? pendingKey, Action<string>? beforeRename, Action<VaultBootstrapSyncTarget> synchronize)
    {
        PrivateFileSystem.CreatePrivateDirectory(paths.Directory);
        RemoveCreationFiles(paths);
        byte[] key;
        if (pendingKey is not null)
        {
            key = pendingKey;
        }
        else
        {
            key = VaultCrypto.GenerateKey();
            keyStore.SavePendingKey(key);
        }

        try
        {
            using (var creating = new VaultStore(paths.CreatingDatabase))
            {
                creating.Checkpoint();
                beforeRename?.Invoke(paths.CreatingDatabase);
            }
            PrivateFileSystem.ApplyFile(paths.CreatingDatabase);
            synchronize(new VaultBootstrapSyncTarget(VaultBootstrapSyncTargetKind.File, paths.CreatingDatabase));
            try
            {
                // Windows has no renamex_np(RENAME_EXCL); Move(overwrite:false) fails closed.
                File.Move(paths.CreatingDatabase, paths.CurrentDatabase, overwrite: false);
            }
            catch (IOException exception)
            {
                throw new VaultBootstrapException(VaultBootstrapError.InvalidState, exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new VaultBootstrapException(VaultBootstrapError.InvalidState, exception);
            }
        }
        catch
        {
            RemoveCreationFiles(paths);
            throw;
        }

        var store = new VaultStore(paths.CurrentDatabase);
        try
        {
            store.BindCredentialAuthenticationKey(key);
            TightenPermissions(paths);
            MakeDurableAndPromote(paths, keyStore, synchronize);
            return (store, key);
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    private static void MakeDurableAndPromote(VaultBootstrapPaths paths, IAppKeyStore keyStore,
        Action<VaultBootstrapSyncTarget> synchronize)
    {
        foreach (var target in DurableEntryChain(paths)) synchronize(target);
        keyStore.PromotePendingKey();
        keyStore.DeletePendingKey();
    }

    private static List<VaultBootstrapSyncTarget> DurableEntryChain(VaultBootstrapPaths paths)
    {
        var directory = Path.GetFullPath(paths.Directory);
        var root = Path.GetFullPath(paths.DurabilityRoot);
        var comparison = StringComparison.OrdinalIgnoreCase;
        if (!directory.Equals(root, comparison)
            && !directory.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison))
        {
            throw new VaultBootstrapException(VaultBootstrapError.InvalidState);
        }
        var chain = new List<VaultBootstrapSyncTarget>
        {
            new(VaultBootstrapSyncTargetKind.File, paths.CurrentDatabase),
            new(VaultBootstrapSyncTargetKind.Directory, directory),
        };
        while (!directory.Equals(root, comparison))
        {
            var parent = Path.GetDirectoryName(directory);
            if (parent is null || parent.Equals(directory, comparison))
            {
                throw new VaultBootstrapException(VaultBootstrapError.InvalidState);
            }
            chain.Add(new VaultBootstrapSyncTarget(VaultBootstrapSyncTargetKind.Directory, parent));
            directory = parent;
        }
        return chain;
    }

    private static void RemoveCreationFiles(VaultBootstrapPaths paths)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            var path = paths.CreatingDatabase + suffix;
            if (File.Exists(path)) File.Delete(path);
        }
    }

    public static void Synchronize(VaultBootstrapSyncTarget target)
    {
        // F_FULLFSYNC equivalent: FlushFileBuffers on the file or directory handle.
        // FlushFileBuffers requires a handle with write access; a read-only handle
        // fails with ERROR_ACCESS_DENIED. A directory is opened with FILE_FLAG_BACKUP_SEMANTICS.
        const uint genericRead = 0x80000000;
        const uint genericWrite = 0x40000000;
        const uint openExisting = 3;
        const uint backupSemantics = 0x02000000;
        var directory = target.Kind == VaultBootstrapSyncTargetKind.Directory;
        // FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE
        const uint shareAll = 0x00000001 | 0x00000002 | 0x00000004;
        using var handle = CreateFileW(target.Path, genericRead | genericWrite,
            shareAll, IntPtr.Zero, openExisting,
            directory ? backupSemantics : 0u, IntPtr.Zero);
        if (handle.IsInvalid) throw new VaultBootstrapException(VaultBootstrapError.InvalidState);
        if (!FlushFileBuffers(handle)) throw new VaultBootstrapException(VaultBootstrapError.InvalidState);
    }

    private static void TightenPermissions(VaultBootstrapPaths paths)
    {
        PrivateFileSystem.ApplyDirectory(paths.Directory);
        PrivateFileSystem.ApplyFile(paths.CurrentDatabase);
    }

    private static (VaultStore Store, byte[] Key) ResumeUnfinishedFirstCreation(VaultBootstrapPaths paths,
        IAppKeyStore keyStore, byte[] key, Action<VaultBootstrapSyncTarget> synchronize)
    {
        CurrentLibrarySnapshot.WithCopy(paths.CurrentDatabase, copy =>
        {
            using var snapshot = VaultStore.OpenReadOnly(copy);
            RequireUnfinishedFirstCreation(snapshot);
            return 0;
        });
        var store = new VaultStore(paths.CurrentDatabase, key, RequireUnfinishedFirstCreation);
        try
        {
            store.BindCredentialAuthenticationKey(key);
            TightenPermissions(paths);
            MakeDurableAndPromote(paths, keyStore, synchronize);
            return (store, key);
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    public static void RequireUnfinishedFirstCreation(VaultStore store)
    {
        var opening = store.ReadOpening();
        string target;
        switch (opening)
        {
            case CurrentLibrarySchema.Opening.Baseline:
                target = CurrentLibrarySchema.BaselineIdentifier;
                break;
            case CurrentLibrarySchema.Opening.Current:
                target = CurrentLibrarySchema.DropLegacyTablesIdentifier;
                break;
            default:
                throw new VaultBootstrapException(VaultBootstrapError.MissingKey);
        }

        using var expected = NewExpectedLibrary(target);
        var schema = store.NormalizedSchema();
        if (!schema.SequenceEqual(expected.NormalizedSchema(), StringComparer.Ordinal)
            || !FirstCreationContents(store).SequenceEqual(expected.FirstCreationContents(), StringComparer.Ordinal))
        {
            throw new VaultBootstrapException(VaultBootstrapError.MissingKey);
        }
    }

    /// <summary>
    /// Builds the expected library at the requested migration stage with the
    /// real migrator, then removes its temporary directory on dispose. A
    /// baseline-stage library must not run the drop migration, so it is built
    /// by <see cref="VaultStore.CreateBaseline"/>.
    /// </summary>
    private static ExpectedLibrary NewExpectedLibrary(string upToIdentifier)
    {
        var directory = Path.Combine(Path.GetTempPath(), "AskKeyExpected-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "expected.db");
        try
        {
            var store = upToIdentifier == CurrentLibrarySchema.BaselineIdentifier
                ? VaultStore.CreateBaseline(path)
                : new VaultStore(path);
            return new ExpectedLibrary(directory, store);
        }
        catch
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
            throw;
        }
    }

    private sealed class ExpectedLibrary : IDisposable
    {
        private readonly string _directory;
        private readonly VaultStore _store;

        public ExpectedLibrary(string directory, VaultStore store)
        {
            _directory = directory;
            _store = store;
        }

        public List<string> NormalizedSchema() => _store.NormalizedSchema();

        public List<string> FirstCreationContents() => VaultBootstrap.FirstCreationContents(_store);

        public void Dispose()
        {
            _store.Dispose();
            try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        }
    }

    private static List<string> FirstCreationContents(VaultStore store)
    {
        var tables = store.QueryRows(
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT GLOB 'sqlite_*' AND name != 'grdb_migrations' ORDER BY name;")
            .Select(row => (string)row["name"]!).ToList();
        var counts = new List<string>();
        foreach (var table in tables)
        {
            counts.Add($"{table}={store.ScalarLong($"SELECT COUNT(*) FROM \"{table}\";")}");
        }

        string? projectId = null;
        if (tables.Contains("projects") || tables.Contains("environments"))
        {
            var projects = store.QueryRows("SELECT * FROM projects;");
            var environments = store.QueryRows("SELECT * FROM environments;");
            if (projects.Count != 1 || environments.Count != 1) return counts;
            projectId = (string?)projects[0]["id"];
            counts.Add("project=" + AbstractColumns(projects[0], new[] { "id", "created_at", "updated_at" }, projectId));
            counts.Add("environment=" + AbstractColumns(environments[0], new[] { "id", "created_at" }, projectId));
        }
        else
        {
            var active = store.QueryRows("SELECT value FROM config WHERE key = 'active_project_id';");
            if (active.Count == 1 && active[0]["value"] is string text && Guid.TryParse(text, out _)) projectId = text;
        }

        var config = store.QueryRows("SELECT key, value FROM config ORDER BY key;")
            .Select(row =>
            {
                var value = (string?)row["value"];
                return $"{row["key"]}={AbstractValue(value, projectId)}";
            });
        counts.AddRange(config);
        return counts;
    }

    private static string AbstractColumns(Dictionary<string, object?> row, string[] volatileColumns, string? seed)
    {
        return string.Join("|", row
            .Where(pair => !volatileColumns.Contains(pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={AbstractValue(pair.Value as string, seed)}"));
    }

    private static string AbstractValue(string? value, string? seed)
        => value is not null && seed is not null && value == seed ? "\0seed-project" : value ?? "\0null";

    public static int CredentialCount(VaultBootstrapPaths paths)
    {
        if (State(paths) != VaultBootstrapState.Current) return 0;
        return CurrentLibrarySnapshot.WithCopy(paths.CurrentDatabase, copy =>
        {
            using var store = VaultStore.OpenReadOnly(copy);
            store.ReadOpening();
            return checked((int)store.ScalarLong("SELECT COUNT(*) FROM credentials;"));
        });
    }

    private static int ValidateCurrentLibrary(VaultBootstrapPaths paths, byte[] key)
    {
        return CurrentLibrarySnapshot.WithCopy(paths.CurrentDatabase, copy =>
        {
            using var store = new VaultStore(copy);
            store.ReadOpening();
            var records = store.FetchAllCredentialsIncludingRecycledRaw();
            foreach (var record in records) CredentialRecordAuthentication.Verify(record, key);
            return records.Count;
        });
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle fileHandle);
}
