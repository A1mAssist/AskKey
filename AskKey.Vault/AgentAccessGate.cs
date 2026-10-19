using AskKey.Broker.Approval;

namespace AskKey.Vault;

/// <summary>
/// Serializes Agent operations against exclusive credential mutations. Ported
/// from AgentAccessGate.swift: an exclusive change marks the gate paused first
/// and then waits for active operations to drain, so no operation can start in
/// the check-then-mutate window.
/// </summary>
public sealed class AgentAccessGate
{
    private enum State
    {
        Active,
        Paused,
        Invalid,
    }

    private readonly object _gate = new();
    private State _state = State.Active;
    private int _activeOperations;
    private bool _changing;

    public void BeginAgentOperation()
    {
        Monitor.Enter(_gate);
        try
        {
            switch (_state)
            {
                case State.Active:
                    _activeOperations++;
                    return;
                case State.Paused:
                    throw VaultException.AgentPaused();
                default:
                    throw InvalidState();
            }
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    public void EndAgentOperation()
    {
        Monitor.Enter(_gate);
        try
        {
            if (_activeOperations > 0) _activeOperations--;
            Monitor.PulseAll(_gate);
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    /// <summary>
    /// Keeps the gate locked across the caller's final expiry check and spawn.
    /// The caller must pair this with <see cref="EndSpawnAuthorization"/>.
    /// </summary>
    public void BeginSpawnAuthorization()
    {
        Monitor.Enter(_gate);
        if (_state == State.Active) return;
        Monitor.Exit(_gate);
        throw new BrokerProviderException(BrokerProviderError.RequestRejected);
    }

    public void EndSpawnAuthorization()
    {
        if (Monitor.IsEntered(_gate)) Monitor.Exit(_gate);
    }

    /// <summary>Agent writes share the exclusion boundary but cannot run while paused.</summary>
    public bool BeginExclusiveAgentChange()
    {
        Monitor.Enter(_gate);
        try
        {
            while (_changing) Monitor.Wait(_gate);
            if (_state != State.Active)
            {
                throw _state == State.Paused ? VaultException.AgentPaused() : InvalidState();
            }
            _changing = true;
            _state = State.Paused;
            while (_activeOperations > 0) Monitor.Wait(_gate);
            return false;
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    public bool BeginExclusiveChange()
    {
        Monitor.Enter(_gate);
        try
        {
            while (_changing) Monitor.Wait(_gate);
            if (_state == State.Invalid) throw InvalidState();
            _changing = true;
            var wasPaused = _state == State.Paused;
            _state = State.Paused;
            while (_activeOperations > 0) Monitor.Wait(_gate);
            return wasPaused;
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    public void EndExclusiveChange(bool paused)
    {
        Monitor.Enter(_gate);
        try
        {
            _state = paused ? State.Paused : State.Active;
            _changing = false;
            Monitor.PulseAll(_gate);
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    public void Synchronize(bool paused)
    {
        Monitor.Enter(_gate);
        try
        {
            _state = paused ? State.Paused : State.Active;
            Monitor.PulseAll(_gate);
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    public void Invalidate()
    {
        Monitor.Enter(_gate);
        try
        {
            _state = State.Invalid;
            Monitor.PulseAll(_gate);
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    public bool IsPaused()
    {
        Monitor.Enter(_gate);
        try
        {
            return _state switch
            {
                State.Active => false,
                State.Paused => true,
                _ => throw InvalidState(),
            };
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    private static VaultException InvalidState()
        => VaultException.Database("Agent access pause state is invalid.");
}
