namespace AskKey.Broker.Approval;

public enum BrokerProviderError
{
    AgentAccessPaused,
    RequestRejected,
    InvalidRequest,
    RequestNotFound,
    ResourceExhausted,
}

public sealed class BrokerProviderException : Exception
{
    public BrokerProviderError Error { get; }

    public BrokerProviderException(BrokerProviderError error) : base(error.ToString())
    {
        Error = error;
    }
}
