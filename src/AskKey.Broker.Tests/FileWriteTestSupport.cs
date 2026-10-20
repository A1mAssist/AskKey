using AskKey.Broker.Approval;
using AskKey.Broker.FileWrite;
using AskKey.Broker.Protocol;

namespace AskKey.Broker.Tests;

internal sealed class FileWriteHarness : IDisposable
{
    public BrokerApprovalStateMachine Approvals { get; }
    public BrokerFileWriteCoordinator Coordinator { get; }
    public string Directory { get; }

    public FileWriteHarness(
        Func<bool>? authenticateReveal = null,
        Action<BrokerFrozenFile>? commitFrozenFile = null,
        Func<string, string?, BrokerApprovalOperationRequest, BrokerApprovalTicket>? submit = null,
        Func<string, string>? normalizeCreateTarget = null,
        Func<string, string?>? resolvePreviousDigest = null,
        TimeSpan? uploadTtl = null,
        Func<DateTimeOffset>? clock = null,
        Func<string, string, string, bool>? completedFileCommit = null)
    {
        Directory = ProtocolTestSupport.NewDirectory();
        Approvals = new BrokerApprovalStateMachine(authenticate: _ => true);
        var resolved = resolvePreviousDigest ?? (_ => null);
        Coordinator = new BrokerFileWriteCoordinator(
            Directory,
            Approvals,
            authenticateReveal ?? (() => true),
            commitFrozenFile ?? (_ => { }),
            submit ?? ((credentialId, expectedDigest, request) =>
            {
                var currentDigest = request.Operation == BrokerApprovalOperation.Modify
                    ? resolved(credentialId)
                    : null;
                if (currentDigest != expectedDigest)
                {
                    throw new BrokerFileWriteException(BrokerFileWriteError.TargetChanged);
                }
                return Approvals.Submit(request, BrokerCredentialDeadline.None);
            }),
            normalizeCreateTarget ?? (target => target),
            resolvePreviousDigest: resolved,
            uploadTtl: uploadTtl,
            clock: clock,
            completedFileCommit: completedFileCommit);
    }

    public BrokerFileUploadTicket Begin(string operationId, int expectedByteCount)
        => Coordinator.Begin(operationId, "credential-id", "credential-id",
            BrokerApprovalOperation.Modify, "AuthKey.p8", expectedByteCount);

    public void Dispose()
    {
        Coordinator.Dispose();
        Approvals.Dispose();
        try { System.IO.Directory.Delete(Directory, recursive: true); } catch (IOException) { }
    }
}

internal sealed class LockedDigest
{
    private readonly object _lock = new();
    private string _value;
    public LockedDigest(string value) => _value = value;
    public string Value
    {
        get { lock (_lock) return _value; }
        set { lock (_lock) _value = value; }
    }
}

internal sealed class LockedFlag
{
    private readonly object _lock = new();
    private bool _value;
    public LockedFlag(bool value) => _value = value;
    public bool Value
    {
        get { lock (_lock) return _value; }
        set { lock (_lock) _value = value; }
    }
}

internal sealed class LockedClock
{
    private readonly object _lock = new();
    private DateTimeOffset _value;
    public LockedClock(DateTimeOffset value) => _value = value;
    public DateTimeOffset Now
    {
        get { lock (_lock) return _value; }
    }
    public void Set(DateTimeOffset value) { lock (_lock) _value = value; }
}

internal sealed class CommitFixtureException : Exception { }

internal sealed class FileWriteRecorder
{
    private readonly object _lock = new();
    private readonly List<byte> _bytes = new();

    public byte[] ReceivedBytes
    {
        get { lock (_lock) return _bytes.ToArray(); }
    }

    public BrokerFileWritePayload Handle(BrokerFileWriteRequest request)
    {
        lock (_lock)
        {
            switch (request.RequestKind)
            {
                case BrokerFileWriteRequest.Kind.BeginComponent:
                case BrokerFileWriteRequest.Kind.FreezeComponent:
                case BrokerFileWriteRequest.Kind.CancelUpload:
                    throw new BrokerFileWriteException(BrokerFileWriteError.InvalidRequest);
                case BrokerFileWriteRequest.Kind.Begin:
                    var begin = request.BeginRequest!;
                    if (begin.OperationId != "write-operation"
                        || begin.CredentialId != "credential-id"
                        || begin.TargetId != "credential-id"
                        || begin.Operation != BrokerApprovalOperation.Modify
                        || begin.OriginalFilename != "AuthKey.p8"
                        || begin.ExpectedByteCount != 17)
                    {
                        throw new BrokerFileWriteException(BrokerFileWriteError.InvalidRequest);
                    }
                    return BrokerFileWritePayload.Upload(new BrokerFileUploadTicket("upload-id", "upload-capability"));
                case BrokerFileWriteRequest.Kind.Append:
                    var append = request.ChunkRequest!;
                    if (append.UploadId != "upload-id" || append.Capability != "upload-capability"
                        || append.Offset != _bytes.Count)
                    {
                        throw new BrokerFileWriteException(BrokerFileWriteError.InvalidRequest);
                    }
                    _bytes.AddRange(append.Bytes);
                    return BrokerFileWritePayload.ChunkAccepted(_bytes.Count);
                case BrokerFileWriteRequest.Kind.Freeze:
                    var freeze = request.FreezeRequest!;
                    if (freeze.UploadId != "upload-id" || freeze.Capability != "upload-capability"
                        || !ReceivedBytes.AsSpan().SequenceEqual("file-write-secret"u8))
                    {
                        throw new BrokerFileWriteException(BrokerFileWriteError.InvalidRequest);
                    }
                    return BrokerFileWritePayload.Approval(new BrokerApprovalTicket(
                        "approval-id", "approval-capability", BrokerRequestState.Pending, 0));
                default:
                    throw new BrokerFileWriteException(BrokerFileWriteError.InvalidRequest);
            }
        }
    }
}
