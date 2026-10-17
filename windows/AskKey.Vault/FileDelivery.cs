namespace AskKey.Vault;

public sealed record FileDeliveryCleanupFailure(string Path, string Message);

/// <summary>
/// One materialized temporary credential file. Finishing or disposing it removes
/// the file; the manager also removes it when the TTL or authorization expires.
/// </summary>
public sealed class FileDelivery : IDisposable
{
    private readonly object _lock = new();
    private readonly WeakReference<FileDeliveryManager> _manager;
    private Guid? _token;

    internal FileDelivery(string path, DateTimeOffset expiresAt, Guid token, FileDeliveryManager manager)
    {
        Path = path;
        ExpiresAt = expiresAt;
        _token = token;
        _manager = new WeakReference<FileDeliveryManager>(manager);
    }

    public string Path { get; }
    public DateTimeOffset ExpiresAt { get; }

    public void Finish()
    {
        Guid? token;
        lock (_lock)
        {
            token = _token;
            _token = null;
        }
        if (token is { } value && _manager.TryGetTarget(out var manager)) manager.Cleanup(value);
    }

    public void Dispose() => Finish();
}
