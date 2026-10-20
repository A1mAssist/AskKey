namespace AskKey.Broker.Protocol;

public sealed class BrokerHealth : IEquatable<BrokerHealth>
{
    public int Version { get; }
    public string Status { get; }

    public BrokerHealth(int version, string status)
    {
        Version = version;
        Status = status;
    }

    public bool Equals(BrokerHealth? other)
        => other is not null && Version == other.Version && Status == other.Status;

    public override bool Equals(object? obj) => Equals(obj as BrokerHealth);

    public override int GetHashCode() => HashCode.Combine(Version, Status);
}

public sealed class BrokerVersion : IEquatable<BrokerVersion>
{
    public int ProtocolVersion { get; }

    public BrokerVersion(int protocolVersion) => ProtocolVersion = protocolVersion;

    public bool Equals(BrokerVersion? other)
        => other is not null && ProtocolVersion == other.ProtocolVersion;

    public override bool Equals(object? obj) => Equals(obj as BrokerVersion);

    public override int GetHashCode() => ProtocolVersion;
}

public enum BrokerCatalogPayloadKind
{
    Text,
    File,
}

public sealed class BrokerCatalogItem : IEquatable<BrokerCatalogItem>
{
    public string? CredentialId { get; init; }
    public IReadOnlyList<BrokerCatalogComponent>? Components { get; init; }
    public required string Name { get; init; }
    public required BrokerCatalogPayloadKind PayloadKind { get; init; }
    public required string UsageInstructions { get; init; }
    public string? Group { get; init; }
    public string? EnvironmentVariable { get; init; }
    public required bool Expired { get; init; }

    public bool Equals(BrokerCatalogItem? other)
        => other is not null
            && CredentialId == other.CredentialId
            && Name == other.Name
            && PayloadKind == other.PayloadKind
            && UsageInstructions == other.UsageInstructions
            && Group == other.Group
            && EnvironmentVariable == other.EnvironmentVariable
            && Expired == other.Expired
            && (Components is null
                ? other.Components is null
                : other.Components is not null && Components.SequenceEqual(other.Components));

    public override bool Equals(object? obj) => Equals(obj as BrokerCatalogItem);

    public override int GetHashCode() => HashCode.Combine(Name, PayloadKind, UsageInstructions, Group, Expired);
}
