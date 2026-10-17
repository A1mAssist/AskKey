using System.Security.Cryptography;
using System.Text;
using AskKey.Broker.Approval;

namespace AskKey.Vault;

/// <summary>
/// Materializes temporary credential files with an owner-only ACL and a bounded
/// lifetime. Ported from FileDeliveryManager.swift. POSIX flock is replaced by
/// FileShare.None owner locks (released by the OS when a process dies) and a
/// named mutex serializes namespace initialization across processes.
/// </summary>
public sealed class FileDeliveryManager : IDisposable
{
    public static readonly TimeSpan MaximumTtl = TimeSpan.FromSeconds(300);

    private sealed record Entry(string CredentialId, string Path, bool RequiresOwnerLease);

    private readonly string _namespaceRoot;
    private readonly string _instanceRoot;
    private readonly FileStream _ownerLease;
    private readonly TimeSpan _ttl;
    private readonly TimeSpan _retryDelay;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<TimeSpan, Action> _schedule;
    private readonly Action<string> _removeItem;
    private readonly Action<FileStream> _synchronizeFile;
    private readonly object _lock = new();
    private readonly Dictionary<Guid, Entry> _entries = new();
    private readonly Dictionary<string, FileDeliveryCleanupFailure> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> _cleanupInProgress = new();
    private readonly List<Timer> _timers = new();
    private bool _disposed;

    public FileDeliveryManager(
        string rootPath,
        TimeSpan? ttl = null,
        TimeSpan? retryDelay = null,
        Func<DateTimeOffset>? now = null,
        Action<TimeSpan, Action>? schedule = null,
        Action<string>? removeItem = null,
        Action<FileStream>? synchronizeFile = null)
    {
        var lifetime = ttl ?? MaximumTtl;
        var retry = retryDelay ?? TimeSpan.FromSeconds(1);
        if (lifetime <= TimeSpan.Zero || lifetime > MaximumTtl || retry <= TimeSpan.Zero)
        {
            throw VaultException.Database("File delivery lifetime is invalid.");
        }
        _ttl = lifetime;
        _retryDelay = retry;
        _clock = now ?? (() => DateTimeOffset.UtcNow);
        _schedule = schedule ?? DefaultSchedule;
        _removeItem = removeItem ?? (path =>
        {
            if (File.Exists(path)) File.Delete(path);
            else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        });
        _synchronizeFile = synchronizeFile ?? (stream => stream.Flush(flushToDisk: true));
        _namespaceRoot = System.IO.Path.GetFullPath(rootPath);

        using (AcquireNamespaceLock(_namespaceRoot))
        {
            PrepareDirectory(_namespaceRoot);
            _instanceRoot = System.IO.Path.Combine(_namespaceRoot, "instance-" + Guid.NewGuid().ToString());
            PrepareDirectory(_instanceRoot);
            var ownerPath = System.IO.Path.Combine(_instanceRoot, ".owner-lock");
            _ownerLease = PrivateFileSystem.CreatePrivateFile(ownerPath, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None);
            SweepCrashResidue();
        }
    }

    public TimeSpan Ttl => _ttl;

    public IReadOnlyList<FileDeliveryCleanupFailure> CleanupFailures
    {
        get
        {
            lock (_lock) return _failures.Values.OrderBy(value => value.Path, StringComparer.Ordinal).ToList();
        }
    }

    public FileDelivery Materialize(string credentialId, byte[] bytes, DateTimeOffset? expiresAt = null,
        BrokerRuntimeReadAuthorization? authorization = null)
    {
        authorization?.Validate();
        var now = _clock();
        var deadline = new[] { now + _ttl, expiresAt, authorization?.ExpiresAt }
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .Min();
        if (deadline <= now) throw VaultException.Unavailable();

        var token = Guid.NewGuid();
        var path = System.IO.Path.Combine(_instanceRoot, Guid.NewGuid().ToString());
        try
        {
            using (var stream = PrivateFileSystem.CreatePrivateFile(path, FileMode.CreateNew, FileAccess.Write,
                       FileShare.Read))
            {
                stream.Write(bytes, 0, bytes.Length);
                _synchronizeFile(stream);
            }
        }
        catch
        {
            ManageFailedMaterialization(path, credentialId);
            throw;
        }

        lock (_lock) _entries[token] = new Entry(credentialId, path, RequiresOwnerLease: false);
        try
        {
            // Writing and flushing consumed part (or all) of the lifetime. Never
            // return an already expired file or restart its full TTL.
            var remaining = deadline - _clock();
            if (remaining <= TimeSpan.Zero) throw VaultException.Unavailable();
            ScheduleCleanup(token, remaining);
            // Invalidation may have happened before or during materialization.
            authorization?.RegisterCleanup(() => Cleanup(token));
            authorization?.Validate();
            if (_clock() >= deadline) throw VaultException.Unavailable();
        }
        catch
        {
            Cleanup(token);
            throw;
        }
        return new FileDelivery(path, deadline, token, this);
    }

    public void Revoke(string credentialId)
    {
        List<Guid> tokens;
        lock (_lock)
        {
            tokens = _entries.Where(pair => pair.Value.CredentialId == credentialId)
                .Select(pair => pair.Key).ToList();
        }
        foreach (var token in tokens) Cleanup(token);
    }

