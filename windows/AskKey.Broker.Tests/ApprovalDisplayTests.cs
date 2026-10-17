using AskKey.Broker.Approval;

namespace AskKey.Broker.Tests;

/// <summary>Ported 1:1 from Tests/AskKeyBrokerTests/ApprovalDisplayTests.swift.</summary>
public sealed class ApprovalDisplayTests
{
    [Fact]
    public void TestDisplayChangesPreserveEqualityAndConsumptionBinding()
    {
        var original = Request(display: Display("original"));
        foreach (var replacement in new BrokerApprovalOperationRequest.Display?[] { null, Display("replacement") })
        {
            var changed = Request(display: replacement);
            Assert.Equal(original, changed);
            Assert.True(original.MatchesForConsumption(changed));
        }
        var changedBindings = new[]
        {
            Request(operationId: "other"),
            Request(credentialId: "other"),
            Request(targetId: "other"),
            Request(operation: BrokerApprovalOperation.Modify),
            Request(digest: new string('b', 64)),
            Request(retransmissionDigest: new string('c', 64)),
            Request(credentialName: "other"),
            Request(callerName: "other"),
            Request(callerPurpose: "other"),
        };
        foreach (var changed in changedBindings)
        {
            Assert.NotEqual(original, changed);
            Assert.False(original.MatchesForConsumption(changed));
        }
    }

    [Fact]
    public void TestDisplayReplacementReusesTicketAndConsumesOriginalApproval()
    {
        var machine = new BrokerApprovalStateMachine(authenticate: _ => true);
        var original = Request(display: Display("original"));
        var changed = Request(display: Display("replacement"));
        var ticket = machine.Submit(original);
        var retransmitted = machine.Submit(changed);
        Assert.Equal(ticket.RequestId, retransmitted.RequestId);
        Assert.Equal(ticket.Capability, retransmitted.Capability);
        var pending = Assert.Single(machine.PendingRequests());
        Assert.Equal(original.DisplayValue, pending.Request.DisplayValue);
        _ = machine.Decide(ticket.RequestId, ticket.Capability, BrokerApprovalDecision.Once);
        var authorization = machine.ConsumeForRuntime(new[]
        {
            new BrokerApprovalConsumption(ticket.RequestId, ticket.Capability, changed),
        });
        Assert.NotNull(authorization);
        try
        {
            Assert.Null(Record.Exception(() => authorization.Validate()));
        }
        finally
        {
            authorization.Finish();
        }
    }

    [Fact]
    public void TestDisplayCannotOverridePayloadOrRetransmissionDigests()
    {
        var machine = new BrokerApprovalStateMachine(authenticate: _ => true);
        var original = Request(display: Display("original"));
        var ticket = machine.Submit(original);
        _ = machine.Decide(ticket.RequestId, ticket.Capability, BrokerApprovalDecision.Once);
        var changedDigest = new string('b', 64);
        var mismatchedPayload = Assert.Throws<BrokerApprovalException>(
            () => machine.Submit(Request(digest: changedDigest, display: original.DisplayValue)));
        Assert.Equal(BrokerApprovalError.PayloadMismatch, mismatchedPayload.Error);
        var mismatchedRetransmission = Assert.Throws<BrokerApprovalException>(
            () => machine.TerminalRetransmission(original.OperationId, changedDigest));
        Assert.Equal(BrokerApprovalError.PayloadMismatch, mismatchedRetransmission.Error);
    }

    [Fact]
    public void TestCommandSummaryTruncatesInMiddleAndRetainsFullText()
    {
        foreach (var count in new[] { 0, 159, 160 })
        {
            var shortCommand = new string('x', count);
            Assert.Equal(shortCommand, Display(shortCommand).CommandSummary);
        }
        var command = string.Concat(Enumerable.Repeat("🙂", 81)) + "middle" + new string('z', 80);
        var value = Display(command);
        Assert.Equal(command, value.CommandLine);
        Assert.Equal(160, value.CommandSummary.EnumerateRunes().Count());
        // Swift counts grapheme clusters: a combining sequence is one Character and must not split.
        var combining = string.Concat(Enumerable.Repeat("e\u0301", 161));
        var combiningSummary = Display(combining).CommandSummary;
        Assert.Equal(160, System.Globalization.StringInfo.ParseCombiningCharacters(combiningSummary).Length);
        var elements = System.Globalization.StringInfo.GetTextElementEnumerator(combiningSummary);
        var clusterCount = 0;
        while (elements.MoveNext())
        {
            var element = (string)elements.Current;
            if (!element.Contains('\u0301')) continue;
            Assert.Equal("e\u0301", element);
            clusterCount++;
        }
        Assert.Equal(159, clusterCount);
        Assert.Equal(
            string.Concat(command.EnumerateRunes().Take(80))
                + "\u2026"
                + string.Concat(command.EnumerateRunes().Skip(167 - 79)),
            value.CommandSummary);
    }

    [Fact]
    public void TestDisplayContextDoesNotReachPrivacyNotification()
    {
        var recorder = new NotificationRecorder();
        var machine = new BrokerApprovalStateMachine(notify: notification => recorder.Record(notification));
        _ = machine.Submit(Request(display: Display("synthetic-private-argument")));
        machine.FlushObservers();
        Assert.Equal(new[] { BrokerPrivacyNotification.ApprovalQueueBecameNonempty }, recorder.Notifications);
    }

    private static BrokerApprovalOperationRequest.Display Display(string command)
        => new(
            commandLine: command,
            workingDirectory: "~/synthetic",
            executableBasename: "tool",
            environmentVariables: new[] { "TOKEN" },
            temporaryFileVariables: new[] { "KEY_FILE" });

    /// <summary>Swift defaults <c>digest</c> to 64 "a" characters; C# cannot default a non-constant,
    /// so a null argument selects the same default as <c>ApprovalStateMachineTestCase.Request</c>.</summary>
    private static BrokerApprovalOperationRequest Request(
        string operationId = "operation",
        string credentialId = "credential",
        string targetId = "credential",
        BrokerApprovalOperation operation = BrokerApprovalOperation.Read,
        string? digest = null,
        string? retransmissionDigest = null,
        string? credentialName = "Synthetic",
        string? callerName = "caller",
        string? callerPurpose = "purpose",
        BrokerApprovalOperationRequest.Display? display = null)
        => new(
            operationId: operationId,
            credentialId: credentialId,
            targetId: targetId,
            operation: operation,
            payloadDigest: digest ?? new string('a', 64),
            credentialName: credentialName,
            callerName: callerName,
            callerPurpose: callerPurpose,
            retransmissionDigest: retransmissionDigest,
            display: display);
}