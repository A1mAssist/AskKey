namespace AskKey.Broker.Approval;

public sealed partial class BrokerApprovalStateMachine
{
    public void UpdateDefaultTimedAllowance(TimeSpan duration)
    {
        Mutate(() => _defaultTimedAllowance = ValidDuration(duration) ? duration : TimeSpan.FromMinutes(30));
    }

    /// <summary>App-only projection; the public Broker RPC surface does not expose it.</summary>
    public DateTimeOffset? TimedAllowanceDeadline(string credentialId)
        => Mutate<DateTimeOffset?>(() =>
        {
            ExpireTimedAllowancesLocked(_clock());
            return _timedAllowances.TryGetValue(credentialId, out var deadline) ? deadline : null;
        });

    public bool RevokeTimedAllowance(string credentialId)
    {
        Action<string>? handler = Mutate<Action<string>?>(() =>
        {
            if (!_timedAllowances.Remove(credentialId)) return null;
            InvalidateRuntimeAuthorizationsLocked(credentialId);
            foreach (var operationId in _entriesByOperationId.Keys.ToArray())
            {
                var entry = _entriesByOperationId[operationId];
                if (entry.Request.CredentialId != credentialId) continue;
                if (entry.State != BrokerRequestState.Approved) continue;
                if (entry.Committing) continue;
                if (!entry.ApprovedByTimedAllowance) continue;
                entry.State = BrokerRequestState.Cancelled;
            }
            return _timedAllowanceRevoked;
        });
        handler?.Invoke(credentialId);
        return handler is not null;
    }

    public void RevokeAllTimedAllowances()
    {
        var result = Mutate(() =>
        {
            var credentialIds = _timedAllowances.Keys.ToArray();
            _timedAllowances.Clear();
            InvalidateRuntimeAuthorizationsLocked();
            foreach (var operationId in _entriesByOperationId.Keys.ToArray())
            {
                var entry = _entriesByOperationId[operationId];
                if (entry.State != BrokerRequestState.Approved) continue;
                if (entry.Committing) continue;
                if (!entry.ApprovedByTimedAllowance) continue;
                entry.State = BrokerRequestState.Cancelled;
            }
            return (CredentialIds: credentialIds, Handler: _timedAllowanceRevoked);
        });
        foreach (var credentialId in result.CredentialIds) result.Handler(credentialId);
    }

    public void SetTimedAllowanceRevokedHandler(Action<string> handler)
    {
        Mutate(() => _timedAllowanceRevoked = handler);
    }
}
