namespace AskKey.Broker.Runtime;

public enum BrokerTextRuntimeError
{
    MissingCommand,
    MissingCredentials,
    InvalidRequest,
    InvalidWorkingDirectory,
    InvalidCredentialMapping,
    UnresolvedExecutable,
    SpawnDeadlineExpired,
    SpawnFailed,
}

public sealed class BrokerTextRuntimeException : Exception
{
    public BrokerTextRuntimeException(BrokerTextRuntimeError error) : base(error.ToString()) => Error = error;

    public BrokerTextRuntimeError Error { get; }
}
