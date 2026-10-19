namespace AskKey.Broker.Approval;

public sealed partial class BrokerApprovalStateMachine
{
    public BrokerRequestState Consume(
        string requestId,
        string capability,
        BrokerApprovalOperationRequest operationRequest)
        => ConsumeApproved(
            requestId,
            capability,
            operationRequest,
            perform: static () => BrokerRequestState.Consumed);

    /// <summary>Runs the approved operation while the approval is exclusively held and
    /// marks it consumed only after the operation succeeds. This lets a storage
    /// transaction fail without burning the one-time approval.</summary>
    public T ConsumeApproved<T>(
        string requestId,
        string capability,
        BrokerApprovalOperationRequest operationRequest,
        Func<T> perform)
    {
        ReserveConsumption(requestId, capability, operationRequest);
        try
        {
            var result = perform();
            FinishConsumption(requestId, capability, consumed: true);
            return result;
        }
        catch (Exception)
        {
            // Swift's inner do/catch rethrows a finish failure ahead of the operation
            // failure; an unguarded call has the same effect.
            FinishConsumption(requestId, capability, consumed: false);
            throw;
        }
    }

    /// <summary>Reserves one approved operation, runs the idempotent business write without
    /// the state lock, then consumes it. A thrown write releases the reservation.</summary>
    public BrokerRequestState Consume(
        string requestId,
        string capability,
        BrokerApprovalOperationRequest operationRequest,
        Action performing)
    {
        ReserveConsumption(requestId, capability, operationRequest);
        try
        {
            performing();
        }
        catch (Exception)
        {
            // Swift's inner do/catch rethrows a finish failure ahead of the operation
            // failure; an unguarded call has the same effect.
            FinishConsumption(requestId, capability, consumed: false);
            throw;
        }
        return FinishConsumption(requestId, capability, consumed: true).State;
    }

    /// <summary>Atomically validates and consumes a complete runtime credential set.</summary>
    public void Consume(IReadOnlyList<BrokerApprovalConsumption> consumptions)
    {
        Mutate(() =>
        {
            ConsumeBatchLocked(consumptions);
            return 0;
        });
    }

    /// <summary>Runtime-only consumption. Existing write reservation/commit APIs retain
    /// their transaction and retry semantics and never hold this lock over SQL.</summary>
    public BrokerRuntimeReadAuthorization? ConsumeForRuntime(
        IReadOnlyList<BrokerApprovalConsumption> consumptions)
    {
        var snapshot = Mutate<(Guid Id, DateTimeOffset ExpiresAt)?>(() =>
        {
            if (_paused || !consumptions.All(c => c.OperationRequest.Operation == BrokerApprovalOperation.Read))
            {
                throw new BrokerApprovalException(BrokerApprovalError.InvalidDecision);
            }
            var entries = ConsumeBatchLocked(consumptions);
            if (entries.Count == 0) return null;
            var expiresAt = entries.Min(entry => entry.ExpiresAt);
            var id = Guid.NewGuid();
            var authorization = new RuntimeAuthorization { ExpiresAt = expiresAt };
            foreach (var entry in entries) authorization.CredentialIds.Add(entry.Request.CredentialId);
            _runtimeAuthorizations[id] = authorization;
            return (id, expiresAt);
        });
        return snapshot is { } value
            ? new BrokerRuntimeReadAuthorization(this, value.Id, value.ExpiresAt)
            : null;
    }

    private List<Entry> ConsumeBatchLocked(IReadOnlyList<BrokerApprovalConsumption> consumptions)
    {
        ExpireUnconsumedLocked(_clock());
        var entries = new List<Entry>(consumptions.Count);
        foreach (var consumption in consumptions)
        {
            var entry = MatchingEntry(consumption.RequestId, consumption.Capability)
                ?? throw new BrokerApprovalException(BrokerApprovalError.RequestNotFound);
            if (!entry.Request.MatchesForConsumption(consumption.OperationRequest))
            {
                throw new BrokerApprovalException(BrokerApprovalError.PayloadMismatch);
            }
            if (entry.State == BrokerRequestState.Consumed)
            {
                throw new BrokerApprovalException(BrokerApprovalError.AlreadyConsumed);
            }
            if (entry.Committing)
            {
                throw new BrokerApprovalException(BrokerApprovalError.CommitInProgress);
            }
            if (entry.State != BrokerRequestState.Approved)
            {
                throw new BrokerApprovalException(BrokerApprovalError.InvalidDecision);
            }
            entries.Add(entry.Copy());
        }
        foreach (var entry in entries)
        {
            entry.State = BrokerRequestState.Consumed;
            _entriesByOperationId[entry.Request.OperationId] = entry;
        }
        return entries;
    }

    /// <summary>Test seam. Production callers cannot supply time.</summary>
    internal BrokerRequestState Consume(
        string requestId,
        string capability,
        BrokerApprovalOperationRequest operationRequest,
        DateTimeOffset now)
        => ConsumeApproved(requestId, capability, operationRequest, now, static () => BrokerRequestState.Consumed);

    private T ConsumeApproved<T>(
        string requestId,
        string capability,
        BrokerApprovalOperationRequest operationRequest,
        DateTimeOffset now,
        Func<T> perform)
        => Mutate(() =>
        {
            ExpireUnconsumedLocked(now);
            var operationId = FindOperationId(requestId, capability);
            if (operationId is null) throw new BrokerApprovalException(BrokerApprovalError.RequestNotFound);
            var entry = _entriesByOperationId[operationId];
            if (!entry.Request.MatchesForConsumption(operationRequest))
            {
                throw new BrokerApprovalException(BrokerApprovalError.PayloadMismatch);
            }
            if (entry.State == BrokerRequestState.Consumed)
            {
                throw new BrokerApprovalException(BrokerApprovalError.AlreadyConsumed);
            }
            if (entry.Committing)
            {
                throw new BrokerApprovalException(BrokerApprovalError.CommitInProgress);
            }
            if (entry.State != BrokerRequestState.Approved)
            {
                throw new BrokerApprovalException(BrokerApprovalError.InvalidDecision);
            }
            var result = perform();
            entry.State = BrokerRequestState.Consumed;
            return result;
        });

    private void ReserveConsumption(
        string requestId,
        string capability,
        BrokerApprovalOperationRequest operationRequest)
    {
        _ = UpdateEntry(requestId, capability, _clock(), entry =>
        {
            if (_paused) throw new BrokerApprovalException(BrokerApprovalError.AgentAccessPaused);
            if (!entry.Request.MatchesForConsumption(operationRequest))
            {
                throw new BrokerApprovalException(BrokerApprovalError.PayloadMismatch);
            }
            if (entry.State == BrokerRequestState.Consumed)
            {
                throw new BrokerApprovalException(BrokerApprovalError.AlreadyConsumed);
            }
            if (entry.State != BrokerRequestState.Approved)
            {
                throw new BrokerApprovalException(BrokerApprovalError.InvalidDecision);
            }
            if (entry.Committing)
            {
                throw new BrokerApprovalException(BrokerApprovalError.CommitInProgress);
            }
            entry.Committing = true;
        });
    }

    private BrokerApprovalTicket FinishConsumption(string requestId, string capability, bool consumed)
        => UpdateEntry(requestId, capability, _clock(), entry =>
        {
            if (!entry.Committing) throw new BrokerApprovalException(BrokerApprovalError.InvalidDecision);
            entry.Committing = false;
            if (consumed)
            {
                entry.State = BrokerRequestState.Consumed;
            }
            else if (_paused)
            {
                entry.State = BrokerRequestState.Cancelled;
            }
        });
}
