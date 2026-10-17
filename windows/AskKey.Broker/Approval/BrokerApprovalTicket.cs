namespace AskKey.Broker.Approval;

public readonly struct BrokerApprovalTicket : IEquatable<BrokerApprovalTicket>
{
    public string RequestId { get; }
    public string Capability { get; }
    public BrokerRequestState State { get; }
    public int RetryCount { get; }

    public BrokerApprovalTicket(string requestId, string capability, BrokerRequestState state, int retryCount)
    {
        RequestId = requestId;
        Capability = capability;
        State = state;
        RetryCount = retryCount;
    }

    public bool Equals(BrokerApprovalTicket other)
        => RequestId == other.RequestId && Capability == other.Capability
            && State == other.State && RetryCount == other.RetryCount;

    public override bool Equals(object? obj) => obj is BrokerApprovalTicket other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(RequestId, Capability, State, RetryCount);

    public static bool operator ==(BrokerApprovalTicket left, BrokerApprovalTicket right) => left.Equals(right);

    public static bool operator !=(BrokerApprovalTicket left, BrokerApprovalTicket right) => !left.Equals(right);
}
