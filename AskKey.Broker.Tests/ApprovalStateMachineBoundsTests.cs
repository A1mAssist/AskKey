namespace AskKey.Broker.Tests;

public sealed class ApprovalStateMachineBoundsTests
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
    public void ExternalFieldsAndPendingQueueAreBounded()
    {
        var machine = new BrokerApprovalStateMachine();
        var emptyOperation = Assert.Throws<BrokerApprovalException>(
            () => machine.Submit(Fixture.MakeRequest(string.Empty)));
        Assert.Equal(BrokerApprovalError.InvalidRequest, emptyOperation.Error);
        var wrongModifyTarget = Assert.Throws<BrokerApprovalException>(
            () => machine.Submit(
                new BrokerApprovalOperationRequest(
                    operationId: "wrong-modify-target",
                    credentialId: "credential-1",
                    targetId: "credential-2",
                    operation: BrokerApprovalOperation.Modify,
                    payloadDigest: new string('a', 64))));
        Assert.Equal(BrokerApprovalError.InvalidRequest, wrongModifyTarget.Error);
        var wrongReadTarget = Assert.Throws<BrokerApprovalException>(
            () => machine.Submit(
                new BrokerApprovalOperationRequest(
                    operationId: "wrong-read-target",
                    credentialId: "credential-1",
                    targetId: "credential-2",
                    operation: BrokerApprovalOperation.Read,
                    payloadDigest: new string('a', 64))));
        Assert.Equal(BrokerApprovalError.InvalidRequest, wrongReadTarget.Error);
        var oversized = Assert.Throws<BrokerApprovalException>(
            () => machine.Submit(
                Fixture.MakeRequest(
                    "oversized",
                    digest: new string('x', BrokerLimits.MaximumFieldBytes + 1))));
        Assert.Equal(BrokerApprovalError.InvalidRequest, oversized.Error);

        BrokerApprovalTicket? first = null;
        for (var index = 0; index < BrokerLimits.MaximumPendingApprovalRequests; index++)
        {
            var ticket = machine.Submit(Fixture.MakeRequest($"operation-{index}"));
            first ??= ticket;
        }
        var overflow = Assert.Throws<BrokerApprovalException>(
            () => machine.Submit(Fixture.MakeRequest("overflow")));
        Assert.Equal(BrokerApprovalError.CapacityReached, overflow.Error);
        if (first is null) Assert.Fail("expected the first pending request");
        var firstTicket = first.GetValueOrDefault();
        _ = machine.Decide(firstTicket.RequestId, firstTicket.Capability, BrokerApprovalDecision.Deny);
        Assert.Null(Record.Exception(() => machine.Submit(Fixture.MakeRequest("replacement"))));

        var allowedMachine = new BrokerApprovalStateMachine(authenticate: _ => true);
        var allowance = allowedMachine.Submit(Fixture.MakeRequest("allowance"));
        _ = allowedMachine.Decide(
            allowance.RequestId,
            allowance.Capability,
            BrokerApprovalDecision.TimedAllow());
        for (var index = 0; index < BrokerLimits.MaximumPendingApprovalRequests - 1; index++)
        {
            _ = allowedMachine.Submit(Fixture.MakeRequest($"other-{index}", $"other-{index}"));
        }
        var fullQueue = Assert.Throws<BrokerApprovalException>(
            () => allowedMachine.Submit(Fixture.MakeRequest("allowed-despite-full-queue")));
        Assert.Equal(BrokerApprovalError.CapacityReached, fullQueue.Error);
    }

    [Fact]
    public void ApprovedRequestExpiresAndAuthenticationCannotCrossTheDeadline()
    {
        var clock = new TestClock(DateTimeOffset.FromUnixTimeSeconds(30_000));
        var machine = new BrokerApprovalStateMachine(
            clock: () => clock.Now,
            authenticate: _ =>
            {
                clock.Advance(TimeSpan.FromSeconds(300));
                return true;
            });
        var slowRequest = Fixture.MakeRequest("slow-auth");
        var ticket = machine.Submit(slowRequest, clock.Now);

        var lateDecision = Assert.Throws<BrokerApprovalException>(
            () => machine.Decide(
                ticket.RequestId,
                ticket.Capability,
                BrokerApprovalDecision.Once,
                DateTimeOffset.FromUnixTimeSeconds(30_000)));
        Assert.Equal(BrokerApprovalError.InvalidDecision, lateDecision.Error);
        Assert.Equal(
            BrokerRequestState.Expired,
            machine.Status(ticket.RequestId, ticket.Capability, clock.Now));

        var approvedMachine = new BrokerApprovalStateMachine(clock: () => clock.Now, authenticate: _ => true);
        var approvedRequest = Fixture.MakeRequest("approved-expiry");
        var approved = approvedMachine.Submit(approvedRequest, clock.Now);
        _ = approvedMachine.Decide(
            approved.RequestId,
            approved.Capability,
            BrokerApprovalDecision.Once,
            clock.Now);
        clock.Advance(TimeSpan.FromSeconds(300));
        Assert.Equal(
            BrokerRequestState.Expired,
            approvedMachine.Status(approved.RequestId, approved.Capability, clock.Now));
        var expiredConsume = Assert.Throws<BrokerApprovalException>(
            () => approvedMachine.Consume(
                approved.RequestId,
                approved.Capability,
                approvedRequest,
                clock.Now));
        Assert.Equal(BrokerApprovalError.InvalidDecision, expiredConsume.Error);

        var rollbackClock = new TestClock(DateTimeOffset.FromUnixTimeSeconds(40_000));
        var rollbackMachine = new BrokerApprovalStateMachine(
            clock: () => rollbackClock.Now,
            authenticate: _ => true);
        var rollbackRequest = Fixture.MakeRequest("rollback");
        var rollback = rollbackMachine.Submit(rollbackRequest, rollbackClock.Now);
        _ = rollbackMachine.Decide(
            rollback.RequestId,
            rollback.Capability,
            BrokerApprovalDecision.Once,
            rollbackClock.Now);
        rollbackClock.Advance(TimeSpan.FromSeconds(300));
        var rollbackConsume = Assert.Throws<BrokerApprovalException>(
            () => rollbackMachine.Consume(
                rollback.RequestId,
                rollback.Capability,
                rollbackRequest));
        Assert.Equal(BrokerApprovalError.InvalidDecision, rollbackConsume.Error);
    }
}
