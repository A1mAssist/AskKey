namespace AskKey.Broker.Approval;

public enum BrokerApprovalError
{
    InvalidRequest,
    PayloadMismatch,
    CapacityReached,
    RequestNotFound,
    AuthenticationFailed,
    InvalidDecision,
    AlreadyConsumed,
    CommitInProgress,
    AgentAccessPaused,
}

public sealed class BrokerApprovalException : Exception
{
    public BrokerApprovalError Error { get; }

    public BrokerApprovalException(BrokerApprovalError error) : base(error.ToString())
    {
        Error = error;
    }
}
