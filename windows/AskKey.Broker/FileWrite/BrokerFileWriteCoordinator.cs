using System.Security.Cryptography;
using System.Text;
using AskKey.Broker.Approval;
using AskKey.Broker.Protocol;
using AskKey.Broker.Transport;

namespace AskKey.Broker.FileWrite;

/// <summary>
/// Freezes Agent-supplied bytes before creating an approval request. Staging
/// files contain only authenticated ciphertext; source paths are never accepted.
/// Ported from BrokerFileWriteCoordinator{,+Staging,+Uploads,+Review,+Commit,+Validation}.swift.
/// </summary>
public sealed class BrokerFileWriteCoordinator : IDisposable
{
    public const int MaximumByteCount = 5 * 1024 * 1024;
    public const int MaximumChunkByteCount = 32 * 1024;
    public const int MaximumUploadSessions = BrokerLimits.MaximumPendingApprovalRequests;

    private sealed class Session
    {
        public required string OperationId { get; init; }
        public required bool ComponentOnly { get; init; }
        public required string CredentialId { get; init; }
        public required string TargetId { get; init; }
        public required BrokerApprovalOperation Operation { get; init; }
        public required string OriginalFilename { get; init; }
        public required int ExpectedByteCount { get; init; }
        public required string? PreviousDigest { get; init; }
        public required string Capability { get; init; }
        public required string StagingPath { get; init; }
        public required byte[] Key { get; init; }
        public int ReceivedByteCount { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public BrokerApprovalTicket? Approval { get; set; }
        public string? ContentDigest { get; set; }
        public string? ApprovalDigest { get; set; }
    }

    private sealed class CompletedUpload
    {
        public required string Capability { get; init; }
        public required BrokerApprovalTicket Ticket { get; init; }
    }

    private readonly object _lock = new();
    private readonly string _stagingDirectory;
    private readonly Action _authorizeUpload;
    private readonly BrokerApprovalStateMachine _approvals;
    private readonly Func<bool> _authenticateReveal;
    private readonly Action<BrokerFrozenFile> _commitFrozenFile;
    private readonly Func<string, string, string, bool> _completedFileCommit;
    private readonly Func<string, string?, BrokerApprovalOperationRequest, BrokerApprovalTicket> _submitFrozenApproval;
    private readonly Func<string, string> _normalizeCreateTarget;
    private readonly Func<string, string?> _resolvePreviousDigest;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _uploadTtl;
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CompletedUpload> _completedUploads = new(StringComparer.Ordinal);
    private readonly List<string> _completedUploadOrder = new();
    private bool _disposed;

    public BrokerFileWriteCoordinator(
        string stagingDirectory,
        BrokerApprovalStateMachine approvals,
        Func<bool> authenticateReveal,
        Action<BrokerFrozenFile> commitFrozenFile,
        Func<string, string?, BrokerApprovalOperationRequest, BrokerApprovalTicket> submitFrozenApproval,
        Func<string, string> normalizeCreateTarget,
        Func<string, string?>? resolvePreviousDigest = null,
        TimeSpan? uploadTtl = null,
        Action? authorizeUpload = null,
        Func<DateTimeOffset>? clock = null,
        Func<string, string, string, bool>? completedFileCommit = null)
    {
        var ttl = uploadTtl ?? TimeSpan.FromMinutes(5);
        if (ttl <= TimeSpan.Zero) throw new BrokerFileWriteException(BrokerFileWriteError.InvalidRequest);
        _stagingDirectory = stagingDirectory;
        _authorizeUpload = authorizeUpload ?? (() => { });
        _approvals = approvals;
        _authenticateReveal = authenticateReveal;
        _commitFrozenFile = commitFrozenFile;
        _completedFileCommit = completedFileCommit ?? ((_, _, _) => false);
        _submitFrozenApproval = submitFrozenApproval;
        _normalizeCreateTarget = normalizeCreateTarget;
        _resolvePreviousDigest = resolvePreviousDigest ?? (_ => null);
        _uploadTtl = ttl;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);

