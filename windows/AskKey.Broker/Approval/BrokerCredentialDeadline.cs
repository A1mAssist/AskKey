namespace AskKey.Broker.Approval;

/// <summary>Vault-derived credential expiry. Agent request fields never supply this value.</summary>
public readonly struct BrokerCredentialDeadline : IEquatable<BrokerCredentialDeadline>
{
    public static BrokerCredentialDeadline None => default;

    public static BrokerCredentialDeadline ExpiresAt(DateTimeOffset date) => new(date);

    public DateTimeOffset? Date { get; }

    private BrokerCredentialDeadline(DateTimeOffset date)
    {
        Date = date;
    }

    public bool Equals(BrokerCredentialDeadline other) => Nullable.Equals(Date, other.Date);

    public override bool Equals(object? obj) => obj is BrokerCredentialDeadline other && Equals(other);

    public override int GetHashCode() => Date.GetHashCode();

    public static bool operator ==(BrokerCredentialDeadline left, BrokerCredentialDeadline right) => left.Equals(right);

    public static bool operator !=(BrokerCredentialDeadline left, BrokerCredentialDeadline right) => !left.Equals(right);
}
