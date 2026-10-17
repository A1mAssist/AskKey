namespace AskKey.Broker.Approval;

public sealed partial class BrokerApprovalStateMachine
{
    /// <summary>Test seam: the state machine owns at most one reschedulable expiration task.</summary>
    internal int ScheduledExpirationTaskCount
    {
        get
        {
            lock (_lock)
            {
                return _hasScheduledExpiration ? 1 : 0;
            }
        }
    }

    private void ExpireUnconsumedLocked(DateTimeOffset now)
    {
        foreach (var operationId in _entriesByOperationId.Keys.ToArray())
        {
            var entry = _entriesByOperationId[operationId];
            if (entry.State != BrokerRequestState.Pending && entry.State != BrokerRequestState.Approved) continue;
            if (entry.Committing) continue;
            if (now < entry.ExpiresAt) continue;
            entry.State = BrokerRequestState.Expired;
        }
    }

    private void ExpireTimedAllowancesLocked(DateTimeOffset now)
    {
        foreach (var credentialId in _timedAllowances.Keys.ToArray())
        {
            if (_timedAllowances[credentialId] <= now) _timedAllowances.Remove(credentialId);
        }
    }

    private void ExpireScheduledRequests()
    {
        var now = _clock();
        Mutate(() =>
        {
            ExpireUnconsumedLocked(now);
            ExpireTimedAllowancesLocked(now);
        });
    }

    /// <summary>Reconcile wall-clock deadlines after wake or screen unlock without
    /// exposing pending request details or extending any authorization.</summary>
    public void RefreshExpiration() => ExpireScheduledRequests();

    private void RescheduleExpirationLocked(DateTimeOffset now)
    {
        DateTimeOffset? nextExpiration = null;
        foreach (var entry in _entriesByOperationId.Values)
        {
            if (entry.Committing) continue;
            if (entry.State != BrokerRequestState.Pending && entry.State != BrokerRequestState.Approved) continue;
            if (nextExpiration is null || entry.ExpiresAt < nextExpiration) nextExpiration = entry.ExpiresAt;
        }
        foreach (var authorization in _runtimeAuthorizations.Values)
        {
            if (!authorization.Valid) continue;
            if (nextExpiration is null || authorization.ExpiresAt < nextExpiration) nextExpiration = authorization.ExpiresAt;
        }
        foreach (var allowance in _timedAllowances.Values)
        {
            if (nextExpiration is null || allowance < nextExpiration) nextExpiration = allowance;
        }
        if (nextExpiration is null)
        {
            _hasScheduledExpiration = false;
            _expirationTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }
        _hasScheduledExpiration = true;
        var delay = nextExpiration.Value - now;
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        // Timer caps its due time; a clamped arm simply fires early and re-arms.
        if (delay > MaximumTimerDelay) delay = MaximumTimerDelay;
        _expirationTimer.Change(delay, Timeout.InfiniteTimeSpan);
    }
}
