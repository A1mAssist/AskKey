namespace AskKey.Broker.Tests;

public sealed class ApprovalAwayExpirationTests
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
    public void WakeRefreshExpiresPausedTimerRequestsAndUpdatesObservers()
    {
        var clock = new TestClock(DateTimeOffset.FromUnixTimeSeconds(2_000_000_000));
        var counts = new NotificationRecorder();
        var machine = new BrokerApprovalStateMachine(
            clock: () => clock.Now,
            pendingCountChanged: counts.Record);
        machine.SuspendExpirationTimer();
        try
        {
            var ticket = machine.Submit(Fixture.MakeRequest(operationId: "away"));
            machine.FlushObservers();
            Assert.Equal(new[] { 1 }, counts.Counts);
            clock.Advance(TimeSpan.FromSeconds(301));
            machine.FlushObservers();
            Assert.Equal(new[] { 1 }, counts.Counts);
            machine.RefreshExpiration();
            machine.FlushObservers();
            Assert.Equal(new[] { 1, 0 }, counts.Counts);
            Assert.Empty(machine.PendingRequests());
            Assert.Equal(BrokerRequestState.Expired, machine.Status(ticket.RequestId, ticket.Capability));
            Assert.Throws<BrokerApprovalException>(
                () => machine.Decide(ticket.RequestId, ticket.Capability, BrokerApprovalDecision.Once));
        }
        finally
        {
            machine.ResumeExpirationTimer();
        }
    }

    [Fact]
    public void ResumeBeforeDeadlineDoesNotExtendOrEndRequest()
    {
        var clock = new TestClock(DateTimeOffset.FromUnixTimeSeconds(2_000_000_000));
        var machine = new BrokerApprovalStateMachine(clock: () => clock.Now);
        var ticket = machine.Submit(Fixture.MakeRequest(operationId: "brief-away"));
        clock.Advance(TimeSpan.FromSeconds(30));
        machine.RefreshExpiration();
        Assert.Equal(BrokerRequestState.Pending, machine.Status(ticket.RequestId, ticket.Capability));
        clock.Advance(TimeSpan.FromSeconds(270));
        machine.RefreshExpiration();
        Assert.Equal(BrokerRequestState.Expired, machine.Status(ticket.RequestId, ticket.Capability));
    }

    [Fact]
    public void ResumeRearmsAConsumedTimerWithoutARefresh()
    {
        var counts = new NotificationRecorder();
        using var machine = new BrokerApprovalStateMachine(
            requestTtl: TimeSpan.FromMilliseconds(80),
            clock: () => DateTimeOffset.UtcNow,
            pendingCountChanged: counts.Record);
        machine.SuspendExpirationTimer();
        var ticket = machine.Submit(Fixture.MakeRequest(operationId: "resume-arm"));
        Assert.True(SpinWait.SpinUntil(
            () => machine.ScheduledExpirationTaskCount == 0, TimeSpan.FromSeconds(2)));
        machine.ResumeExpirationTimer();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (counts.Counts.Contains(0))
            {
                Assert.Equal(BrokerRequestState.Expired, machine.Status(ticket.RequestId, ticket.Capability));
                return;
            }
            Thread.Sleep(20);
        }
        Assert.Contains(0, counts.Counts);
    }
}
