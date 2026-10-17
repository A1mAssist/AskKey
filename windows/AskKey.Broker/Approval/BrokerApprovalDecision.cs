namespace AskKey.Broker.Approval;

public enum BrokerApprovalDecisionKind
{
    Once,
    Deny,
    TimedAllow,
}

public readonly struct BrokerApprovalDecision : IEquatable<BrokerApprovalDecision>
{
    public BrokerApprovalDecisionKind Kind { get; }
    /// <summary>Null selects the configured default allowance window.</summary>
    public TimeSpan? Duration { get; }

    private BrokerApprovalDecision(BrokerApprovalDecisionKind kind, TimeSpan? duration)
    {
        Kind = kind;
        Duration = duration;
    }

    public static BrokerApprovalDecision Once => new(BrokerApprovalDecisionKind.Once, null);

    public static BrokerApprovalDecision Deny => new(BrokerApprovalDecisionKind.Deny, null);

    public static BrokerApprovalDecision TimedAllow(TimeSpan? duration = null)
        => new(BrokerApprovalDecisionKind.TimedAllow, duration);

    public bool Equals(BrokerApprovalDecision other)
        => Kind == other.Kind && Nullable.Equals(Duration, other.Duration);

    public override bool Equals(object? obj) => obj is BrokerApprovalDecision other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Kind, Duration);

    public static bool operator ==(BrokerApprovalDecision left, BrokerApprovalDecision right) => left.Equals(right);

    public static bool operator !=(BrokerApprovalDecision left, BrokerApprovalDecision right) => !left.Equals(right);
}
