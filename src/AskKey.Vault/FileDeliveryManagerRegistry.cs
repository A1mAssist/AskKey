namespace AskKey.Vault;

/// <summary>
/// Lazily owns a delivery manager and retries failed initialization, matching
/// FileDeliveryManagerRegistry.swift.
/// </summary>
public sealed class FileDeliveryManagerRegistry : IDisposable
{
    private readonly object _lock = new();
    private readonly TimeSpan _retryDelay;
    private readonly Func<FileDeliveryManager> _factory;
    private FileDeliveryManager? _manager;
    private Exception? _initializationError;
    private bool _retryScheduled;
    private bool _disposed;

    public FileDeliveryManagerRegistry(Func<FileDeliveryManager> factory, TimeSpan? retryDelay = null)
    {
        _factory = factory;
        _retryDelay = retryDelay is { } delay && delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1);
        AttemptInitialization();
    }

    public FileDeliveryManagerRegistry(FileDeliveryManager manager)
    {
        _factory = () => manager;
        _retryDelay = TimeSpan.FromSeconds(1);
        _manager = manager;
    }

    public FileDeliveryManager Get()
    {
        lock (_lock)
        {
            if (_manager is not null) return _manager;
            throw _initializationError ?? VaultException.Database("Temporary credential cleanup is unavailable.");
        }
    }

    public bool HasFailures
    {
        get
        {
            lock (_lock)
            {
                if (_initializationError is not null) return true;
                return _manager is not null && _manager.CleanupFailures.Count > 0;
            }
        }
    }

    public void Revoke(string credentialId)
    {
        try { Get().Revoke(credentialId); }
        catch (Exception) { }
    }

    public void CleanupAll()
    {
        try { Get().CleanupAll(); }
        catch (Exception) { }
    }

    private void AttemptInitialization()
    {
        try
        {
            var created = _factory();
            lock (_lock)
            {
                _manager = created;
                _initializationError = null;
                _retryScheduled = false;
            }
        }
        catch (Exception exception)
        {
            bool schedule;
            lock (_lock)
            {
                _manager = null;
                _initializationError = exception;
                schedule = !_retryScheduled;
                _retryScheduled = true;
            }
            if (schedule) ScheduleRetry();
        }
    }

    private void ScheduleRetry()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(_retryDelay).ConfigureAwait(false);
            lock (_lock)
            {
                if (_disposed) return;
                _retryScheduled = false;
            }
            AttemptInitialization();
        });
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _manager?.Dispose();
            _manager = null;
        }
    }
}