        PrepareStagingDirectory(stagingDirectory);
        try
        {
            foreach (var residue in Directory.EnumerateFileSystemEntries(stagingDirectory))
            {
                if (Path.GetFileName(residue).StartsWith("upload-", StringComparison.Ordinal))
                {
                    File.Delete(residue);
                }
            }
        }
        catch (BrokerFileWriteException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new BrokerFileWriteException(BrokerFileWriteError.StagingFailed);
        }
    }

    /// <summary>0700-equivalent directory; a file at the path is a typed failure, never a fallback.</summary>
    private static void PrepareStagingDirectory(string path)
    {
        if (File.Exists(path)) throw new BrokerFileWriteException(BrokerFileWriteError.StagingNotADirectory);
        try
        {
            Directory.CreateDirectory(path);
            BrokerAcl.SecureDirectory(path);
        }
        catch (UnauthorizedAccessException)
        {
            throw new BrokerFileWriteException(BrokerFileWriteError.StagingPermissionDenied);
        }
        catch (IOException error) when (error.Message.Contains("denied", StringComparison.OrdinalIgnoreCase))
        {
            throw new BrokerFileWriteException(BrokerFileWriteError.StagingPermissionDenied);
        }
        catch (BrokerFileWriteException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new BrokerFileWriteException(BrokerFileWriteError.StagingFailed);
        }
    }

    public void Dispose()
    {
        string[] paths;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            paths = _sessions.Values.Select(session => session.StagingPath).ToArray();
            _sessions.Clear();
        }
        foreach (var path in paths)
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    public BrokerFileUploadTicket Begin(
        string operationId,
        string credentialId,
        string targetId,
        BrokerApprovalOperation operation,
        string originalFilename,
        int expectedByteCount,
        bool componentOnly = false)
    {
        _authorizeUpload();
        var boundTargetId = operation == BrokerApprovalOperation.Create
            ? _normalizeCreateTarget(targetId)
            : targetId;
        if (!ValidField(operationId) || !ValidField(credentialId)
            || !ValidField(boundTargetId) || !ValidFilename(originalFilename)
            || (operation != BrokerApprovalOperation.Create && operation != BrokerApprovalOperation.Modify)
            || (operation != BrokerApprovalOperation.Create && boundTargetId != credentialId)
            || expectedByteCount < 0)
        {
            throw new BrokerFileWriteException(BrokerFileWriteError.InvalidRequest);
        }
        if (expectedByteCount > MaximumByteCount)
        {
            throw new BrokerFileWriteException(BrokerFileWriteError.TooLarge);
        }
        var uploadId = Guid.NewGuid().ToString().ToUpperInvariant();
        var capability = Guid.NewGuid().ToString().ToUpperInvariant();
        var stagingPath = Path.Combine(_stagingDirectory, "upload-" + Guid.NewGuid().ToString().ToUpperInvariant());
        var key = RandomNumberGenerator.GetBytes(32);
        var previousDigest = operation == BrokerApprovalOperation.Modify
            ? _resolvePreviousDigest(credentialId)
            : null;
        if (previousDigest is not null && !ValidDigest(previousDigest))
        {
            throw new BrokerFileWriteException(BrokerFileWriteError.InvalidRequest);
        }
        WriteEncrypted(Array.Empty<byte>(), key, stagingPath);
        var session = new Session
        {
            OperationId = operationId,
            ComponentOnly = componentOnly,
            CredentialId = credentialId,
            TargetId = boundTargetId,
            Operation = operation,
            OriginalFilename = originalFilename,
            ExpectedByteCount = expectedByteCount,
            PreviousDigest = previousDigest,
            Capability = capability,
            StagingPath = stagingPath,
            Key = key,
            ReceivedByteCount = 0,
            ExpiresAt = _clock() + _uploadTtl,
        };
        var admitted = WithLock(() =>
        {
            PurgeExpiredLocked(_clock());
            if (_sessions.Count >= MaximumUploadSessions) return false;
            _sessions[uploadId] = session;
            return true;
        });
        if (!admitted)
        {
            try { File.Delete(stagingPath); } catch (IOException) { }
            throw new BrokerFileWriteException(BrokerFileWriteError.CapacityReached);
        }
        return new BrokerFileUploadTicket(uploadId, capability);
    }

