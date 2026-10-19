namespace AskKey.Broker.Protocol;

public sealed class BrokerComponentFileReference : IEquatable<BrokerComponentFileReference>
{
    public string UploadId { get; }
    public string Capability { get; }
    public string Digest { get; }

    public BrokerComponentFileReference(string uploadId, string capability, string digest)
    {
        UploadId = uploadId;
        Capability = capability;
        Digest = digest;
    }

    public bool Equals(BrokerComponentFileReference? other)
        => other is not null && UploadId == other.UploadId
            && Capability == other.Capability && Digest == other.Digest;

    public override bool Equals(object? obj) => Equals(obj as BrokerComponentFileReference);

    public override int GetHashCode() => HashCode.Combine(UploadId, Capability, Digest);
}

/// <summary>Swift encodes the enum payload plus an environment variable name for the file case.</summary>
public sealed class BrokerComponentDelivery : IEquatable<BrokerComponentDelivery>
{
    public enum Kind
    {
        EnvironmentVariable,
        TemporaryFile,
        None,
    }

    public Kind DeliveryKind { get; }
    public string? Name { get; }

    private BrokerComponentDelivery(Kind kind, string? name)
    {
        DeliveryKind = kind;
        Name = name;
    }

    public static BrokerComponentDelivery EnvironmentVariable(string name) => new(Kind.EnvironmentVariable, name);

    public static BrokerComponentDelivery TemporaryFile(string name) => new(Kind.TemporaryFile, name);

    public static BrokerComponentDelivery None { get; } = new(Kind.None, null);

    /// <summary>Swift projects both variable and temporary-file names here.</summary>
    public string? EnvironmentVariableName => Name;

    public bool Equals(BrokerComponentDelivery? other)
        => other is not null && DeliveryKind == other.DeliveryKind && Name == other.Name;

    public override bool Equals(object? obj) => Equals(obj as BrokerComponentDelivery);

    public override int GetHashCode() => HashCode.Combine(DeliveryKind, Name);
}

public sealed class BrokerCatalogComponent : IEquatable<BrokerCatalogComponent>
{
    public required string Name { get; init; }
    public required BrokerCatalogPayloadKind PayloadKind { get; init; }
    public required BrokerComponentDelivery Delivery { get; init; }

    public bool Equals(BrokerCatalogComponent? other)
        => other is not null && Name == other.Name
            && PayloadKind == other.PayloadKind && Delivery.Equals(other.Delivery);

    public override bool Equals(object? obj) => Equals(obj as BrokerCatalogComponent);

    public override int GetHashCode() => HashCode.Combine(Name, PayloadKind, Delivery);
}

public sealed class BrokerFrozenComponentFile : IEquatable<BrokerFrozenComponentFile>
{
    public string OriginalFilename { get; }
    public byte[] Bytes { get; }
    public string Digest { get; }

    public BrokerFrozenComponentFile(string originalFilename, byte[] bytes, string digest)
    {
        OriginalFilename = originalFilename;
        Bytes = bytes;
        Digest = digest;
    }

    public bool Equals(BrokerFrozenComponentFile? other)
        => other is not null && OriginalFilename == other.OriginalFilename
            && Digest == other.Digest && Bytes.AsSpan().SequenceEqual(other.Bytes);

    public override bool Equals(object? obj) => Equals(obj as BrokerFrozenComponentFile);

    public override int GetHashCode() => HashCode.Combine(OriginalFilename, Digest, Bytes.Length);
}

public sealed class BrokerComponentUploadBeginRequest : IEquatable<BrokerComponentUploadBeginRequest>
{
    public string OperationId { get; }
    public string OriginalFilename { get; }
    public int ExpectedByteCount { get; }

    public BrokerComponentUploadBeginRequest(string operationId, string originalFilename, int expectedByteCount)
    {
        OperationId = operationId;
        OriginalFilename = originalFilename;
        ExpectedByteCount = expectedByteCount;
    }

    public bool Equals(BrokerComponentUploadBeginRequest? other)
        => other is not null && OperationId == other.OperationId
            && OriginalFilename == other.OriginalFilename
            && ExpectedByteCount == other.ExpectedByteCount;

    public override bool Equals(object? obj) => Equals(obj as BrokerComponentUploadBeginRequest);

    public override int GetHashCode() => HashCode.Combine(OperationId, OriginalFilename, ExpectedByteCount);
}

/// <summary>App-memory material; deliberately not part of the Broker response surface.</summary>
public sealed class BrokerCredentialComponentValue
{
    public string? Text { get; }
    public BrokerComponentFileReference? File { get; }

    private BrokerCredentialComponentValue(string? text, BrokerComponentFileReference? file)
    {
        Text = text;
        File = file;
    }

    public static BrokerCredentialComponentValue FromText(string text) => new(text, null);

    public static BrokerCredentialComponentValue FromFile(BrokerComponentFileReference file) => new(null, file);
}

public sealed class BrokerCredentialComponentInput
{
    public required string Name { get; init; }
    public required BrokerCredentialComponentValue Value { get; init; }
    public required BrokerComponentDelivery Delivery { get; init; }
    public bool Masked { get; init; } = true;
}

public sealed class BrokerCredentialComponentChange
{
    public BrokerCredentialComponentInput? Upsert { get; }
    public string? RemoveName { get; }

    private BrokerCredentialComponentChange(BrokerCredentialComponentInput? upsert, string? removeName)
    {
        Upsert = upsert;
        RemoveName = removeName;
    }

    public static BrokerCredentialComponentChange UpsertComponent(BrokerCredentialComponentInput input)
        => new(input, null);

    public static BrokerCredentialComponentChange Remove(string name) => new(null, name);
}

/// <summary>Null group on an action preserves it; <see cref="Ungrouped"/> clears it.</summary>
public sealed class BrokerCredentialGroupChange
{
    public string? Name { get; }
    public bool Ungrouped { get; }

    private BrokerCredentialGroupChange(string? name, bool ungrouped)
    {
        Name = name;
        Ungrouped = ungrouped;
    }

    public static BrokerCredentialGroupChange Named(string name) => new(name, false);

    public static BrokerCredentialGroupChange Clear { get; } = new(null, true);
}
