namespace AskKey.Broker.Protocol;

/// <summary>Wire error vocabulary. Raw values are the Swift case raw values and must not drift.</summary>
public enum BrokerErrorCode
{
    UnsupportedVersion,
    MethodNotAllowed,
    InvalidRequest,
    RequestNotFound,
    ResourceExhausted,
    DeadlineExceeded,
    ResponseTooLarge,
    InternalError,
    AgentAccessPaused,
    RequestRejected,
    /// <summary>The vault is locked, so no credential data could be read or written.
    /// The request was not executed; retry it after the user unlocks Ask Key.</summary>
    VaultLocked,
}