    public void Append(string uploadId, string capability, long offset, byte[] bytes)
    {
        _authorizeUpload();
        WithLock(() =>
        {
            var now = _clock();
            PurgeExpiredLocked(now);
            if (!_sessions.TryGetValue(uploadId, out var session) || session.Capability != capability)
            {
                throw new BrokerFileWriteException(BrokerFileWriteError.InvalidCapability);
            }
            if (session.Approval is not null || session.ContentDigest is not null)
            {
                throw new BrokerFileWriteException(BrokerFileWriteError.AlreadyFrozen);
            }
            if (offset != session.ReceivedByteCount)
            {
                throw new BrokerFileWriteException(BrokerFileWriteError.OutOfOrderChunk);
            }
            if (bytes.Length == 0)
            {
                throw new BrokerFileWriteException(BrokerFileWriteError.InvalidRequest);
            }
            if (bytes.Length > MaximumChunkByteCount
                || session.ReceivedByteCount > session.ExpectedByteCount - bytes.Length)
            {
                throw new BrokerFileWriteException(BrokerFileWriteError.TooLarge);
            }
            var staged = ReadEncrypted(session);
            var combined = new byte[staged.Length + bytes.Length];
            staged.CopyTo(combined, 0);
            bytes.CopyTo(combined, staged.Length);
            WriteEncrypted(combined, session.Key, session.StagingPath);
            session.ReceivedByteCount = combined.Length;
            session.ExpiresAt = now + _uploadTtl;
            return true;
        });
    }

    public BrokerApprovalTicket Freeze(string uploadId, string capability)
    {
        _authorizeUpload();
        return WithLock(() =>
        {
            var now = _clock();
            if (_completedUploads.TryGetValue(uploadId, out var completed))
            {
                if (!ConstantTimeEqual(completed.Capability, capability))
                {
                    throw new BrokerFileWriteException(BrokerFileWriteError.InvalidCapability);
                }
                return completed.Ticket;
            }
            PurgeExpiredLocked(now);
            if (!_sessions.TryGetValue(uploadId, out var session) || session.Capability != capability)
            {
                throw new BrokerFileWriteException(BrokerFileWriteError.InvalidCapability);
            }
            if (session.ComponentOnly) throw new BrokerFileWriteException(BrokerFileWriteError.InvalidRequest);
            if (session.Approval is not null && session.ApprovalDigest is not null)
            {
                var retried = _approvals.Submit(
                    ApprovalRequest(session, session.ApprovalDigest), BrokerCredentialDeadline.None);
                session.Approval = retried;
                return retried;
            }
            if (session.ReceivedByteCount != session.ExpectedByteCount)
            {
                throw new BrokerFileWriteException(BrokerFileWriteError.TruncatedUpload);
            }
            var bytes = ReadEncrypted(session);
            if (bytes.Length != session.ExpectedByteCount)
            {
                throw new BrokerFileWriteException(BrokerFileWriteError.TruncatedUpload);
            }
            var contentDigest = Digest(bytes);
            var approvalDigest = ApprovalDigest(session, contentDigest);
            var approval = _submitFrozenApproval(
                session.CredentialId,
                session.PreviousDigest,
                ApprovalRequest(session, approvalDigest));
            session.Approval = approval;
            session.ContentDigest = contentDigest;
            session.ApprovalDigest = approvalDigest;
            session.ExpiresAt = now + _uploadTtl;
            return approval;
        });
    }

