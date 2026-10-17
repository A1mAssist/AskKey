using AskKey.Broker.Approval;

namespace AskKey.Broker.Protocol;

public sealed class BrokerFileWriteBeginRequest : IEquatable<BrokerFileWriteBeginRequest>
{
    public string OperationId { get; }
    public string CredentialId { get; }
    public string TargetId { get; }
    public BrokerApprovalOperation Operation { get; }
    public string OriginalFilename { get; }
    public long ExpectedByteCount { get; }

    public BrokerFileWriteBeginRequest(
        string operationId,
        string credentialId,
        string targetId,
        BrokerApprovalOperation operation,
        string originalFilename,
        long expectedByteCount)
    {
        OperationId = operationId;
        CredentialId = credentialId;
        TargetId = targetId;
        Operation = operation;
        OriginalFilename = originalFilename;
        ExpectedByteCount = expectedByteCount;
    }

    public bool Equals(BrokerFileWriteBeginRequest? other)
        => other is not null && OperationId == other.OperationId
            && CredentialId == other.CredentialId && TargetId == other.TargetId
            && Operation == other.Operation && OriginalFilename == other.OriginalFilename
            && ExpectedByteCount == other.ExpectedByteCount;

    public override bool Equals(object? obj) => Equals(obj as BrokerFileWriteBeginRequest);

    public override int GetHashCode() => HashCode.Combine(OperationId, CredentialId, TargetId, Operation);
}

public sealed class BrokerFileWriteChunkRequest : IEquatable<BrokerFileWriteChunkRequest>
{
    public string UploadId { get; }
    public string Capability { get; }
    public long Offset { get; }
    public byte[] Bytes { get; }

    public BrokerFileWriteChunkRequest(string uploadId, string capability, long offset, byte[] bytes)
    {
        UploadId = uploadId;
        Capability = capability;
        Offset = offset;
        Bytes = bytes;
    }

    public bool Equals(BrokerFileWriteChunkRequest? other)
        => other is not null && UploadId == other.UploadId && Capability == other.Capability
            && Offset == other.Offset && Bytes.AsSpan().SequenceEqual(other.Bytes);

    public override bool Equals(object? obj) => Equals(obj as BrokerFileWriteChunkRequest);

    public override int GetHashCode() => HashCode.Combine(UploadId, Capability, Offset, Bytes.Length);
}

public sealed class BrokerFileWriteFreezeRequest : IEquatable<BrokerFileWriteFreezeRequest>
{
    public string UploadId { get; }
    public string Capability { get; }

    public BrokerFileWriteFreezeRequest(string uploadId, string capability)
    {
        UploadId = uploadId;
        Capability = capability;
    }

    public bool Equals(BrokerFileWriteFreezeRequest? other)
        => other is not null && UploadId == other.UploadId && Capability == other.Capability;

    public override bool Equals(object? obj) => Equals(obj as BrokerFileWriteFreezeRequest);

    public override int GetHashCode() => HashCode.Combine(UploadId, Capability);
}

public sealed class BrokerFileWriteRequest : IEquatable<BrokerFileWriteRequest>
{
    public enum Kind
    {
        BeginComponent,
        FreezeComponent,
        CancelUpload,
        Begin,
        Append,
        Freeze,
    }

    public Kind RequestKind { get; }
    public BrokerComponentUploadBeginRequest? ComponentBegin { get; }
    public BrokerFileWriteFreezeRequest? FreezeRequest { get; }
    public BrokerFileWriteBeginRequest? BeginRequest { get; }
    public BrokerFileWriteChunkRequest? ChunkRequest { get; }

    private BrokerFileWriteRequest(
        Kind kind,
        BrokerComponentUploadBeginRequest? componentBegin,
        BrokerFileWriteFreezeRequest? freeze,
        BrokerFileWriteBeginRequest? begin,
        BrokerFileWriteChunkRequest? chunk)
    {
        RequestKind = kind;
        ComponentBegin = componentBegin;
        FreezeRequest = freeze;
        BeginRequest = begin;
        ChunkRequest = chunk;
    }

    public static BrokerFileWriteRequest BeginComponent(BrokerComponentUploadBeginRequest request)
        => new(Kind.BeginComponent, request, null, null, null);

    public static BrokerFileWriteRequest FreezeComponent(BrokerFileWriteFreezeRequest request)
        => new(Kind.FreezeComponent, null, request, null, null);

    public static BrokerFileWriteRequest CancelUpload(BrokerFileWriteFreezeRequest request)
        => new(Kind.CancelUpload, null, request, null, null);

    public static BrokerFileWriteRequest Begin(BrokerFileWriteBeginRequest request)
        => new(Kind.Begin, null, null, request, null);

    public static BrokerFileWriteRequest Append(BrokerFileWriteChunkRequest request)
        => new(Kind.Append, null, null, null, request);

    public static BrokerFileWriteRequest Freeze(BrokerFileWriteFreezeRequest request)
        => new(Kind.Freeze, null, request, null, null);

