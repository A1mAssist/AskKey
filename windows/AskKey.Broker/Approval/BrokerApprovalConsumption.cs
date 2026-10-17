namespace AskKey.Broker.Approval;

public sealed class BrokerApprovalConsumption : IEquatable<BrokerApprovalConsumption>
{
    public string RequestId { get; }
    public string Capability { get; }
    public BrokerApprovalOperationRequest OperationRequest { get; }

    public BrokerApprovalConsumption(string requestId, string capability, BrokerApprovalOperationRequest operationRequest)
    {
        RequestId = requestId;
        Capability = capability;
        OperationRequest = operationRequest;
    }

    public bool Equals(BrokerApprovalConsumption? other)
        => other is not null && RequestId == other.RequestId && Capability == other.Capability
            && OperationRequest == other.OperationRequest;

    public override bool Equals(object? obj) => Equals(obj as BrokerApprovalConsumption);

    public override int GetHashCode() => HashCode.Combine(RequestId, Capability, OperationRequest);
}