    public BrokerComponentFileReference FreezeComponent(string uploadId, string capability)
    {
        _authorizeUpload();
        return WithLock(() =>
        {
            PurgeExpiredLocked(_clock());
            if (!_sessions.TryGetValue(uploadId, out var session) || session.Capability != capability
                || !session.ComponentOnly)
            {
                throw new BrokerFileWriteException(BrokerFileWriteError.InvalidCapability);
            }
            if (session.ContentDigest is { } existing)
            {
                return new BrokerComponentFileReference(uploadId, capability, existing);
            }
            if (session.ReceivedByteCount != session.ExpectedByteCount)
            {
                throw new BrokerFileWriteException(BrokerFileWriteError.TruncatedUpload);
            }
            var bytes = ReadEncrypted(session);
            if (bytes.Length != session.ExpectedByteCount)
            {
                throw new BrokerFileWriteException(BrokerFileWriteError.TruncatedUpload);
            }
            var digest = Digest(bytes);
            session.ContentDigest = digest;
            session.ExpiresAt = _clock() + _uploadTtl;
            return new BrokerComponentFileReference(uploadId, capability, digest);
        });
    }

    /// <summary>App-only: resolve before taking the Vault's agent-operation lease so a
    /// legacy file commit holding this coordinator cannot deadlock on that lease.</summary>
    public BrokerFrozenComponentFile ResolveComponent(
        BrokerComponentFileReference reference,
        string operationId)
    {
        _authorizeUpload();
        return WithLock(() =>
        {
            PurgeExpiredLocked(_clock());
            if (!_sessions.TryGetValue(reference.UploadId, out var session) || !session.ComponentOnly
                || session.Capability != reference.Capability
                || session.OperationId != operationId
                || session.ContentDigest is not { } digest
                || !ConstantTimeEqual(digest, reference.Digest))
            {
                throw new BrokerFileWriteException(BrokerFileWriteError.InvalidCapability);
            }
            var bytes = ReadEncrypted(session);
            if (bytes.Length != session.ExpectedByteCount || !ConstantTimeEqual(Digest(bytes), digest))
            {
                throw new BrokerFileWriteException(BrokerFileWriteError.DigestMismatch);
            }
            return new BrokerFrozenComponentFile(session.OriginalFilename, bytes, digest);
        });
    }

    public void CancelUpload(string uploadId, string capability)
    {
        WithLock(() =>
        {
            if (!_sessions.TryGetValue(uploadId, out var session) || session.Capability != capability)
            {
                throw new BrokerFileWriteException(BrokerFileWriteError.InvalidCapability);
            }
            if (session.Approval is { } approval)
            {
                _ = _approvals.Cancel(approval.RequestId, approval.Capability);
            }
            _sessions.Remove(uploadId);
            try
            {
                File.Delete(session.StagingPath);
            }
            catch (IOException)
            {
                throw new BrokerFileWriteException(BrokerFileWriteError.StagingFailed);
            }
            return true;
        });
    }

    public BrokerFileWritePayload Handle(BrokerFileWriteRequest request)
    {
        switch (request.RequestKind)
        {
            case BrokerFileWriteRequest.Kind.BeginComponent:
                var component = request.ComponentBegin!;
                return BrokerFileWritePayload.Upload(Begin(
                    component.OperationId,
                    component.OperationId,
                    "component",
                    BrokerApprovalOperation.Create,
                    component.OriginalFilename,
                    component.ExpectedByteCount,
                    componentOnly: true));
            case BrokerFileWriteRequest.Kind.FreezeComponent:
                var frozen = request.FreezeRequest!;
                return BrokerFileWritePayload.ComponentFrozen(
                    FreezeComponent(frozen.UploadId, frozen.Capability));
            case BrokerFileWriteRequest.Kind.CancelUpload:
                var cancelled = request.FreezeRequest!;
                CancelUpload(cancelled.UploadId, cancelled.Capability);
                return BrokerFileWritePayload.UploadCancelled;
            case BrokerFileWriteRequest.Kind.Begin:
                var begin = request.BeginRequest!;
                return BrokerFileWritePayload.Upload(Begin(
                    begin.OperationId,
                    begin.CredentialId,
                    begin.TargetId,
                    begin.Operation,
                    begin.OriginalFilename,
                    checked((int)begin.ExpectedByteCount)));
            case BrokerFileWriteRequest.Kind.Append:
                var append = request.ChunkRequest!;
                Append(append.UploadId, append.Capability, append.Offset, append.Bytes);
                return BrokerFileWritePayload.ChunkAccepted(append.Offset + append.Bytes.Length);
            case BrokerFileWriteRequest.Kind.Freeze:
                var freeze = request.FreezeRequest!;
                return BrokerFileWritePayload.Approval(Freeze(freeze.UploadId, freeze.Capability));
            default:
                throw new BrokerFileWriteException(BrokerFileWriteError.InvalidRequest);
        }
    }

