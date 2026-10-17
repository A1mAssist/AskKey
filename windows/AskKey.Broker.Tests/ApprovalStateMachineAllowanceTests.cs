namespace AskKey.Broker.Tests;

public sealed class ApprovalStateMachineAllowanceTests
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
    public void LargeDurationIsAcceptedInsteadOfSilentlyDowngraded()
    {
        var start = DateTimeOffset.FromUnixTimeSeconds(10_000);
        var machine = new BrokerApprovalStateMachine(clock: () => start, authenticate: _ => true);
        var ticket = machine.Submit(Fixture.MakeRequest(operationId: "long-allowance"), start);
        var duration = TimeSpan.FromDays(40_000);
        Assert.Equal(
            BrokerRequestState.Approved,
            machine.Decide(ticket.RequestId, ticket.Capability, BrokerApprovalDecision.TimedAllow(duration), start).State);
        Assert.Equal(start + duration, machine.TimedAllowanceDeadline("credential-1"));
    }

    [Fact]
    public void VaultLockRevokesEveryTimedAllowance()
    {
        var machine = new BrokerApprovalStateMachine(authenticate: _ => true);
        var first = machine.Submit(Fixture.MakeRequest(operationId: "first"));
        _ = machine.Decide(
            first.RequestId,
            first.Capability,
            BrokerApprovalDecision.TimedAllow(TimeSpan.FromSeconds(1_800)));

        machine.RevokeAllTimedAllowances();

        Assert.Equal(BrokerRequestState.Pending, machine.Submit(Fixture.MakeRequest(operationId: "after-lock")).State);
    }

    [Fact]
    public void TimedAllowanceIsGlobalForOneCredentialReadAndNeverCoversWrites()
    {
        var authentications = new AuthenticationRecorder();
        var start = DateTimeOffset.FromUnixTimeSeconds(10_000);
        var machine = new BrokerApprovalStateMachine(clock: () => start, authenticate: purpose =>
        {
            authentications.Record(purpose);
            return true;
        });
        var first = machine.Submit(Fixture.MakeRequest(operationId: "first"), start);
        Assert.Equal(
            BrokerRequestState.Approved,
            machine.Decide(
                first.RequestId,
                first.Capability,
                BrokerApprovalDecision.TimedAllow(),
                start).State);

        var sameCredential = machine.Submit(
            Fixture.MakeRequest(operationId: "same-credential"),
            start + TimeSpan.FromSeconds(1_799));
        Assert.Equal(BrokerRequestState.Approved, sameCredential.State);
        Assert.Equal(
            BrokerRequestState.Pending,
            machine.Submit(
                Fixture.MakeRequest(operationId: "other-credential", credentialId: "credential-2"),
                start + TimeSpan.FromSeconds(1)).State);
        var write = machine.Submit(
            Fixture.MakeRequest(operationId: "write", operation: BrokerApprovalOperation.Modify),
            start + TimeSpan.FromSeconds(1));
        Assert.Equal(BrokerRequestState.Pending, write.State);
        var timedAllowError = Assert.Throws<BrokerApprovalException>(() => machine.Decide(
            write.RequestId,
            write.Capability,
            BrokerApprovalDecision.TimedAllow(),
            start + TimeSpan.FromSeconds(1)));
        Assert.Equal(BrokerApprovalError.InvalidDecision, timedAllowError.Error);
        Assert.Equal(
            BrokerRequestState.Approved,
            machine.Decide(
                write.RequestId,
                write.Capability,
                BrokerApprovalDecision.Once,
                start + TimeSpan.FromSeconds(1)).State);
        Assert.Equal(
            new[] { BrokerAuthenticationPurpose.ReadApproval, BrokerAuthenticationPurpose.WriteApproval },
            authentications.Values);

        Assert.True(machine.RevokeTimedAllowance("credential-1"));
        Assert.Equal(
            BrokerRequestState.Cancelled,
            machine.Status(
                sameCredential.RequestId,
                sameCredential.Capability,
                start + TimeSpan.FromSeconds(1_799)));
        var consumeError = Assert.Throws<BrokerApprovalException>(() => machine.Consume(
            sameCredential.RequestId,
            sameCredential.Capability,
            Fixture.MakeRequest(operationId: "same-credential"),
            start + TimeSpan.FromSeconds(1_799)));
        Assert.Equal(BrokerApprovalError.InvalidDecision, consumeError.Error);
        Assert.Equal(
            BrokerRequestState.Pending,
            machine.Submit(
                Fixture.MakeRequest(operationId: "after-revoke"),
                start + TimeSpan.FromSeconds(2)).State);
        var expiryMachine = new BrokerApprovalStateMachine(clock: () => start, authenticate: _ => true);
        var expiryFirst = expiryMachine.Submit(Fixture.MakeRequest(operationId: "expiry-first"), start);
        _ = expiryMachine.Decide(
            expiryFirst.RequestId,
            expiryFirst.Capability,
            BrokerApprovalDecision.TimedAllow(),
            start);
        Assert.Equal(
            BrokerRequestState.Pending,
            expiryMachine.Submit(
                Fixture.MakeRequest(operationId: "after-expiry"),
                start + TimeSpan.FromSeconds(1_800)).State);

        var allowanceClock = new TestClock(start);
        var allowanceMachine = new BrokerApprovalStateMachine(
            clock: () => allowanceClock.Now,
            authenticate: _ => true);
        var allowanceFirst = allowanceMachine.Submit(Fixture.MakeRequest(operationId: "short-window"));
        _ = allowanceMachine.Decide(
            allowanceFirst.RequestId,
            allowanceFirst.Capability,
            BrokerApprovalDecision.TimedAllow(TimeSpan.FromSeconds(60)));
        allowanceClock.Advance(TimeSpan.FromSeconds(59));
        var preapproved = allowanceMachine.Submit(Fixture.MakeRequest(operationId: "preapproved"));
        Assert.Equal(BrokerRequestState.Approved, preapproved.State);
        allowanceClock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(
            BrokerRequestState.Expired,
            allowanceMachine.Status(preapproved.RequestId, preapproved.Capability));
    }

    [Fact]
    public void ExplicitTimedAllowanceShortensTheCurrentRequestDeadline()
    {
        var start = DateTimeOffset.FromUnixTimeSeconds(50_000);
        var clock = new TestClock(start);
        var machine = new BrokerApprovalStateMachine(
            clock: () => clock.Now,
            authenticate: _ => true);
        var operation = Fixture.MakeRequest(operationId: "explicit-short-window");
        var ticket = machine.Submit(operation);
        Assert.Equal(
            BrokerRequestState.Approved,
            machine.Decide(
                ticket.RequestId,
                ticket.Capability,
                BrokerApprovalDecision.TimedAllow(TimeSpan.FromSeconds(60))).State);

        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(
            BrokerRequestState.Expired,
            machine.Status(ticket.RequestId, ticket.Capability));
        var error = Assert.Throws<BrokerApprovalException>(() => machine.Consume(
            ticket.RequestId,
            ticket.Capability,
            operation));
        Assert.Equal(BrokerApprovalError.InvalidDecision, error.Error);
    }

    [Fact]
    public void ReadAuthenticationCanBeDisabledButWriteAuthenticationFailsClosed()
    {
        var authentications = new AuthenticationRecorder();
        var machine = new BrokerApprovalStateMachine(
            readAuthenticationEnabled: false,
            authenticate: purpose =>
            {
                authentications.Record(purpose);
                return false;
            });
        var read = machine.Submit(Fixture.MakeRequest(operationId: "read"));
        Assert.Equal(
            BrokerRequestState.Approved,
            machine.Decide(
                read.RequestId,
                read.Capability,
                BrokerApprovalDecision.Once).State);
        Assert.Empty(authentications.Values);

        var write = machine.Submit(Fixture.MakeRequest(operationId: "write", operation: BrokerApprovalOperation.Delete));
        var error = Assert.Throws<BrokerApprovalException>(() => machine.Decide(
            write.RequestId,
            write.Capability,
            BrokerApprovalDecision.Once));
        Assert.Equal(BrokerApprovalError.AuthenticationFailed, error.Error);
        Assert.Equal(new[] { BrokerAuthenticationPurpose.WriteApproval }, authentications.Values);
        Assert.Equal(
            BrokerRequestState.Pending,
            machine.Status(write.RequestId, write.Capability));
    }
}