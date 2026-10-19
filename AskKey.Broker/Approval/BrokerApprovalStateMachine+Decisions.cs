namespace AskKey.Broker.Approval;

public sealed partial class BrokerApprovalStateMachine
{
    public BrokerApprovalTicket Decide(
        string requestId,
        string capability,
        BrokerApprovalDecision decision)
        => Decide(requestId, capability, decision, _clock());

    /// <summary>Test seam. Production callers cannot supply time.</summary>
    internal BrokerApprovalTicket Decide(
        string requestId,
        string capability,
        BrokerApprovalDecision decision,
        DateTimeOffset now)
    {
        var context = WithEntry(requestId, capability, now, entry => (Entry: entry, _readAuthenticationEnabled));
        var entry = context.Entry;
        if (entry.State != BrokerRequestState.Pending)
        {
            throw new BrokerApprovalException(BrokerApprovalError.InvalidDecision);
        }
        if (decision.Kind == BrokerApprovalDecisionKind.TimedAllow)
        {
            if (entry.Request.Operation != BrokerApprovalOperation.Read)
            {
                throw new BrokerApprovalException(BrokerApprovalError.InvalidDecision);
            }
            if (decision.Duration is { } duration && !ValidDuration(duration))
            {
                throw new BrokerApprovalException(BrokerApprovalError.InvalidDecision);
            }
        }
        if (decision.Kind != BrokerApprovalDecisionKind.Deny
            && (entry.Request.Operation != BrokerApprovalOperation.Read || context._readAuthenticationEnabled))
        {
            var purpose = entry.Request.Operation == BrokerApprovalOperation.Read
                ? BrokerAuthenticationPurpose.ReadApproval
                : BrokerAuthenticationPurpose.WriteApproval;
            Func<BrokerAuthenticationPurpose, bool> authenticate;
            lock (_observerLock)
            {
                authenticate = _authenticate;
            }
            // Authentication runs outside the state lock; the later updateEntry
            // re-checks the request is still pending.
            if (!authenticate(purpose))
            {
                throw new BrokerApprovalException(BrokerApprovalError.AuthenticationFailed);
            }
        }
        // A slow authentication must not let the approval outlive its deadline.
        var verifiedAt = decision.Kind == BrokerApprovalDecisionKind.Deny ? now : Max(now, _clock());

        return UpdateEntry(requestId, capability, verifiedAt, entry =>
        {
            if (entry.State != BrokerRequestState.Pending)
            {
                throw new BrokerApprovalException(BrokerApprovalError.InvalidDecision);
            }
            switch (decision.Kind)
            {
                case BrokerApprovalDecisionKind.Deny:
                    entry.State = BrokerRequestState.Denied;
                    break;
                case BrokerApprovalDecisionKind.Once:
                    entry.State = BrokerRequestState.Approved;
                    entry.ApprovedByTimedAllowance = false;
                    break;
                case BrokerApprovalDecisionKind.TimedAllow:
                    var windowEnd = verifiedAt + (decision.Duration ?? _defaultTimedAllowance);
                    var effectiveWindowEnd = entry.CredentialExpiresAt is { } credentialExpiresAt
                        ? Min(windowEnd, credentialExpiresAt)
                        : windowEnd;
                    _timedAllowances[entry.Request.CredentialId] = effectiveWindowEnd;
                    entry.ExpiresAt = Min(entry.ExpiresAt, effectiveWindowEnd);
                    entry.State = BrokerRequestState.Approved;
                    entry.ApprovedByTimedAllowance = true;
                    break;
            }
        });
    }
}
