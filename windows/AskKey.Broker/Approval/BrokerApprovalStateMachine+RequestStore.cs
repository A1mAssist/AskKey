namespace AskKey.Broker.Approval;

public sealed partial class BrokerApprovalStateMachine
{
    private T WithEntry<T>(
        string requestId,
        string capability,
        DateTimeOffset now,
        Func<Entry, T> body)
        => Mutate(() =>
        {
            ExpireUnconsumedLocked(now);
            var entry = MatchingEntry(requestId, capability)
                ?? throw new BrokerApprovalException(BrokerApprovalError.RequestNotFound);
            // Swift passes the struct by value; the body must not mutate the store.
            return body(entry.Copy());
        });

    private BrokerApprovalTicket UpdateEntry(
        string requestId,
        string capability,
        DateTimeOffset now,
        Action<Entry> body)
        => Mutate(() =>
        {
            ExpireUnconsumedLocked(now);
            var operationId = FindOperationId(requestId, capability)
                ?? throw new BrokerApprovalException(BrokerApprovalError.RequestNotFound);
            // Swift mutates a copy and writes it back, so a throwing body leaves
            // the stored entry untouched.
            var entry = _entriesByOperationId[operationId].Copy();
            body(entry);
            _entriesByOperationId[operationId] = entry;
            return TicketFor(entry);
        });

    private string? FindOperationId(string requestId, string capability)
    {
        foreach (var (operationId, entry) in _entriesByOperationId)
        {
            if (entry.RequestId == requestId && entry.Capability == capability) return operationId;
        }
        return null;
    }

    internal static bool Valid(BrokerApprovalOperationRequest request)
    {
        var required = new[] { request.OperationId, request.TargetId, request.PayloadDigest };
        foreach (var value in required)
        {
            if (value.Length == 0 || Utf8Length(value) > BrokerLimits.MaximumFieldBytes) return false;
        }
        foreach (var optional in new[] { request.CredentialName, request.CallerName, request.CallerPurpose })
        {
            if (optional is not null && Utf8Length(optional) > BrokerLimits.MaximumFieldBytes) return false;
        }
        if (!ValidSubject(request)) return false;
        if (request.RetransmissionDigest is { } retransmission && !IsHex64(retransmission)) return false;
        return IsHex64(request.PayloadDigest);
    }

    private static bool ValidSubject(BrokerApprovalOperationRequest request)
    {
        if (request.Operation == BrokerApprovalOperation.Organize)
        {
            if (request.CredentialId.Length != 0 || request.TargetId != "credential-library") return false;
            if (request.CredentialName is not null || request.DisplayValue is not null) return false;
            if (request.OrganizationCredentialIds is not { } ids) return false;
            var sortedUnique = ids.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal);
            if (!ids.SequenceEqual(sortedUnique, StringComparer.Ordinal)) return false;
            foreach (var id in ids)
            {
                if (id.Length == 0 || Utf8Length(id) > BrokerLimits.MaximumFieldBytes) return false;
            }
            return true;
        }
        if (request.OrganizationCredentialIds is not null) return false;
        if (request.CredentialId.Length == 0) return false;
        if (Utf8Length(request.CredentialId) > BrokerLimits.MaximumFieldBytes) return false;
        return request.Operation == BrokerApprovalOperation.Create || request.TargetId == request.CredentialId;
    }

    private static int Utf8Length(string value) => System.Text.Encoding.UTF8.GetByteCount(value);

    private static bool IsHex64(string value)
    {
        if (value.Length != 64) return false;
        foreach (var character in value)
        {
            var isHex = character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!isHex) return false;
        }
        return true;
    }

    private void MakeRetentionRoom()
    {
        if (_entriesByOperationId.Count < BrokerLimits.MaximumRetainedRequestStates) return;
        var index = _operationOrder.FindIndex(operationId =>
            _entriesByOperationId.TryGetValue(operationId, out var entry)
            && entry.State != BrokerRequestState.Pending
            && entry.State != BrokerRequestState.Approved);
        if (index < 0) throw new BrokerApprovalException(BrokerApprovalError.CapacityReached);
        _entriesByOperationId.Remove(_operationOrder[index]);
        _operationOrder.RemoveAt(index);
    }

    private Entry? MatchingEntry(string requestId, string capability)
    {
        // Bounded O(n) lookup (max 256); add an index only if the cap grows.
        foreach (var entry in _entriesByOperationId.Values)
        {
            if (entry.RequestId == requestId && entry.Capability == capability) return entry;
        }
        return null;
    }

    private static BrokerApprovalTicket TicketFor(Entry entry)
        => new(entry.RequestId, entry.Capability, entry.State, entry.RetryCount);
}
