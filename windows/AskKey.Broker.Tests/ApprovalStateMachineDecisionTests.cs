namespace AskKey.Broker.Tests;

public sealed class ApprovalStateMachineDecisionTests
{
    // See ApprovalStateMachineQueueTests for why this holder exists.
    private sealed class Fixture : ApprovalStateMachineTestCase
    {
        public static BrokerApprovalOperationRequest MakeRequest(
            string operationId,
            string credentialId = "credential-1",
            BrokerApprovalOperation operation = BrokerApprovalOperation.Read,
            string? digest = null)
            => Request(operationId, credentialId, operation, digest);
    }

    [Fact]
    public void PendingRequestsExposeConfirmationContextWithoutCredentialValues()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var machine = new BrokerApprovalStateMachine(clock: () => now);
        var request = new BrokerApprovalOperationRequest(
            operationId: new string('a', 64),
            credentialId: "credential-id",
            targetId: "credential-id",
            operation: BrokerApprovalOperation.Read,
            payloadDigest: new string('b', 64),
            callerName: "Codex",
            callerPurpose: "Deploy the release");
        var ticket = machine.Submit(request, BrokerCredentialDeadline.None);

        Assert.Equal(
            new[]
            {
                new BrokerPendingApproval(
                    ticket.RequestId,
                    ticket.Capability,
                    request,
                    now.AddSeconds(5 * 60)),
            },
            machine.PendingRequests());
    }

    [Fact]
    public void AppCanConfigureApprovalAuthenticationBeforeDecision()
    {
        var machine = new BrokerApprovalStateMachine();
        var purposes = new AuthenticationRecorder();
        machine.ConfigureAuthentication(purpose =>
        {
            purposes.Record(purpose);
            return true;
        });
        var request = new BrokerApprovalOperationRequest(
            operationId: new string('c', 64),
            credentialId: "credential-id",
            targetId: "credential-id",
            operation: BrokerApprovalOperation.Read,
            payloadDigest: new string('d', 64));
        var ticket = machine.Submit(request, BrokerCredentialDeadline.None);

        Assert.Equal(
            BrokerRequestState.Approved,
            machine.Decide(ticket.RequestId, ticket.Capability, BrokerApprovalDecision.Once).State);
        Assert.Equal(new[] { BrokerAuthenticationPurpose.ReadApproval }, purposes.Values);
    }

    [Fact]
    public void RetransmissionReturnsTheExistingCapabilityAndRejectsPayloadSwap()
    {
        var machine = new BrokerApprovalStateMachine();
        var request = new BrokerApprovalOperationRequest(
            operationId: "operation-1",
            credentialId: "credential-1",
            targetId: "credential-1",
            operation: BrokerApprovalOperation.Read,
            payloadDigest: new string('a', 64));

        var first = machine.Submit(request);
        var retry = machine.Submit(request);

        Assert.Equal(first.RequestId, retry.RequestId);
        Assert.Equal(first.Capability, retry.Capability);
        Assert.Equal(0, first.RetryCount);
        Assert.Equal(1, retry.RetryCount);
        Assert.Equal(BrokerRequestState.Pending, retry.State);
        var payloadSwap = Assert.Throws<BrokerApprovalException>(
            () => machine.Submit(
                new BrokerApprovalOperationRequest(
                    operationId: "operation-1",
                    credentialId: "credential-1",
                    targetId: "credential-1",
                    operation: BrokerApprovalOperation.Read,
                    payloadDigest: new string('b', 64))));
        Assert.Equal(BrokerApprovalError.PayloadMismatch, payloadSwap.Error);
    }

    [Fact]
    public void DenialHasNoCooldownAndOnceApprovalCanOnlyBeConsumedOnce()
    {
        var machine = new BrokerApprovalStateMachine(authenticate: _ => true);
        var denied = machine.Submit(Fixture.MakeRequest("operation-denied"));
        Assert.Equal(
            BrokerRequestState.Denied,
            machine.Decide(denied.RequestId, denied.Capability, BrokerApprovalDecision.Deny).State);

        var next = machine.Submit(Fixture.MakeRequest("operation-next"));
        Assert.NotEqual(denied.RequestId, next.RequestId);
        Assert.Equal(BrokerRequestState.Pending, next.State);
        Assert.Equal(
            BrokerRequestState.Approved,
            machine.Decide(next.RequestId, next.Capability, BrokerApprovalDecision.Once).State);
        var swappedPayload = Assert.Throws<BrokerApprovalException>(
            () => machine.Consume(
                next.RequestId,
                next.Capability,
                Fixture.MakeRequest("operation-next", digest: new string('b', 64))));
        Assert.Equal(BrokerApprovalError.PayloadMismatch, swappedPayload.Error);
        Assert.Equal(
            BrokerRequestState.Consumed,
            machine.Consume(
                next.RequestId,
                next.Capability,
                Fixture.MakeRequest("operation-next")));
        var reused = Assert.Throws<BrokerApprovalException>(
            () => machine.Consume(
                next.RequestId,
                next.Capability,
                Fixture.MakeRequest("operation-next")));
        Assert.Equal(BrokerApprovalError.AlreadyConsumed, reused.Error);
    }

    [Fact]
    public void PendingRequestExpiresAfterFiveMinutesAndCancellationCannotBeReused()
    {
        var authentications = new AuthenticationRecorder();
        var created = DateTimeOffset.FromUnixTimeSeconds(1_000);
        var machine = new BrokerApprovalStateMachine(
            clock: () => created,
            authenticate: purpose =>
            {
                authentications.Record(purpose);
                return true;
            });
        var expiring = machine.Submit(Fixture.MakeRequest("expiring"), created);

        Assert.Equal(
            BrokerRequestState.Pending,
            machine.Status(expiring.RequestId, expiring.Capability, created.AddSeconds(299)));
        var wrongCapability = Assert.Throws<BrokerApprovalException>(
            () => machine.Status(expiring.RequestId, "wrong-capability", created));
        Assert.Equal(BrokerApprovalError.RequestNotFound, wrongCapability.Error);
        Assert.Equal(
            BrokerRequestState.Expired,
            machine.Status(expiring.RequestId, expiring.Capability, created.AddSeconds(300)));
        var expiredDecision = Assert.Throws<BrokerApprovalException>(
            () => machine.Decide(
                expiring.RequestId,
                expiring.Capability,
                BrokerApprovalDecision.Once,
                created.AddSeconds(300)));
        Assert.Equal(BrokerApprovalError.InvalidDecision, expiredDecision.Error);
        Assert.Empty(authentications.Values);

        var cancelled = machine.Submit(Fixture.MakeRequest("cancelled"), created);
        Assert.Equal(
            BrokerRequestState.Cancelled,
            machine.Cancel(cancelled.RequestId, cancelled.Capability, created));
        var cancelledConsume = Assert.Throws<BrokerApprovalException>(
            () => machine.Consume(
                cancelled.RequestId,
                cancelled.Capability,
                Fixture.MakeRequest("cancelled")));
        Assert.Equal(BrokerApprovalError.InvalidDecision, cancelledConsume.Error);
    }

    [Fact]
    public void TerminalRequestCannotBeDecidedTwice()
    {
        var machine = new BrokerApprovalStateMachine(authenticate: _ => true);
        var ticket = machine.Submit(Fixture.MakeRequest("decide-once"));

        Assert.Equal(
            BrokerRequestState.Denied,
            machine.Decide(ticket.RequestId, ticket.Capability, BrokerApprovalDecision.Deny).State);
        var duplicate = Assert.Throws<BrokerApprovalException>(
            () => machine.Decide(ticket.RequestId, ticket.Capability, BrokerApprovalDecision.Once));

        Assert.Equal(BrokerApprovalError.InvalidDecision, duplicate.Error);
        Assert.Equal(BrokerRequestState.Denied, machine.Status(ticket.RequestId, ticket.Capability));
    }
}
