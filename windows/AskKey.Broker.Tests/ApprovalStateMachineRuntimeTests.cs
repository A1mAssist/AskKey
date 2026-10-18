namespace AskKey.Broker.Tests;

public sealed class ApprovalStateMachineRuntimeTests
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
    public void ConfiguringAnObserverPreservesSubscribersAndReplacesOnlyTheConfiguredCallback()
    {
        using var machine = new BrokerApprovalStateMachine();
        var delivered = new List<string>();
        machine.AddOperationStateChanged((_, _, _) => delivered.Add("cleanup"));
        machine.ConfigureOperationStateChanged((_, _, _) => delivered.Add("replaced"));
        machine.AddOperationStateChanged((_, _, _) => delivered.Add("ui"));
        machine.ConfigureOperationStateChanged((_, _, _) => delivered.Add("configured"));

        var request = Fixture.MakeRequest("observer-registration");
        var ticket = machine.Submit(request);
        machine.Cancel(ticket.RequestId, ticket.Capability);

        Assert.Equal(new[] { "cleanup", "ui", "configured" }, delivered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AbandonedAuthorizationCleansEveryResourceEvenAfterOwnerDisposal(bool disposeOwner)
    {
        using var machine = new BrokerApprovalStateMachine(authenticate: _ => true);
        var cleaned = 0;
        var abandoned = AbandonAuthorization(machine, () => Interlocked.Increment(ref cleaned));
        if (disposeOwner) machine.Dispose();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(abandoned.IsAlive);
        Assert.Equal(1, Volatile.Read(ref cleaned));
        GC.KeepAlive(machine);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference AbandonAuthorization(BrokerApprovalStateMachine machine, Action cleanup)
    {
        var request = Fixture.MakeRequest(operationId: "abandoned-runtime");
        var ticket = machine.Submit(request);
        machine.Decide(ticket.RequestId, ticket.Capability, BrokerApprovalDecision.Once);
        var lease = machine.ConsumeForRuntime(new[]
        {
            new BrokerApprovalConsumption(ticket.RequestId, ticket.Capability, request),
        });
        Assert.NotNull(lease);
        lease.RegisterCleanup(() => throw new IOException("Synthetic cleanup failure"));
        lease.RegisterCleanup(cleanup);
        return new WeakReference(lease);
    }

    [Fact]
    public void ConcurrentCountCallbacksCannotPublishAnOlderCountLast()
    {
        var recorder = new OrderedCountRecorder();
        var machine = new BrokerApprovalStateMachine(pendingCountChanged: recorder.Record);
        var firstRequest = Fixture.MakeRequest(operationId: "first-callback");
        var firstFinished = new SemaphoreSlim(0);
        _ = Task.Run(() =>
        {
            try { machine.Submit(firstRequest); }
            catch (Exception) { }
            firstFinished.Release();
        });
        Assert.True(recorder.FirstEntered.Wait(TimeSpan.FromSeconds(2)));

        var secondRequest = Fixture.MakeRequest(operationId: "second-callback");
        var secondFinished = new SemaphoreSlim(0);
        _ = Task.Run(() =>
        {
            try { machine.Submit(secondRequest); }
            catch (Exception) { }
            secondFinished.Release();
        });
        recorder.SecondEntered.Wait(TimeSpan.FromMilliseconds(200));
        recorder.ReleaseFirst.Release();
        Assert.True(firstFinished.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(secondFinished.Wait(TimeSpan.FromSeconds(2)));
        machine.FlushObservers();

        Assert.Equal(new[] { 1, 2 }, recorder.Values);
    }

    [Fact]
    public void RuntimeAuthorizationSurvivesTicketEvictionButNeverRevocationOrRenewal()
    {
        var clock = new TestClock(DateTimeOffset.FromUnixTimeSeconds(10_000));
        var machine = new BrokerApprovalStateMachine(clock: () => clock.Now, authenticate: _ => true);
        var original = Fixture.MakeRequest(operationId: "runtime-original");
        var ticket = machine.Submit(original);
        _ = machine.Decide(
            ticket.RequestId,
            ticket.Capability,
            BrokerApprovalDecision.TimedAllow(TimeSpan.FromSeconds(30)));
        var lease = machine.ConsumeForRuntime(new[]
        {
            new BrokerApprovalConsumption(ticket.RequestId, ticket.Capability, original),
        });
        Assert.NotNull(lease);
        try
        {
            for (var index = 0; index < BrokerLimits.MaximumRetainedRequestStates; index++)
            {
                var other = machine.Submit(Fixture.MakeRequest(operationId: $"evict-{index}", credentialId: "other"));
                _ = machine.Decide(other.RequestId, other.Capability, BrokerApprovalDecision.Deny);
            }
            var statusError = Assert.Throws<BrokerApprovalException>(
                () => machine.Status(ticket.RequestId, ticket.Capability));
            Assert.Equal(BrokerApprovalError.RequestNotFound, statusError.Error);
            Assert.Null(Record.Exception(() => lease.Validate()));
            Assert.True(machine.RevokeTimedAllowance("credential-1"));
            Assert.Throws<BrokerProviderException>(() => lease.Validate());
            var renewed = Fixture.MakeRequest(operationId: "runtime-renewed");
            var renewedTicket = machine.Submit(renewed);
            _ = machine.Decide(
                renewedTicket.RequestId,
                renewedTicket.Capability,
                BrokerApprovalDecision.TimedAllow(TimeSpan.FromSeconds(30)));
            var freshLease = machine.ConsumeForRuntime(new[]
            {
                new BrokerApprovalConsumption(renewedTicket.RequestId, renewedTicket.Capability, renewed),
            });
            Assert.NotNull(freshLease);
            try
            {
                Assert.Null(Record.Exception(() => freshLease.Validate()));
                Assert.Throws<BrokerProviderException>(() => lease.Validate());
            }
            finally
            {
                freshLease.Finish();
            }
        }
        finally
        {
            lease.Finish();
        }
    }

    [Fact]
    public void RuntimeAuthorizationKeepsItsOriginalDeadlineAndCleanupCanReenterStateMachine()
    {
        foreach (var decision in new[]
        {
            BrokerApprovalDecision.Once,
            BrokerApprovalDecision.TimedAllow(TimeSpan.FromSeconds(30)),
        })
        {
            var start = DateTimeOffset.FromUnixTimeSeconds(10_000);
            var clock = new TestClock(start);
            var machine = new BrokerApprovalStateMachine(
                requestTtl: TimeSpan.FromSeconds(120),
                clock: () => clock.Now,
                authenticate: _ => true);
            var original = Fixture.MakeRequest(operationId: "original-deadline");
            var ticket = machine.Submit(original);
            _ = machine.Decide(ticket.RequestId, ticket.Capability, decision);
            var lease = machine.ConsumeForRuntime(new[]
            {
                new BrokerApprovalConsumption(ticket.RequestId, ticket.Capability, original),
            });
            Assert.NotNull(lease);
            try
            {
                var expectedDuration = decision == BrokerApprovalDecision.Once ? 120d : 30d;
                Assert.Equal(start + TimeSpan.FromSeconds(expectedDuration), lease.ExpiresAt);
                var cleaned = new ManualResetEventSlim(false);
                lease.RegisterCleanup(() =>
                {
                    _ = machine.PendingRequests();
                    cleaned.Set();
                });
                clock.Advance(TimeSpan.FromSeconds(expectedDuration));
                var sweepFinished = new ManualResetEventSlim(false);
                _ = Task.Run(() =>
                {
                    _ = machine.TimedAllowanceDeadline("credential-1");
                    sweepFinished.Set();
                });
                Assert.True(cleaned.Wait(TimeSpan.FromSeconds(2)));
                Assert.True(sweepFinished.Wait(TimeSpan.FromSeconds(2)));
                Assert.Throws<BrokerProviderException>(() => lease.Validate());
            }
            finally
            {
                lease.Finish();
            }
        }
    }
}
