namespace AskKey.Broker.Approval;

public static class BrokerLimits
{
    public const int MaximumFrameBytes = 64 * 1024;
    public const int MaximumResponseBytes = 64 * 1024;
    public const int MaximumFieldBytes = 4 * 1024;
    public const int MaximumConnections = 16;
    public const int MaximumRequestsPerConnection = 32;
    public const int MaximumConcurrentRequests = 8;
    public const int MaximumRuntimeReceiptCount = 65_536;
    public const int MaximumQueuedRequests = 32;
    public const int MaximumPendingApprovalRequests = 64;
    public const int MaximumRetainedRequestStates = 256;
    public static readonly TimeSpan ReadDeadline = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan WriteDeadline = TimeSpan.FromSeconds(2);
}
