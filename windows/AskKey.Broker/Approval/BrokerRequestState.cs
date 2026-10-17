namespace AskKey.Broker.Approval;

public enum BrokerRequestState
{
    Pending,
    Approved,
    Denied,
    Cancelled,
    Expired,
    Completed,
    Consumed,
    OutcomeUnknown,
}
