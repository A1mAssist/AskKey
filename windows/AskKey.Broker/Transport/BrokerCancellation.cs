namespace AskKey.Broker.Transport;

/// <summary>Thread-safe cancellation flag with one-shot callbacks, mirroring Swift.</summary>
public sealed class BrokerCancellation
{
    private readonly object _lock = new();
    private readonly ManualResetEventSlim _cancelled = new(false);
    private bool _isCancelled;
    private List<Action> _callbacks = new();

    public bool IsCancelled
    {
        get
        {
            lock (_lock) return _isCancelled;
        }
    }

    public void Cancel()
    {
        List<Action> pending;
        lock (_lock)
        {
            if (_isCancelled) return;
            _isCancelled = true;
            pending = _callbacks;
            _callbacks = new List<Action>();
            _cancelled.Set();
        }
        foreach (var callback in pending)
        {
            try { callback(); } catch (Exception) { /* observer faults must not block cancellation */ }
        }
    }

    public void OnCancel(Action callback)
    {
        lock (_lock)
        {
            if (!_isCancelled)
            {
                _callbacks.Add(callback);
                return;
            }
        }
        callback();
    }

    public void Check()
    {
        if (IsCancelled) throw new BrokerCancellationException();
    }

    public bool Wait(TimeSpan timeout) => _cancelled.Wait(timeout);

    public void WaitUntilCancelled() => _cancelled.Wait();
}

public sealed class BrokerCancellationException : Exception
{
    public BrokerCancellationException() : base("cancelled") { }
}