    public BrokerFileWriteSummary Summary(string requestId)
        => WithLock(() =>
        {
            PurgeExpiredLocked(_clock());
            var session = SessionForRequest(requestId)
                ?? throw new BrokerFileWriteException(BrokerFileWriteError.RequestNotFound);
            if (session.Approval is not { } approval || session.ContentDigest is not { } digest)
            {
                throw new BrokerFileWriteException(BrokerFileWriteError.RequestNotFound);
            }
            var state = _approvals.Status(approval.RequestId, approval.Capability);
            return new BrokerFileWriteSummary
            {
                TargetId = session.TargetId,
                Operation = session.Operation,
                PayloadKind = BrokerCatalogPayloadKind.File,
                OriginalFilename = session.OriginalFilename,
                ByteCount = session.ReceivedByteCount,
                PreviousDigest = session.PreviousDigest,
                Digest = digest,
                PayloadMasked = true,
                State = state,
            };
        });

    public BrokerFrozenFile Reveal(string requestId)
    {
        if (!WithLock(() =>
            {
                PurgeExpiredLocked(_clock());
                return SessionForRequest(requestId) is not null;
            }))
        {
            throw new BrokerFileWriteException(BrokerFileWriteError.RequestNotFound);
        }
        if (!_authenticateReveal()) throw new BrokerFileWriteException(BrokerFileWriteError.AuthenticationFailed);
        return WithLock(() =>
        {
            PurgeExpiredLocked(_clock());
            var session = SessionForRequest(requestId)
                ?? throw new BrokerFileWriteException(BrokerFileWriteError.RequestNotFound);
            if (session.Approval is not { } approval || session.ApprovalDigest is not { } digest)
            {
                throw new BrokerFileWriteException(BrokerFileWriteError.RequestNotFound);
            }
            var state = _approvals.Status(
                approval.RequestId, approval.Capability, ApprovalRequest(session, digest));
            if (state != BrokerRequestState.Pending && state != BrokerRequestState.Approved)
            {
                throw new BrokerFileWriteException(BrokerFileWriteError.RequestNotFound);
            }
            return FrozenFile(session);
        });
    }

    public BrokerApprovalTicket Decide(
        string requestId, string capability, BrokerApprovalDecision decision)
        => _approvals.Decide(requestId, capability, decision);

    public void Commit(string requestId, string capability, string expectedDigest)
        => Commit(requestId, capability, expectedDigest, _commitFrozenFile);

