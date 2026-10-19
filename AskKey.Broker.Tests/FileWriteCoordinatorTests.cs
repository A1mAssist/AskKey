using System.Text;
using AskKey.Broker.Approval;
using AskKey.Broker.FileWrite;
using AskKey.Broker.Protocol;
using AskKey.Broker.Transport;

namespace AskKey.Broker.Tests;

public sealed class FileWriteCoordinatorStagingTests
{
    [Fact]
    public void StagingPathThatIsAFileIsATypedFailure()
    {
        var directory = ProtocolTestSupport.NewDirectory();
        try
        {
            var staging = Path.Combine(directory, "file-write-staging");
            File.WriteAllText(staging, "not-a-directory");
            var error = Assert.Throws<BrokerFileWriteException>(() => MakeCoordinator(staging));
            Assert.Equal(BrokerFileWriteError.StagingNotADirectory, error.Error);
            Assert.DoesNotContain(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), error.Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void StagingSucceedsAfterATypedDirectoryConditionIsFixed()
    {
        var directory = ProtocolTestSupport.NewDirectory();
        try
        {
            var staging = Path.Combine(directory, "file-write-staging");
            File.WriteAllText(staging, "not-a-directory");
            Assert.Equal(
                BrokerFileWriteError.StagingNotADirectory,
                Assert.Throws<BrokerFileWriteException>(() => MakeCoordinator(staging)).Error);
            File.Delete(staging);
            using var coordinator = MakeCoordinator(staging);
            Assert.True(Directory.Exists(staging));
            Assert.True(BrokerAcl.IsPrivateDirectory(staging));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void OutOfOrderChunkIsRejectedBeforeApprovalExists()
    {
        using var harness = new FileWriteHarness();
        var upload = harness.Begin("upload-operation", 6);
        Assert.Equal(
            BrokerFileWriteError.OutOfOrderChunk,
            Assert.Throws<BrokerFileWriteException>(() =>
                harness.Coordinator.Append(upload.UploadId, upload.Capability, 3, "def"u8.ToArray())).Error);
        Assert.Equal(
            BrokerFileWriteError.TruncatedUpload,
            Assert.Throws<BrokerFileWriteException>(() =>
                harness.Coordinator.Freeze(upload.UploadId, upload.Capability)).Error);
    }

    [Fact]
    public void CapabilityAndSizeLimitsFailBeforeFreeze()
    {
        using var harness = new FileWriteHarness();
        Assert.Equal(
            BrokerFileWriteError.InvalidRequest,
            Assert.Throws<BrokerFileWriteException>(() => harness.Coordinator.Begin(
                "path-input", "credential-id", "credential-id", BrokerApprovalOperation.Modify,
                "/tmp/replaceable.key", 1)).Error);
        Assert.Equal(
            BrokerFileWriteError.TooLarge,
            Assert.Throws<BrokerFileWriteException>(() => harness.Coordinator.Begin(
                "too-large", "credential-id", "credential-id", BrokerApprovalOperation.Modify,
                "key.bin", BrokerFileWriteCoordinator.MaximumByteCount + 1)).Error);

        var upload = harness.Begin("bounded", 3);
        Assert.Equal(
            BrokerFileWriteError.InvalidCapability,
            Assert.Throws<BrokerFileWriteException>(() =>
                harness.Coordinator.Append(upload.UploadId, "wrong-capability", 0, "abc"u8.ToArray())).Error);
        harness.Coordinator.Append(upload.UploadId, upload.Capability, 0, "abc"u8.ToArray());
        Assert.Equal(
            BrokerFileWriteError.TooLarge,
            Assert.Throws<BrokerFileWriteException>(() =>
                harness.Coordinator.Append(upload.UploadId, upload.Capability, 3, "d"u8.ToArray())).Error);
    }

    [Fact]
    public void StagingNeverContainsPlaintext()
    {
        using var harness = new FileWriteHarness();
        var secret = Encoding.UTF8.GetBytes("PRIVATE-KEY-MATERIAL-DO-NOT-LOG");
        var upload = harness.Begin("encrypted", secret.Length);
        harness.Coordinator.Append(upload.UploadId, upload.Capability, 0, secret);
        var staged = Directory.GetFiles(harness.Directory);
        Assert.Single(staged);
        Assert.False(Contains(File.ReadAllBytes(staged[0]), secret));
        _ = harness.Coordinator.Freeze(upload.UploadId, upload.Capability);
        Assert.False(Contains(File.ReadAllBytes(staged[0]), secret));
    }

    [Fact]
    public void UnfrozenUploadSessionsAreBounded()
    {
        using var harness = new FileWriteHarness();
        for (var index = 0; index < BrokerFileWriteCoordinator.MaximumUploadSessions; index++)
        {
            _ = harness.Begin($"pending-{index}", 0);
        }
        Assert.Equal(
            BrokerFileWriteError.CapacityReached,
            Assert.Throws<BrokerFileWriteException>(() => harness.Begin("overflow", 0)).Error);
        Assert.Equal(
            BrokerFileWriteCoordinator.MaximumUploadSessions,
            Directory.GetFiles(harness.Directory).Length);
    }

    [Fact]
    public void ExpiredUploadsReleaseCapacityAndStartupRemovesCiphertextResidue()
    {
        var clock = new LockedClock(DateTimeOffset.FromUnixTimeSeconds(0));
        using var harness = new FileWriteHarness(uploadTtl: TimeSpan.FromSeconds(1), clock: () => clock.Now);
        for (var index = 0; index < BrokerFileWriteCoordinator.MaximumUploadSessions; index++)
        {
            _ = harness.Begin($"expiring-{index}", 0);
        }
        clock.Set(DateTimeOffset.FromUnixTimeSeconds(2));
        _ = harness.Begin("after-expiry", 0);
        Assert.Single(Directory.GetFiles(harness.Directory));

        var residue = Path.Combine(harness.Directory, "upload-orphaned-ciphertext");
        File.WriteAllText(residue, "old encrypted generation");
        using var restarted = MakeCoordinator(harness.Directory);
        Assert.Empty(Directory.GetFiles(harness.Directory));
    }

    private static bool Contains(byte[] haystack, byte[] needle)
        => haystack.AsSpan().IndexOf(needle) >= 0;

    private static BrokerFileWriteCoordinator MakeCoordinator(string staging)
        => new(
            staging,
            new BrokerApprovalStateMachine(authenticate: _ => true),
            authenticateReveal: () => true,
            commitFrozenFile: _ => { },
            submitFrozenApproval: (_, _, request) =>
                new BrokerApprovalStateMachine(authenticate: _ => true)
                    .Submit(request, BrokerCredentialDeadline.None),
            normalizeCreateTarget: target => target);
}

public sealed class FileWriteCoordinatorCommitTests
{
    [Fact]
    public void ApprovalBindsFilenameAsWellAsFileBytes()
    {
        using var harness = new FileWriteHarness();
        var bytes = Encoding.UTF8.GetBytes("same-file-bytes");
        var first = harness.Coordinator.Begin(
            "filename-bound", "credential-id", "credential-id", BrokerApprovalOperation.Modify,
            "first.p8", bytes.Length);
        harness.Coordinator.Append(first.UploadId, first.Capability, 0, bytes);
        _ = harness.Coordinator.Freeze(first.UploadId, first.Capability);

        var swapped = harness.Coordinator.Begin(
            "filename-bound", "credential-id", "credential-id", BrokerApprovalOperation.Modify,
            "second.p8", bytes.Length);
        harness.Coordinator.Append(swapped.UploadId, swapped.Capability, 0, bytes);
        Assert.Equal(
            BrokerApprovalError.PayloadMismatch,
            Assert.Throws<BrokerApprovalException>(() =>
                harness.Coordinator.Freeze(swapped.UploadId, swapped.Capability)).Error);
    }

    [Fact]
    public void TargetChangeBetweenBeginAndFreezeFailsBeforeApproval()
    {
        var target = new LockedDigest(new string('a', 64));
        using var harness = new FileWriteHarness(resolvePreviousDigest: _ => target.Value);
        var bytes = Encoding.UTF8.GetBytes("rotated-file");
        var upload = harness.Begin("target-changed-before-freeze", bytes.Length);
        harness.Coordinator.Append(upload.UploadId, upload.Capability, 0, bytes);
        target.Value = new string('b', 64);
        Assert.Equal(
            BrokerFileWriteError.TargetChanged,
            Assert.Throws<BrokerFileWriteException>(() =>
                harness.Coordinator.Freeze(upload.UploadId, upload.Capability)).Error);
    }

    [Fact]
    public void TargetChangeAfterFreezeFailsBeforeCommitTransaction()
    {
        var originalDigest = new string('a', 64);
        var target = new LockedDigest(originalDigest);
        using var harness = new FileWriteHarness(resolvePreviousDigest: _ => target.Value);
        var bytes = Encoding.UTF8.GetBytes("approved-rotation");
        var upload = harness.Begin("target-changed-before-commit", bytes.Length);
        harness.Coordinator.Append(upload.UploadId, upload.Capability, 0, bytes);
        var approval = harness.Coordinator.Freeze(upload.UploadId, upload.Capability);
        var frozen = harness.Coordinator.Reveal(approval.RequestId);
        Assert.Equal(originalDigest, frozen.PreviousDigest);
        _ = harness.Coordinator.Decide(approval.RequestId, approval.Capability, BrokerApprovalDecision.Once);

        target.Value = new string('b', 64);
        var transactionCalled = false;
        Assert.Equal(
            BrokerFileWriteError.TargetChanged,
            Assert.Throws<BrokerFileWriteException>(() => harness.Coordinator.Commit(
                approval.RequestId, approval.Capability, frozen.Digest,
                _ => transactionCalled = true)).Error);
        Assert.False(transactionCalled);
    }

    [Fact]
    public void PathReplacementCannotChangeFrozenOrCommittedBytes()
    {
        var previousDigest = new string('a', 64);
        using var harness = new FileWriteHarness(resolvePreviousDigest: _ => previousDigest);
        var source = Path.Combine(Path.GetTempPath(), "askkey-source-" + Guid.NewGuid().ToString("N"));
        try
        {
            var original = Encoding.UTF8.GetBytes("original-key-bytes");
            var replacement = Encoding.UTF8.GetBytes("replacement-key!!");
            File.WriteAllBytes(source, original);
            var upload = harness.Begin("path-swap", original.Length);
            harness.Coordinator.Append(upload.UploadId, upload.Capability, 0, File.ReadAllBytes(source));
            File.WriteAllBytes(source, replacement);

            var approval = harness.Coordinator.Freeze(upload.UploadId, upload.Capability);
            var summary = harness.Coordinator.Summary(approval.RequestId);
            Assert.True(summary.PayloadMasked);
            Assert.Equal("credential-id", summary.TargetId);
            Assert.Equal(BrokerApprovalOperation.Modify, summary.Operation);
            Assert.Equal(BrokerCatalogPayloadKind.File, summary.PayloadKind);
            Assert.Equal(previousDigest, summary.PreviousDigest);
            Assert.Equal(original.Length, summary.ByteCount);
            Assert.Equal("8b37c60caf7a515a32a7dc6c10a9470c7dae3df03b719dc686e35feff45a42f5", summary.Digest);
            var revealed = harness.Coordinator.Reveal(approval.RequestId);
            Assert.Equal(original, revealed.Bytes);
            Assert.Equal(summary.Digest, revealed.Digest);

            _ = harness.Coordinator.Decide(approval.RequestId, approval.Capability, BrokerApprovalDecision.Once);
            BrokerFrozenFile? committed = null;
            harness.Coordinator.Commit(
                approval.RequestId, approval.Capability, summary.Digest, file => committed = file);
            Assert.Equal(original, committed?.Bytes);
            Assert.NotEqual(replacement, committed?.Bytes);
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Fact]
    public void DigestSwapAndReplayCannotReuseApproval()
    {
        using var harness = new FileWriteHarness();
        var bytes = Encoding.UTF8.GetBytes("approved-payload");
        var upload = harness.Begin("digest-bound", bytes.Length);
        harness.Coordinator.Append(upload.UploadId, upload.Capability, 0, bytes);
        var approval = harness.Coordinator.Freeze(upload.UploadId, upload.Capability);
        var summary = harness.Coordinator.Summary(approval.RequestId);
        var swapped = Encoding.UTF8.GetBytes("swapped-payload!");
        var retry = harness.Begin("digest-bound", swapped.Length);
        harness.Coordinator.Append(retry.UploadId, retry.Capability, 0, swapped);
        Assert.Equal(
            BrokerApprovalError.PayloadMismatch,
            Assert.Throws<BrokerApprovalException>(() =>
                harness.Coordinator.Freeze(retry.UploadId, retry.Capability)).Error);
        _ = harness.Coordinator.Decide(approval.RequestId, approval.Capability, BrokerApprovalDecision.Once);

        Assert.Equal(
            BrokerFileWriteError.DigestMismatch,
            Assert.Throws<BrokerFileWriteException>(() => harness.Coordinator.Commit(
                approval.RequestId, approval.Capability, new string('0', 64), _ => { })).Error);
        var commitCount = 0;
        Assert.Equal(
            BrokerFileWriteError.OutcomeUnknown,
            Assert.Throws<BrokerFileWriteException>(() => harness.Coordinator.Commit(
                approval.RequestId, approval.Capability, summary.Digest,
                _ => throw new CommitFixtureException())).Error);
        Assert.Equal(
            BrokerRequestState.Approved,
            harness.Approvals.Status(approval.RequestId, approval.Capability));
        harness.Coordinator.Commit(approval.RequestId, approval.Capability, summary.Digest, _ =>
        {
            Assert.Equal(0, harness.Approvals.CancelPending("credential-id"));
            commitCount++;
        });
        Assert.ThrowsAny<Exception>(() => harness.Coordinator.Commit(
            approval.RequestId, approval.Capability, summary.Digest, _ => commitCount++));
        Assert.Equal(1, commitCount);
    }
}

public sealed class FileWriteCoordinatorBrokerTests
{
    [Fact]
    public void ActiveRevealRequiresAuthenticationAndDoesNotApprove()
    {
        var revealAllowed = new LockedFlag(false);
        using var harness = new FileWriteHarness(authenticateReveal: () => revealAllowed.Value);
        var bytes = Encoding.UTF8.GetBytes("inspect-me");
        var upload = harness.Begin("reveal", bytes.Length);
        harness.Coordinator.Append(upload.UploadId, upload.Capability, 0, bytes);
        var approval = harness.Coordinator.Freeze(upload.UploadId, upload.Capability);

        Assert.Equal(
            BrokerFileWriteError.AuthenticationFailed,
            Assert.Throws<BrokerFileWriteException>(() =>
                harness.Coordinator.Reveal(approval.RequestId)).Error);
        revealAllowed.Value = true;
        var revealed = harness.Coordinator.Reveal(approval.RequestId);
        Assert.Equal(bytes, revealed.Bytes);
        Assert.Equal(
            BrokerRequestState.Pending,
            harness.Approvals.Status(approval.RequestId, approval.Capability));
        Assert.ThrowsAny<Exception>(() => harness.Coordinator.Commit(
            approval.RequestId, approval.Capability, revealed.Digest, _ => { }));
    }

    [Fact]
    public void FailedCommitDuringPauseCancelsApprovalInsteadOfAllowingRetry()
    {
        using var harness = new FileWriteHarness();
        var bytes = Encoding.UTF8.GetBytes("pause-race");
        var upload = harness.Begin("pause-race", bytes.Length);
        harness.Coordinator.Append(upload.UploadId, upload.Capability, 0, bytes);
        var approval = harness.Coordinator.Freeze(upload.UploadId, upload.Capability);
        var summary = harness.Coordinator.Summary(approval.RequestId);
        _ = harness.Coordinator.Decide(approval.RequestId, approval.Capability, BrokerApprovalDecision.Once);

        Assert.Throws<BrokerFileWriteException>(() => harness.Coordinator.Commit(
            approval.RequestId, approval.Capability, summary.Digest, _ =>
            {
                harness.Approvals.PauseAndCancelAll();
                throw new CommitFixtureException();
            }));
        Assert.Equal(
            BrokerRequestState.Cancelled,
            harness.Approvals.Status(approval.RequestId, approval.Capability));
        harness.Approvals.Resume();
        Assert.ThrowsAny<Exception>(() => harness.Coordinator.Commit(
            approval.RequestId, approval.Capability, summary.Digest, _ => { }));
    }

    [Fact]
    public void VersionedBrokerExposesOnlyUploadChunkAndFreeze()
    {
        using var harness = new FileWriteHarness();
        var handler = new BrokerRequestHandler(
            catalog: _ => Array.Empty<BrokerCatalogItem>(),
            requestStatus: (_, _) => null,
            fileWrite: harness.Coordinator.Handle);
        var pipe = ProtocolTestSupport.NewPipeName();
        using var server = new BrokerSocketServer(pipe, handler);
        server.Start();
        var client = new BrokerSocketClient(pipe);

        var beginResponse = client.Send(new BrokerRequest
        {
            Version = BrokerProtocolVersion.Current,
            Method = "credential.file.write",
            FileWrite = BrokerFileWriteRequest.Begin(new BrokerFileWriteBeginRequest(
                "protocol-upload", "credential-id", "credential-id",
                BrokerApprovalOperation.Modify, "AuthKey.p8", 3)),
        });
        var upload = Assert.IsType<BrokerFileUploadTicket>(beginResponse.Payload?.FileWrite?.UploadTicket);

        var chunkResponse = client.Send(new BrokerRequest
        {
            Version = BrokerProtocolVersion.Current,
            Method = "credential.file.write",
            FileWrite = BrokerFileWriteRequest.Append(new BrokerFileWriteChunkRequest(
                upload.UploadId, upload.Capability, 0, "abc"u8.ToArray())),
        });
        Assert.Equal(BrokerResponse.Success(BrokerPayload.FromFileWrite(
            BrokerFileWritePayload.ChunkAccepted(3))), chunkResponse);
        Assert.DoesNotContain("abc", Encoding.UTF8.GetString(BrokerJson.Encode(chunkResponse)));

        var secretFailure = "must-not-echo"u8.ToArray();
        var failureResponse = client.Send(new BrokerRequest
        {
            Version = BrokerProtocolVersion.Current,
            Method = "credential.file.write",
            FileWrite = BrokerFileWriteRequest.Append(new BrokerFileWriteChunkRequest(
                upload.UploadId, upload.Capability, 99, secretFailure)),
        });
        Assert.Equal(BrokerResponse.Failure(BrokerErrorCode.InvalidRequest), failureResponse);
        Assert.DoesNotContain("must-not-echo", Encoding.UTF8.GetString(BrokerJson.Encode(failureResponse)));

        var freezeResponse = client.Send(new BrokerRequest
        {
            Version = BrokerProtocolVersion.Current,
            Method = "credential.file.write",
            FileWrite = BrokerFileWriteRequest.Freeze(new BrokerFileWriteFreezeRequest(
                upload.UploadId, upload.Capability)),
        });
        var approval = freezeResponse.Payload?.FileWrite?.ApprovalTicket;
        Assert.NotNull(approval);
        Assert.Equal(BrokerRequestState.Pending, approval.Value.State);

        var retryResponse = client.Send(new BrokerRequest
        {
            Version = BrokerProtocolVersion.Current,
            Method = "credential.file.write",
            FileWrite = BrokerFileWriteRequest.Freeze(new BrokerFileWriteFreezeRequest(
                upload.UploadId, upload.Capability)),
        });
        var retry = retryResponse.Payload?.FileWrite?.ApprovalTicket;
        Assert.NotNull(retry);
        Assert.Equal(approval.Value.RequestId, retry.Value.RequestId);
        Assert.Equal(approval.Value.Capability, retry.Value.Capability);
        Assert.Equal(1, retry.Value.RetryCount);

        foreach (var forbidden in new[]
                 {
                     "credential.file.reveal", "credential.file.approve", "credential.file.commit",
                 })
        {
            Assert.Equal(
                BrokerResponse.Failure(BrokerErrorCode.MethodNotAllowed),
                client.Send(new BrokerRequest { Version = BrokerProtocolVersion.Current, Method = forbidden }));
        }
    }
}
