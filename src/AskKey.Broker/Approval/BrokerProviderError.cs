namespace AskKey.Broker.Approval;

public enum BrokerProviderError
{
    AgentAccessPaused,
    RequestRejected,
    InvalidRequest,
    RequestNotFound,
    ResourceExhausted,
    /// <summary>The vault is locked. The request was not executed and can be retried
    /// once the user unlocks Ask Key.</summary>
    VaultLocked,
}

public sealed class BrokerProviderException : Exception
{
    public BrokerProviderError Error { get; }

    public BrokerProviderException(BrokerProviderError error) : base(error.ToString())
    {
        Error = error;
    }
}
