using AskKey.Broker.Approval;

namespace AskKey.Broker.Protocol;

public sealed class BrokerPayload : IEquatable<BrokerPayload>
{
    public enum Kind
    {
        Health,
        Version,
        Catalog,
        RequestStatus,
        TextRun,
        FileWrite,
        TextWriteRequest,
        TextWriteResult,
        OrganizationWriteResult,
    }

    public Kind PayloadKind { get; }
    public BrokerHealth? Health { get; }
    public BrokerVersion? Version { get; }
    public IReadOnlyList<BrokerCatalogItem>? CatalogItems { get; }
    public IReadOnlyList<string>? CatalogGroups { get; }
    public BrokerRequestState RequestState { get; }
    public BrokerTextRunResult? TextRun { get; }
    public BrokerFileWritePayload? FileWrite { get; }
    public AgentTextWriteRequestOutcome? TextWriteRequest { get; }
    public AgentTextWriteResult? TextWriteResult { get; }
    public string? OrganizationOperationId { get; }

    private BrokerPayload(
        Kind kind,
        BrokerHealth? health = null,
        BrokerVersion? version = null,
        IReadOnlyList<BrokerCatalogItem>? catalogItems = null,
        IReadOnlyList<string>? catalogGroups = null,
        BrokerRequestState requestState = default,
        BrokerTextRunResult? textRun = null,
        BrokerFileWritePayload? fileWrite = null,
        AgentTextWriteRequestOutcome? textWriteRequest = null,
        AgentTextWriteResult? textWriteResult = null,
        string? organizationOperationId = null)
    {
        PayloadKind = kind;
        Health = health;
        Version = version;
        CatalogItems = catalogItems;
        CatalogGroups = catalogGroups;
        RequestState = requestState;
        TextRun = textRun;
        FileWrite = fileWrite;
        TextWriteRequest = textWriteRequest;
        TextWriteResult = textWriteResult;
        OrganizationOperationId = organizationOperationId;
    }

    public static BrokerPayload FromHealth(BrokerHealth health) => new(Kind.Health, health: health);

    public static BrokerPayload FromVersion(BrokerVersion version) => new(Kind.Version, version: version);

    public static BrokerPayload FromCatalog(IReadOnlyList<BrokerCatalogItem> items, IReadOnlyList<string>? groups = null)
        => new(Kind.Catalog, catalogItems: items, catalogGroups: groups ?? Array.Empty<string>());

    public static BrokerPayload FromRequestStatus(BrokerRequestState state)
        => new(Kind.RequestStatus, requestState: state);

    public static BrokerPayload FromTextRun(BrokerTextRunResult result) => new(Kind.TextRun, textRun: result);

    public static BrokerPayload FromFileWrite(BrokerFileWritePayload payload) => new(Kind.FileWrite, fileWrite: payload);

    public static BrokerPayload FromTextWriteRequest(AgentTextWriteRequestOutcome outcome)
        => new(Kind.TextWriteRequest, textWriteRequest: outcome);

    public static BrokerPayload FromTextWriteResult(AgentTextWriteResult result)
        => new(Kind.TextWriteResult, textWriteResult: result);

    public static BrokerPayload FromOrganizationWriteResult(string operationId)
        => new(Kind.OrganizationWriteResult, organizationOperationId: operationId);

    public bool Equals(BrokerPayload? other)
    {
        if (other is null || PayloadKind != other.PayloadKind) return false;
        return PayloadKind switch
        {
            Kind.Health => Equals(Health, other.Health),
            Kind.Version => Equals(Version, other.Version),
            Kind.Catalog => (CatalogItems ?? Array.Empty<BrokerCatalogItem>())
                    .SequenceEqual(other.CatalogItems ?? Array.Empty<BrokerCatalogItem>())
                && (CatalogGroups is null
                    ? other.CatalogGroups is null
                    : other.CatalogGroups is not null && CatalogGroups.SequenceEqual(other.CatalogGroups)),
            Kind.RequestStatus => RequestState == other.RequestState,
            Kind.TextRun => Equals(TextRun, other.TextRun),
            Kind.FileWrite => Equals(FileWrite, other.FileWrite),
            Kind.TextWriteRequest => Equals(TextWriteRequest, other.TextWriteRequest),
            Kind.TextWriteResult => Equals(TextWriteResult, other.TextWriteResult),
            Kind.OrganizationWriteResult => OrganizationOperationId == other.OrganizationOperationId,
            _ => false,
        };
    }

    public override bool Equals(object? obj) => Equals(obj as BrokerPayload);

    public override int GetHashCode() => HashCode.Combine(PayloadKind, RequestState, OrganizationOperationId);
}

public sealed class BrokerResponse : IEquatable<BrokerResponse>
{
    public enum Kind
    {
        Success,
        Failure,
    }

    public Kind ResponseKind { get; }
    public BrokerPayload? Payload { get; }
    public BrokerErrorCode Error { get; }

    private BrokerResponse(Kind kind, BrokerPayload? payload, BrokerErrorCode error)
    {
        ResponseKind = kind;
        Payload = payload;
        Error = error;
    }

    public static BrokerResponse Success(BrokerPayload payload) => new(Kind.Success, payload, default);

    public static BrokerResponse Failure(BrokerErrorCode error) => new(Kind.Failure, null, error);

    public bool Equals(BrokerResponse? other)
        => other is not null && ResponseKind == other.ResponseKind
            && Equals(Payload, other.Payload) && Error == other.Error;

    public override bool Equals(object? obj) => Equals(obj as BrokerResponse);

    public override int GetHashCode() => HashCode.Combine(ResponseKind, Error);

    public override string ToString()
        => ResponseKind == Kind.Success ? $"success({Payload?.PayloadKind})" : $"failure({Error})";
}

