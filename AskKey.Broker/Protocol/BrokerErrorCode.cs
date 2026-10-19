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
}
