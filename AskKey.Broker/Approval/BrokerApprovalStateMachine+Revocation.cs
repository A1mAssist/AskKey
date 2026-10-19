namespace AskKey.Broker.Approval;

public sealed partial class BrokerApprovalStateMachine
{
    public int CancelPending(string credentialId) => CancelPending(credentialId, forExpiry: false);

    /// <summary>Rename/delete-only members do not impose a credential expiry deadline.</summary>
    public int CancelPendingForExpiry(string credentialId) => CancelPending(credentialId, forExpiry: true);

    private int CancelPending(string credentialId, bool forExpiry)
        => Mutate(() =>
        {
            var now = _clock();
            if (forExpiry) ExpireUnconsumedLocked(now);
            _timedAllowances.Remove(credentialId);
            InvalidateRuntimeAuthorizationsLocked(credentialId);
            var count = 0;
            foreach (var operationId in _entriesByOperationId.Keys.ToArray())
            {
                var entry = _entriesByOperationId[operationId];
                var matchesCredential = entry.Request.CredentialId == credentialId
                    || (entry.Request.OrganizationCredentialIds?.Contains(credentialId, StringComparer.Ordinal) ?? false);
                if (!matchesCredential) continue;
                if (entry.Committing) continue;
                if (entry.State != BrokerRequestState.Pending && entry.State != BrokerRequestState.Approved) continue;
                if (forExpiry
                    && entry.Request.Operation == BrokerApprovalOperation.Organize
                    && entry.ExpiresAt > now)
                {
                    continue;
                }
                entry.State = BrokerRequestState.Cancelled;
                count++;
            }
            return count;
        });

    public void PauseAndCancelAll()
        => Mutate(() =>
        {
            _paused = true;
            _timedAllowances.Clear();
            InvalidateRuntimeAuthorizationsLocked();
            foreach (var operationId in _entriesByOperationId.Keys.ToArray())
            {
                var entry = _entriesByOperationId[operationId];
                if (entry.Committing) continue;
                if (entry.State != BrokerRequestState.Pending && entry.State != BrokerRequestState.Approved) continue;
                entry.State = BrokerRequestState.Cancelled;
            }
        });

    public void Resume()
        => Mutate(() => { _paused = false; });
}