    public void Commit(
        string requestId,
        string capability,
        string expectedDigest,
        Action<BrokerFrozenFile> consume)
        => WithLock(() =>
        {
            // A database receipt survives both staging cleanup and a process
            // restart, including the DB-commit -> approval-consumed crash window.
            if (_completedFileCommit(requestId, capability, expectedDigest))
            {
                if (SessionForRequest(requestId) is { } existing && existing.ApprovalDigest is { } digest
                    && TryStatus(requestId, capability) == BrokerRequestState.Approved)
                {
                    try
                    {
                        _ = _approvals.Consume(
                            requestId, capability, ApprovalRequest(existing, digest));
                    }
                    catch (BrokerApprovalException) { }
                }
                RetainCompletedUploads(requestId);
                return true;
            }
            PurgeExpiredLocked(_clock());
            var session = SessionForRequest(requestId)
                ?? throw new BrokerFileWriteException(BrokerFileWriteError.RequestNotFound);
            if (session.Approval is not { } approval
                || approval.Capability != capability
                || session.ContentDigest is not { } contentDigest
                || session.ApprovalDigest is not { } approvalDigest)
            {
                throw new BrokerFileWriteException(BrokerFileWriteError.RequestNotFound);
            }
            if (!ConstantTimeEqual(contentDigest, expectedDigest))
            {
                throw new BrokerFileWriteException(BrokerFileWriteError.DigestMismatch);
            }
            var currentPreviousDigest = session.Operation == BrokerApprovalOperation.Modify
                ? _resolvePreviousDigest(session.CredentialId)
                : null;
            if (currentPreviousDigest is not null && !ValidDigest(currentPreviousDigest))
            {
                throw new BrokerFileWriteException(BrokerFileWriteError.InvalidRequest);
            }
            if (!EqualDigest(currentPreviousDigest, session.PreviousDigest))
            {
                throw new BrokerFileWriteException(BrokerFileWriteError.TargetChanged);
            }
            var file = FrozenFile(session);
            try
            {
                // File writes are rare and capped; one lock keeps commit, approval
                // consumption, and replay exclusion linearizable.
                _ = _approvals.Consume(
                    requestId,
                    capability,
                    new BrokerApprovalOperationRequest(
                        session.OperationId,
                        session.CredentialId,
                        session.TargetId,
                        session.Operation,
                        approvalDigest),
                    () => consume(file));
            }
            catch (BrokerApprovalException)
            {
                throw;
            }
            catch (Exception)
            {
                throw new BrokerFileWriteException(BrokerFileWriteError.OutcomeUnknown);
            }
            RetainCompletedUploads(requestId);
            return true;
        });

    private BrokerRequestState? TryStatus(string requestId, string capability)
    {
        try
        {
            return _approvals.Status(requestId, capability);
        }
        catch (BrokerApprovalException)
        {
            return null;
        }
    }

    private void RetainCompletedUploads(string requestId)
    {
        var completed = _sessions
            .Where(pair => pair.Value.Approval?.RequestId == requestId)
            .ToArray();
        foreach (var (uploadId, session) in completed)
        {
            if (session.Approval is not { } approval) continue;
            _completedUploads[uploadId] = new CompletedUpload
            {
                Capability = session.Capability,
                Ticket = new BrokerApprovalTicket(
                    approval.RequestId, approval.Capability, BrokerRequestState.Consumed, approval.RetryCount),
            };
            _completedUploadOrder.Add(uploadId);
            _sessions.Remove(uploadId);
            try { File.Delete(session.StagingPath); } catch (IOException) { }
        }
        while (_completedUploadOrder.Count > BrokerLimits.MaximumRetainedRequestStates)
        {
            _completedUploads.Remove(_completedUploadOrder[0]);
            _completedUploadOrder.RemoveAt(0);
        }
    }

    private Session? SessionForRequest(string requestId)
        => _sessions.Values.FirstOrDefault(session => session.Approval?.RequestId == requestId);

    private static BrokerApprovalOperationRequest ApprovalRequest(Session session, string digest)
        => new(
            session.OperationId,
            session.CredentialId,
            session.TargetId,
            session.Operation,
            digest);

    private BrokerFrozenFile FrozenFile(Session session)
    {
        if (session.ContentDigest is not { } digest)
        {
            throw new BrokerFileWriteException(BrokerFileWriteError.RequestNotFound);
        }
        var bytes = ReadEncrypted(session);
        if (!ConstantTimeEqual(digest, Digest(bytes)))
        {
            throw new BrokerFileWriteException(BrokerFileWriteError.DigestMismatch);
        }
        return new BrokerFrozenFile
        {
            OperationId = session.OperationId,
            CredentialId = session.CredentialId,
            TargetId = session.TargetId,
            Operation = session.Operation,
            PreviousDigest = session.PreviousDigest,
            OriginalFilename = session.OriginalFilename,
            Bytes = bytes,
            ByteCount = bytes.Length,
            Digest = digest,
            ApprovalRequestId = session.Approval?.RequestId,
            ApprovalCapability = session.Approval?.Capability,
        };
    }

