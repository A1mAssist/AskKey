using AskKey.Broker.Approval;
using AskKey.Broker.Protocol;

namespace AskKey.Broker.Transport;

/// <summary>Capability-bound request state owned by the App, mirroring Swift's registry.</summary>
public sealed class BrokerRequestRegistry
{
    private sealed class PendingState
    {
        public required string Capability { get; init; }
        public string? CredentialId { get; init; }
    }

    private sealed class RetainedState
    {
        public required string Capability { get; init; }
        public BrokerRequestState State { get; set; }
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, PendingState> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RetainedState> _retained = new(StringComparer.Ordinal);
    private readonly List<string> _retainedOrder = new();
    private bool _paused;

    public void Register(string requestId, string capability, string? credentialId = null)
    {
        lock (_lock)
        {
            if (_paused) throw new BrokerRequestRegistryException(BrokerRequestRegistryError.AgentAccessPaused);
            if (_pending.ContainsKey(requestId) || _retained.ContainsKey(requestId))
            {
                throw new BrokerRequestRegistryException(BrokerRequestRegistryError.DuplicateRequest);
            }
            if (_pending.Count >= BrokerLimits.MaximumPendingApprovalRequests)
            {
                throw new BrokerRequestRegistryException(BrokerRequestRegistryError.CapacityReached);
            }
            _pending[requestId] = new PendingState { Capability = capability, CredentialId = credentialId };
        }
    }

    public BrokerRequestState? Status(string requestId, string capability)
    {
        lock (_lock)
        {
            if (_pending.TryGetValue(requestId, out var pending) && pending.Capability == capability)
            {
                return BrokerRequestState.Pending;
            }
            return _retained.TryGetValue(requestId, out var retained) && retained.Capability == capability
                ? retained.State
                : null;
        }
    }

    public BrokerRequestState? Cancel(string requestId, string capability)
    {
        lock (_lock)
        {
            if (_pending.TryGetValue(requestId, out var pending) && pending.Capability == capability)
            {
                _pending.Remove(requestId);
                Retain(requestId, capability, BrokerRequestState.Cancelled);
                return BrokerRequestState.Cancelled;
            }
            return _retained.TryGetValue(requestId, out var retained) && retained.Capability == capability
                ? retained.State
                : null;
        }
    }

    public int CancelAllPending()
    {
        lock (_lock) return CancelAllPendingLocked();
    }

    public int CancelPending(string credentialId)
    {
        lock (_lock)
        {
            var matching = _pending.Where(entry => entry.Value.CredentialId == credentialId).ToArray();
            foreach (var (requestId, pending) in matching)
            {
                _pending.Remove(requestId);
                Retain(requestId, pending.Capability, BrokerRequestState.Cancelled);
            }
            return matching.Length;
        }
    }

    public void PauseAndCancelAll()
    {
        lock (_lock)
        {
            _paused = true;
            _ = CancelAllPendingLocked();
        }
    }

    public void Resume()
    {
        lock (_lock) _paused = false;
    }

    public bool SetState(string requestId, string capability, BrokerRequestState state)
    {
        lock (_lock)
        {
            if (state == BrokerRequestState.Pending)
            {
                return _pending.TryGetValue(requestId, out var pending) && pending.Capability == capability;
            }
            if (_pending.TryGetValue(requestId, out var active) && active.Capability == capability)
            {
                _pending.Remove(requestId);
                Retain(requestId, capability, state);
                return true;
            }
            return _retained.TryGetValue(requestId, out var retained)
                && retained.Capability == capability && retained.State == state;
        }
    }

    private int CancelAllPendingLocked()
    {
        var pending = _pending.ToArray();
        _pending.Clear();
        foreach (var (requestId, state) in pending)
        {
            Retain(requestId, state.Capability, BrokerRequestState.Cancelled);
        }
        return pending.Length;
    }

    private void Retain(string requestId, string capability, BrokerRequestState state)
    {
        _retained[requestId] = new RetainedState { Capability = capability, State = state };
        _retainedOrder.Add(requestId);
        if (_retainedOrder.Count > BrokerLimits.MaximumRetainedRequestStates)
        {
            _retained.Remove(_retainedOrder[0]);
            _retainedOrder.RemoveAt(0);
        }
    }
}

public enum BrokerRequestRegistryError
{
    CapacityReached,
    DuplicateRequest,
    AgentAccessPaused,
}

public sealed class BrokerRequestRegistryException : Exception
{
    public BrokerRequestRegistryException(BrokerRequestRegistryError error) : base(error.ToString()) => Error = error;

    public BrokerRequestRegistryError Error { get; }
}

/// <summary>Releases one in-flight request slot exactly once.</summary>
internal sealed class BrokerRequestLease
{
    private readonly object _lock = new();
    private bool _released;
    private readonly Action _release;

    public BrokerRequestLease(Action release) => _release = release;

    public void Release()
    {
        lock (_lock)
        {
            if (_released) return;
            _released = true;
        }
        _release();
    }
}

