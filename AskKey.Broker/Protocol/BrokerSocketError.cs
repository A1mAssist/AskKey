namespace AskKey.Broker.Protocol;

public enum BrokerSocketError
{
    PathTooLong,
    NotRunning,
    NoResponse,
    FrameTooLarge,
    ResponseTooLarge,
    MalformedResponse,
    BrokerFailure,
    SystemError,
}

public sealed class BrokerSocketException : Exception
{
    public BrokerSocketException(BrokerSocketError error, BrokerErrorCode brokerFailure = default, string? detail = null)
        : base(Describe(error, brokerFailure, detail))
    {
        Error = error;
        BrokerFailure = brokerFailure;
        Detail = detail;
    }

    public BrokerSocketError Error { get; }

    /// <summary>Only meaningful when <see cref="Error"/> is <see cref="BrokerSocketError.BrokerFailure"/>.</summary>
    public BrokerErrorCode BrokerFailure { get; }

    public string? Detail { get; }

    private static string Describe(BrokerSocketError error, BrokerErrorCode brokerFailure, string? detail) => error switch
    {
        BrokerSocketError.PathTooLong => "The Broker pipe name is too long.",
        BrokerSocketError.NotRunning => "The Ask Key Broker is not running.",
        BrokerSocketError.NoResponse => "The Ask Key Broker closed the connection without responding.",
        BrokerSocketError.FrameTooLarge => "The Broker request exceeds its fixed size limit.",
        BrokerSocketError.ResponseTooLarge => "The Broker response exceeds its fixed size limit.",
        BrokerSocketError.MalformedResponse => "The Ask Key Broker returned a malformed response.",
        BrokerSocketError.BrokerFailure => $"The Ask Key Broker rejected the request ({BrokerWire.ToWire(brokerFailure)}).",
        _ => $"Broker transport failed ({detail ?? "unspecified"}).",
    };
}