    private void PurgeExpiredLocked(DateTimeOffset now)
    {
        foreach (var (uploadId, session) in _sessions.ToArray())
        {
            if (session.ExpiresAt > now) continue;
            _sessions.Remove(uploadId);
            try { File.Delete(session.StagingPath); } catch (IOException) { }
        }
    }

    private static byte[] ReadEncrypted(Session session)
    {
        try
        {
            var combined = File.ReadAllBytes(session.StagingPath);
            if (combined.Length < 12 + 16) throw new CryptographicException("short sealed box");
            var nonce = combined.AsSpan(0, 12);
            var cipher = combined.AsSpan(12, combined.Length - 12 - 16);
            var tag = combined.AsSpan(combined.Length - 16, 16);
            var plain = new byte[cipher.Length];
            using var chacha = new ChaCha20Poly1305(session.Key);
            chacha.Decrypt(nonce, cipher, tag, plain);
            return plain;
        }
        catch (BrokerFileWriteException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new BrokerFileWriteException(BrokerFileWriteError.StagingFailed);
        }
    }

    private static void WriteEncrypted(byte[] data, byte[] key, string path)
    {
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var cipher = new byte[data.Length];
            var tag = new byte[16];
            using (var chacha = new ChaCha20Poly1305(key))
            {
                chacha.Encrypt(nonce, data, cipher, tag);
            }
            var combined = new byte[12 + cipher.Length + 16];
            nonce.CopyTo(combined, 0);
            cipher.CopyTo(combined, 12);
            tag.CopyTo(combined, 12 + cipher.Length);
            File.WriteAllBytes(path, combined);
            BrokerAcl.SecureFile(path);
        }
        catch (BrokerFileWriteException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new BrokerFileWriteException(BrokerFileWriteError.StagingFailed);
        }
    }

    internal static bool ValidField(string value)
        => value.Length > 0 && BrokerWire.Utf8Length(value) <= BrokerLimits.MaximumFieldBytes;

    internal static bool ValidFilename(string value)
        => ValidField(value)
            && value != "."
            && value != ".."
            && !value.Contains('/')
            && !value.Contains('\\')
            && !value.Any(char.IsControl);

    internal static string Digest(byte[] data)
        => Convert.ToHexStringLower(SHA256.HashData(data));

    private static string ApprovalDigest(Session session, string contentDigest)
        => ApprovalDigest(
            session.OperationId,
            session.CredentialId,
            session.TargetId,
            session.Operation,
            session.OriginalFilename,
            session.ExpectedByteCount,
            session.PreviousDigest,
            contentDigest);

    internal static string ApprovalDigest(
        string operationId,
        string credentialId,
        string targetId,
        BrokerApprovalOperation operation,
        string originalFilename,
        int byteCount,
        string? previousDigest,
        string contentDigest)
    {
        var fields = new[]
        {
            operationId,
            credentialId,
            targetId,
            BrokerWire.ToWire(operation),
            originalFilename,
            byteCount.ToString(global::System.Globalization.CultureInfo.InvariantCulture),
            previousDigest ?? "none",
            contentDigest,
        };
        var canonical = string.Join("|", fields.Select(text => $"{BrokerWire.Utf8Length(text)}:{text}"));
        return Digest(Encoding.UTF8.GetBytes(canonical));
    }

    internal static bool ConstantTimeEqual(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        if (leftBytes.Length != rightBytes.Length) return false;
        var difference = 0;
        for (var index = 0; index < leftBytes.Length; index++) difference |= leftBytes[index] ^ rightBytes[index];
        return difference == 0;
    }

    internal static bool EqualDigest(string? left, string? right)
    {
        if (left is null && right is null) return true;
        if (left is null || right is null) return false;
        return ConstantTimeEqual(left, right);
    }

    internal static bool ValidDigest(string value)
        => value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private T WithLock<T>(Func<T> body)
    {
        lock (_lock) return body();
    }
}
