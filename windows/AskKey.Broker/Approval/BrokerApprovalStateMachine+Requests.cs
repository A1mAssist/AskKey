namespace AskKey.Broker.Approval;

public sealed partial class BrokerApprovalStateMachine
{
    public BrokerApprovalTicket Submit(
        BrokerApprovalOperationRequest request,
        BrokerCredentialDeadline trustedCredentialDeadline,
        string? trustedCredentialName = null)
        => Submit(request, trustedCredentialDeadline, trustedCredentialName, _clock());

    /// <summary>Returns a retained terminal result for an exact network retransmission
    /// without requiring the caller-known payload to be frozen again.</summary>
    public BrokerApprovalTicket? TerminalRetransmission(string operationId, string payloadDigest)
        => Mutate<BrokerApprovalTicket?>(() =>
        {
            if (operationId.Length == 0
                || Utf8Length(operationId) > BrokerLimits.MaximumFieldBytes
                || !IsHex64(payloadDigest))
            {
                throw new BrokerApprovalException(BrokerApprovalError.InvalidRequest);
            }
            ExpireUnconsumedLocked(_clock());
            if (!_entriesByOperationId.TryGetValue(operationId, out var existing)) return null;
            var boundDigest = existing.Request.RetransmissionDigest ?? existing.Request.PayloadDigest;
            if (!BrokerApprovalOperationRequest.ConstantTimeEqual(boundDigest, payloadDigest))
            {
                throw new BrokerApprovalException(BrokerApprovalError.PayloadMismatch);
            }
            if (existing.State == BrokerRequestState.Pending || existing.State == BrokerRequestState.Approved)
            {
                return null;
            }
            existing.RetryCount += 1;
            return TicketFor(existing);
        });

    /// <summary>Test seam. Production callers cannot supply time.</summary>
    internal BrokerApprovalTicket Submit(BrokerApprovalOperationRequest request, DateTimeOffset now)
        => Submit(request, BrokerCredentialDeadline.None, null, now);

    /// <summary>Test seam. Production callers must supply the Vault-derived deadline.</summary>
    internal BrokerApprovalTicket Submit(BrokerApprovalOperationRequest request)
        => Submit(request, BrokerCredentialDeadline.None, null, _clock());

    private BrokerApprovalTicket Submit(
        BrokerApprovalOperationRequest request,
        BrokerCredentialDeadline trustedCredentialDeadline,
        string? trustedCredentialName,
        DateTimeOffset now)
        => Mutate(() =>
        {
            if (!Valid(request)) throw new BrokerApprovalException(BrokerApprovalError.InvalidRequest);
            if (_paused) throw new BrokerApprovalException(BrokerApprovalError.AgentAccessPaused);
            ExpireUnconsumedLocked(now);
            ExpireTimedAllowancesLocked(now);
            if (_entriesByOperationId.TryGetValue(request.OperationId, out var existing))
            {
                if (existing.Request != request || existing.CredentialExpiresAt != trustedCredentialDeadline.Date)
                {
                    throw new BrokerApprovalException(BrokerApprovalError.PayloadMismatch);
                }
                existing.RetryCount += 1;
                return TicketFor(existing);
            }
            var activeCount = 0;
            foreach (var candidate in _entriesByOperationId.Values)
            {
                if (candidate.State == BrokerRequestState.Pending || candidate.State == BrokerRequestState.Approved)
                {
                    activeCount++;
                }
            }
            if (activeCount >= BrokerLimits.MaximumPendingApprovalRequests)
            {
                throw new BrokerApprovalException(BrokerApprovalError.CapacityReached);
            }
            DateTimeOffset? allowanceEnd = null;
            if (request.Operation == BrokerApprovalOperation.Read
                && _timedAllowances.TryGetValue(request.CredentialId, out var allowanceDeadline))
            {
                allowanceEnd = allowanceDeadline;
            }
            var state = allowanceEnd is not null ? BrokerRequestState.Approved : BrokerRequestState.Pending;
            MakeRetentionRoom();
            var requestDeadline = now + _requestTtl;
            var credentialDeadline = trustedCredentialDeadline.Date ?? requestDeadline;
            var expiresAt = Min(requestDeadline, Min(credentialDeadline, allowanceEnd ?? requestDeadline));
            if (expiresAt <= now) throw new BrokerApprovalException(BrokerApprovalError.InvalidRequest);
            var entry = new Entry
            {
                TrustedCredentialName = trustedCredentialName,
                Request = request,
                RequestId = Guid.NewGuid().ToString(),
                Capability = Guid.NewGuid().ToString(),
                ExpiresAt = expiresAt,
                CredentialExpiresAt = trustedCredentialDeadline.Date,
                State = state,
                ApprovedByTimedAllowance = allowanceEnd is not null,
                RetryCount = 0,
                Committing = false,
            };
            _entriesByOperationId[request.OperationId] = entry;
            _operationOrder.Add(request.OperationId);
            return TicketFor(entry);
        });

    public BrokerRequestState Status(string requestId, string capability)
        => Status(requestId, capability, _clock());

    public BrokerRequestState Status(
        string requestId,
        string capability,
        BrokerApprovalOperationRequest operationRequest)
        => WithEntry(requestId, capability, _clock(), entry =>
        {
            if (!entry.Request.MatchesForConsumption(operationRequest))
            {
                throw new BrokerApprovalException(BrokerApprovalError.PayloadMismatch);
            }
            if (entry.State != BrokerRequestState.Pending && entry.State != BrokerRequestState.Approved)
            {
                throw new BrokerApprovalException(BrokerApprovalError.InvalidDecision);
            }
            return entry.State;
        });

    /// <summary>Test seam. Production callers cannot supply time.</summary>
    internal BrokerRequestState Status(string requestId, string capability, DateTimeOffset now)
        => WithEntry(requestId, capability, now, entry => entry.State);

    public BrokerRequestState Cancel(string requestId, string capability)
        => Cancel(requestId, capability, _clock());

    public BrokerRequestState Cancel(
        string requestId,
        string capability,
        BrokerApprovalOperationRequest operationRequest)
        => UpdateEntry(requestId, capability, _clock(), entry =>
        {
            if (!entry.Request.MatchesForConsumption(operationRequest))
            {
                throw new BrokerApprovalException(BrokerApprovalError.PayloadMismatch);
            }
            if (!entry.Committing
                && (entry.State == BrokerRequestState.Pending || entry.State == BrokerRequestState.Approved))
            {
                entry.State = BrokerRequestState.Cancelled;
            }
        }).State;

    /// <summary>Test seam. Production callers cannot supply time.</summary>
    internal BrokerRequestState Cancel(string requestId, string capability, DateTimeOffset now)
        => UpdateEntry(requestId, capability, now, entry =>
        {
            if (!entry.Committing
                && (entry.State == BrokerRequestState.Pending || entry.State == BrokerRequestState.Approved))
            {
                entry.State = BrokerRequestState.Cancelled;
            }
        }).State;

    public IReadOnlyList<BrokerPendingApproval> PendingRequests()
        => Mutate<IReadOnlyList<BrokerPendingApproval>>(() =>
        {
            ExpireUnconsumedLocked(_clock());
            var result = new List<BrokerPendingApproval>();
            foreach (var operationId in _operationOrder)
            {
                if (!_entriesByOperationId.TryGetValue(operationId, out var entry)) continue;
                if (entry.State != BrokerRequestState.Pending) continue;
                result.Add(new BrokerPendingApproval(
                    entry.RequestId,
                    entry.Capability,
                    entry.Request,
                    entry.ExpiresAt,
                    entry.TrustedCredentialName));
            }
            return result;
        });
}