    public void CleanupAll()
    {
        List<Guid> tokens;
        lock (_lock) tokens = _entries.Keys.ToList();
        foreach (var token in tokens) Cleanup(token);
    }

    internal void Cleanup(Guid token)
    {
        Entry? entry;
        lock (_lock)
        {
            if (!_entries.TryGetValue(token, out entry)) return;
            if (!_cleanupInProgress.Add(token)) return;
        }
        try
        {
            FileStream? owner = null;
            try
            {
                if (entry.RequiresOwnerLease)
                {
                    var ownerPath = System.IO.Path.Combine(entry.Path, ".owner-lock");
                    try
                    {
                        owner = new FileStream(ownerPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    }
                    catch (IOException)
                    {
                        // The instance became live again; never remove it without its owner lease.
                        Forget(token, entry.Path);
                        return;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        RecordCleanupFailure(token, entry.Path, "Temporary credential owner could not be locked.");
                        return;
                    }
                }
                try
                {
                    _removeItem(entry.Path);
                    Forget(token, entry.Path);
                }
                catch (FileNotFoundException)
                {
                    Forget(token, entry.Path);
                }
                catch (DirectoryNotFoundException)
                {
                    Forget(token, entry.Path);
                }
                catch (Exception exception)
                {
                    RecordCleanupFailure(token, entry.Path, exception.Message);
                }
            }
            finally
            {
                owner?.Dispose();
            }
        }
        finally
        {
            lock (_lock) _cleanupInProgress.Remove(token);
        }
    }

    private void ManageFailedMaterialization(string path, string credentialId)
    {
        var token = Guid.NewGuid();
        lock (_lock) _entries[token] = new Entry(credentialId, path, RequiresOwnerLease: false);
        Cleanup(token);
    }

    private void RecordCleanupFailure(Guid token, string path, string message)
    {
        lock (_lock) _failures[path] = new FileDeliveryCleanupFailure(path, message);
        ScheduleCleanup(token, _retryDelay);
    }

    private void Forget(Guid token, string path)
    {
        lock (_lock)
        {
            _entries.Remove(token);
            _failures.Remove(path);
        }
    }

    private void ScheduleCleanup(Guid token, TimeSpan delay)
    {
        _schedule(delay, () => Cleanup(token));
    }

    private void DefaultSchedule(TimeSpan delay, Action action)
    {
        lock (_lock)
        {
            if (_disposed) return;
            Timer? timer = null;
            timer = new Timer(_ =>
            {
                lock (_lock) _timers.Remove(timer!);
                timer!.Dispose();
                action();
            }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            timer.Change(delay, Timeout.InfiniteTimeSpan);
            _timers.Add(timer);
        }
    }

    private void SweepCrashResidue()
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(_namespaceRoot))
        {
            if (string.Equals(path, _instanceRoot, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(System.IO.Path.GetFileName(path), ".namespace-lock", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var requiresOwnerLease = Directory.Exists(path)
                && System.IO.Path.GetFileName(path).StartsWith("instance-", StringComparison.Ordinal);
            if (requiresOwnerLease)
            {
                var ownerPath = System.IO.Path.Combine(path, ".owner-lock");
                FileStream? lease = null;
                try
                {
                    lease = new FileStream(ownerPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException)
                {
                    // A live manager still owns this instance.
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    TrackSweepFailure(path, requiresOwnerLease, "Temporary credential owner could not be locked.");
                    continue;
                }
                finally
                {
                    lease?.Dispose();
                }
            }
            try
            {
                _removeItem(path);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (Exception exception)
            {
                TrackSweepFailure(path, requiresOwnerLease, exception.Message);
            }
        }
    }

    private void TrackSweepFailure(string path, bool requiresOwnerLease, string message)
    {
        var token = Guid.NewGuid();
        lock (_lock) _entries[token] = new Entry("", path, requiresOwnerLease);
        RecordCleanupFailure(token, path, message);
    }

    private static void PrepareDirectory(string path)
    {
        PrivateFileSystem.CreatePrivateDirectory(path);
        if (!PrivateFileSystem.IsPrivateDirectory(path))
        {
            throw VaultException.Database("Temporary credential directory is not an owned ordinary directory.");
        }
    }

    /// <summary>
    /// Owns the namespace mutex. Dispose releases the mutex on the acquiring
    /// thread before closing the handle; disposing alone would leave the mutex
    /// permanently held for the process lifetime.
    /// </summary>
    private sealed class NamespaceLock : IDisposable
    {
        private Mutex? _mutex;

        public NamespaceLock(Mutex mutex) => _mutex = mutex;

        public void Dispose()
        {
            var mutex = Interlocked.Exchange(ref _mutex, null);
            if (mutex is null) return;
            try
            {
                mutex.ReleaseMutex();
            }
            finally
            {
                mutex.Dispose();
            }
        }
    }

    private static NamespaceLock AcquireNamespaceLock(string rootPath)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rootPath)))[..32];
        var mutex = new Mutex(false, "Local\\AskKey.FileDelivery." + digest);
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(10)))
            {
                throw VaultException.Database("Temporary credential namespace could not be locked.");
            }
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
        return new NamespaceLock(mutex);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        CleanupAll();
        lock (_lock)
        {
            foreach (var timer in _timers) timer.Dispose();
            _timers.Clear();
        }
        _ownerLease.Dispose();
        try
        {
            if (Directory.Exists(_instanceRoot)) Directory.Delete(_instanceRoot, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
