using System.Text.Json;

namespace AskKey.Broker.Tests;

public sealed class ApprovalStateMachineQueueTests
{
    // ApprovalStateMachineTestCase is internal, so a public xUnit test class cannot
    // derive from it (CS0060) and an internal class is not discovered. This holder
    // keeps the one shared request builder reachable from a public test class.
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
    public void QueueNotifiesOnlyOnEmptyTransitionAndNotificationIsPrivate()
    {
        var notifications = new NotificationRecorder();
        var machine = new BrokerApprovalStateMachine(
            notify: notification => notifications.Record(notification),
            pendingCountChanged: count => notifications.Record(count));
        var first = machine.Submit(
            new BrokerApprovalOperationRequest(
                operationId: "operation-secret",
                credentialId: "credential-secret",
                targetId: "target-secret",
                operation: BrokerApprovalOperation.Create,
                payloadDigest: new string('d', 64),
                callerName: "caller-secret",
                callerPurpose: "purpose-secret"));
        _ = machine.Submit(
            new BrokerApprovalOperationRequest(
                operationId: "operation-secret",
                credentialId: "credential-secret",
                targetId: "target-secret",
                operation: BrokerApprovalOperation.Create,
                payloadDigest: new string('d', 64),
                callerName: "caller-secret",
                callerPurpose: "purpose-secret"));
        var second = machine.Submit(Fixture.MakeRequest("operation-2"));
        machine.FlushObservers();

        Assert.Equal(new[] { 1, 2 }, notifications.Counts);
        Assert.Equal(
            new[] { BrokerPrivacyNotification.ApprovalQueueBecameNonempty },
            notifications.Notifications);
        var encoded = JsonSerializer.Serialize(notifications.Notifications[0]);
        foreach (var privateValue in new[]
                 {
                     "credential-secret",
                     "target-secret",
                     new string('d', 64),
                     "caller-secret",
                     "purpose-secret",
                 })
        {
            Assert.DoesNotContain(privateValue, encoded);
        }
        Assert.Empty(notifications.Notifications[0].Actions);

        _ = machine.Decide(first.RequestId, first.Capability, BrokerApprovalDecision.Deny);
        _ = machine.Decide(second.RequestId, second.Capability, BrokerApprovalDecision.Deny);
        _ = machine.Submit(Fixture.MakeRequest("operation-3"));
        machine.FlushObservers();

        Assert.Equal(new[] { 1, 2, 1, 0, 1 }, notifications.Counts);
        Assert.Equal(
            new[]
            {
                BrokerPrivacyNotification.ApprovalQueueBecameNonempty,
                BrokerPrivacyNotification.ApprovalQueueBecameNonempty,
            },
            notifications.Notifications);
    }

    [Fact]
    public void DeadlineActivelyClearsCountAndRearmsNotificationWithoutAnotherCall()
    {
        var recorder = new ExpirationObserverRecorder();
        var machine = new BrokerApprovalStateMachine(
            requestTtl: TimeSpan.FromSeconds(0.05),
            notify: notification => recorder.Record(notification),
            pendingCountChanged: count => recorder.Record(count));

        _ = machine.Submit(Fixture.MakeRequest("first-expiring"));
        machine.FlushObservers();
        Assert.Equal(new[] { 1 }, recorder.Counts);
        if (!recorder.Expired.Wait(TimeSpan.FromSeconds(1))) Assert.Fail("pending request expires");
        machine.FlushObservers();
        Assert.Equal(new[] { 1, 0 }, recorder.Counts);

        _ = machine.Submit(Fixture.MakeRequest("second-expiring"));
        machine.FlushObservers();
        Assert.Equal(new[] { 1, 0, 1 }, recorder.Counts);
        Assert.Equal(
            new[]
            {
                BrokerPrivacyNotification.ApprovalQueueBecameNonempty,
                BrokerPrivacyNotification.ApprovalQueueBecameNonempty,
            },
            recorder.Notifications);
    }

    [Fact]
    public void ExpirationSchedulingStaysBoundedAcrossCancelAndRetentionEviction()
    {
        var machine = new BrokerApprovalStateMachine();

        for (var index = 0; index < 300; index++)
        {
            var ticket = machine.Submit(Fixture.MakeRequest($"timer-churn-{index}"));
            Assert.Equal(1, machine.ScheduledExpirationTaskCount);
            Assert.Equal(
                BrokerRequestState.Cancelled,
                machine.Cancel(ticket.RequestId, ticket.Capability));
            Assert.Equal(0, machine.ScheduledExpirationTaskCount);
        }
    }

    [Fact]
    public void PauseAndCredentialRevocationCancelRequestsAndTimedAllowances()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(20_000);
        var machine = new BrokerApprovalStateMachine(clock: () => now, authenticate: _ => true);
        var allowed = machine.Submit(Fixture.MakeRequest("allow"), now);
        _ = machine.Decide(
            allowed.RequestId,
            allowed.Capability,
            BrokerApprovalDecision.TimedAllow(),
            now);
        var pending = machine.Submit(Fixture.MakeRequest("pending"), now);

        Assert.Equal(2, machine.CancelPending("credential-1"));
        Assert.Equal(
            BrokerRequestState.Cancelled,
            machine.Status(allowed.RequestId, allowed.Capability, now));
        Assert.Equal(
            BrokerRequestState.Cancelled,
            machine.Status(pending.RequestId, pending.Capability, now));
        Assert.Equal(
            BrokerRequestState.Pending,
            machine.Submit(Fixture.MakeRequest("after-change"), now).State);

        machine.PauseAndCancelAll();
        var paused = Assert.Throws<BrokerApprovalException>(
            () => machine.Submit(Fixture.MakeRequest("while-paused"), now));
        Assert.Equal(BrokerApprovalError.AgentAccessPaused, paused.Error);
        machine.Resume();
        Assert.Equal(
            BrokerRequestState.Pending,
            machine.Submit(Fixture.MakeRequest("after-resume"), now).State);
    }
}
