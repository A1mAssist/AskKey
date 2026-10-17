namespace AskKey.Broker.Approval;

/// <summary>The original approvals consumed by one runtime operation. Its identity and
/// deadline survive terminal-ticket eviction and cannot be renewed by a later
/// allowance. File cleanup belongs to this identity, never to a credential name.</summary>
public sealed class BrokerRuntimeReadAuthorization : IDisposable
{
    public DateTimeOffset ExpiresAt { get; }

    private readonly BrokerApprovalStateMachine _owner;
    private readonly Guid _id;

    internal BrokerRuntimeReadAuthorization(
        BrokerApprovalStateMachine owner,
        Guid id,
        DateTimeOffset expiresAt)
    {
        _owner = owner;
        _id = id;
        ExpiresAt = expiresAt;
    }

    public void Validate() => _owner.WithRuntimeAuthorization(_id);

    /// <summary>Call under the Vault spawn gate. Revocation takes the same approval lock
    /// and therefore cannot complete between this check and the actual spawn.</summary>
    public T PerformAuthorizedSpawn<T>(Func<T> spawn) => _owner.WithRuntimeAuthorization(_id, spawn);

    /// <summary>Registers a specific materialized resource. If invalidation already won,
    /// clean that resource immediately, outside the approval lock, and refuse it.</summary>
    public void RegisterCleanup(Action cleanup) => _owner.RegisterRuntimeCleanup(_id, cleanup);

    public void Finish() => _owner.ReleaseRuntimeAuthorization(_id);

    // Swift's deinit calls finish(); C# has no deterministic destructor, so callers
    // must Finish() or Dispose(). The authorization still expires by deadline.
    public void Dispose() => Finish();
}
