namespace AskKey.Broker.FileWrite;

public enum BrokerFileWriteError
{
    InvalidRequest,
    InvalidCapability,
    OutOfOrderChunk,
    TooLarge,
    CapacityReached,
    TruncatedUpload,
    AlreadyFrozen,
    RequestNotFound,
    DigestMismatch,
    TargetChanged,
    AuthenticationFailed,
    OutcomeUnknown,
    StagingFailed,
    StagingNotADirectory,
    StagingPermissionDenied,
}

public sealed class BrokerFileWriteException : Exception
{
    public BrokerFileWriteException(BrokerFileWriteError error) : base(error.ToString()) => Error = error;

    public BrokerFileWriteError Error { get; }
}