    public bool Equals(BrokerFileWriteRequest? other)
        => other is not null && RequestKind == other.RequestKind
            && Equals(ComponentBegin, other.ComponentBegin)
            && Equals(FreezeRequest, other.FreezeRequest)
            && Equals(BeginRequest, other.BeginRequest)
            && Equals(ChunkRequest, other.ChunkRequest);

    public override bool Equals(object? obj) => Equals(obj as BrokerFileWriteRequest);

    public override int GetHashCode() => HashCode.Combine(RequestKind, FreezeRequest?.UploadId, BeginRequest?.OperationId);
}

public sealed class BrokerFileUploadTicket : IEquatable<BrokerFileUploadTicket>
{
    public string UploadId { get; }
    public string Capability { get; }

    public BrokerFileUploadTicket(string uploadId, string capability)
    {
        UploadId = uploadId;
        Capability = capability;
    }

    public bool Equals(BrokerFileUploadTicket? other)
        => other is not null && UploadId == other.UploadId && Capability == other.Capability;

    public override bool Equals(object? obj) => Equals(obj as BrokerFileUploadTicket);

    public override int GetHashCode() => HashCode.Combine(UploadId, Capability);
}

public sealed class BrokerFileWritePayload : IEquatable<BrokerFileWritePayload>
{
    public enum Kind
    {
        ComponentFrozen,
        UploadCancelled,
        Upload,
        ChunkAccepted,
        Approval,
    }

    public Kind PayloadKind { get; }
    public BrokerComponentFileReference? ComponentReference { get; }
    public BrokerFileUploadTicket? UploadTicket { get; }
    public long NextOffset { get; }
    public BrokerApprovalTicket? ApprovalTicket { get; }

    private BrokerFileWritePayload(
        Kind kind,
        BrokerComponentFileReference? reference,
        BrokerFileUploadTicket? upload,
        long nextOffset,
        BrokerApprovalTicket? approval)
    {
        PayloadKind = kind;
        ComponentReference = reference;
        UploadTicket = upload;
        NextOffset = nextOffset;
        ApprovalTicket = approval;
    }

    public static BrokerFileWritePayload ComponentFrozen(BrokerComponentFileReference reference)
        => new(Kind.ComponentFrozen, reference, null, 0, default);

    public static BrokerFileWritePayload UploadCancelled { get; }
        = new(Kind.UploadCancelled, null, null, 0, default);

    public static BrokerFileWritePayload Upload(BrokerFileUploadTicket ticket)
        => new(Kind.Upload, null, ticket, 0, default);

    public static BrokerFileWritePayload ChunkAccepted(long nextOffset)
        => new(Kind.ChunkAccepted, null, null, nextOffset, default);

    public static BrokerFileWritePayload Approval(BrokerApprovalTicket ticket)
        => new(Kind.Approval, null, null, 0, ticket);

    public bool Equals(BrokerFileWritePayload? other)
        => other is not null && PayloadKind == other.PayloadKind
            && Equals(ComponentReference, other.ComponentReference)
            && Equals(UploadTicket, other.UploadTicket)
            && NextOffset == other.NextOffset
            && Nullable.Equals(ApprovalTicket, other.ApprovalTicket);

    public override bool Equals(object? obj) => Equals(obj as BrokerFileWritePayload);

    public override int GetHashCode() => HashCode.Combine(PayloadKind, NextOffset, UploadTicket?.UploadId);
}

/// <summary>App-only projection of a frozen file write; carries no credential values.</summary>
public sealed class BrokerFileWriteSummary
{
    public required string TargetId { get; init; }
    public required BrokerApprovalOperation Operation { get; init; }
    public required BrokerCatalogPayloadKind PayloadKind { get; init; }
    public required string OriginalFilename { get; init; }
    public required int ByteCount { get; init; }
    public string? PreviousDigest { get; init; }
    public required string Digest { get; init; }
    public required bool PayloadMasked { get; init; }
    public required BrokerRequestState State { get; init; }
}

public sealed class BrokerFrozenFile
{
    public required string OperationId { get; init; }
    public required string CredentialId { get; init; }
    public required string TargetId { get; init; }
    public required BrokerApprovalOperation Operation { get; init; }
    public string? PreviousDigest { get; init; }
    public required string OriginalFilename { get; init; }
    public required byte[] Bytes { get; init; }
    public required int ByteCount { get; init; }
    public required string Digest { get; init; }
    public string? ApprovalRequestId { get; init; }
    public string? ApprovalCapability { get; init; }

    /// <summary>Length-prefixed canonical fields, exactly as the Swift approval payload digest.</summary>
    public string ApprovalPayloadDigest
    {
        get
        {
            var fields = new[]
            {
                OperationId,
                CredentialId,
                TargetId,
                BrokerWire.ToWire(Operation),
                OriginalFilename,
                ByteCount.ToString(global::System.Globalization.CultureInfo.InvariantCulture),
                PreviousDigest ?? "none",
                Digest,
            };
            var canonical = string.Join("|", fields.Select(text => $"{BrokerWire.Utf8Length(text)}:{text}"));
            return global::System.Convert.ToHexStringLower(
                global::System.Security.Cryptography.SHA256.HashData(global::System.Text.Encoding.UTF8.GetBytes(canonical)));
        }
    }
}

