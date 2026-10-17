using AskKey.Broker.Approval;

namespace AskKey.Broker.Tests;

/// <summary>Ported 1:1 from Tests/AskKeyBrokerTests/OrganizationApprovalTests.swift.</summary>
public sealed class OrganizationApprovalTests
{
    [Fact]
    public void TestOrganizationSubjectRequiresSortedUniqueBindingAndCannotMasqueradeAsSingleCredential()
    {
        var invalid = new[]
        {
            Request((IReadOnlyList<string>?)null),
            Request(new[] { "b", "a" }),
            Request(new[] { "a", "a" }),
            Request(new[] { "" }),
            Request(credentialId: "a"),
            Request(targetId: "a"),
            Request(credentialId: "a", targetId: "a", operation: BrokerApprovalOperation.Modify),
        };
        foreach (var candidate in invalid)
        {
            var error = Assert.Throws<BrokerApprovalException>(
                () => new BrokerApprovalStateMachine().Submit(candidate));
            Assert.Equal(BrokerApprovalError.InvalidRequest, error.Error);
        }
        Assert.Equal(BrokerRequestState.Pending, new BrokerApprovalStateMachine().Submit(Request()).State);
        Assert.Equal(
            BrokerRequestState.Pending,
            new BrokerApprovalStateMachine().Submit(Request(Array.Empty<string>())).State);
    }

    [Fact]
    public void TestConsumptionAndEqualityBindAllMembersAndOneTicket()
    {
        var machine = new BrokerApprovalStateMachine(authenticate: _ => true);
        var original = Request();
        var ticket = machine.Submit(original);
        Assert.Single(machine.PendingRequests());
        Assert.NotEqual(original, Request(new[] { "a" }));
        _ = machine.Decide(ticket.RequestId, ticket.Capability, BrokerApprovalDecision.Once);
        var singleMember = Assert.Throws<BrokerApprovalException>(
            () => machine.Consume(ticket.RequestId, ticket.Capability, Request(new[] { "a" })));
        Assert.Equal(BrokerApprovalError.PayloadMismatch, singleMember.Error);
        Assert.Equal(
            BrokerRequestState.Consumed,
            machine.Consume(ticket.RequestId, ticket.Capability, original));
        var repeated = Assert.Throws<BrokerApprovalException>(
            () => machine.Consume(ticket.RequestId, ticket.Capability, original));
        Assert.Equal(BrokerApprovalError.AlreadyConsumed, repeated.Error);
    }

    /// <summary>Swift defaults <c>ids</c> to ["a", "b"]; C# cannot default a list parameter, so the
    /// parameterless-ids overload supplies it while a null argument still means "no organization binding".</summary>
    private static BrokerApprovalOperationRequest Request(
        IReadOnlyList<string>? ids,
        string credentialId = "",
        string targetId = "credential-library",
        BrokerApprovalOperation operation = BrokerApprovalOperation.Organize)
        => new(
            operationId: "batch",
            credentialId: credentialId,
            targetId: targetId,
            operation: operation,
            payloadDigest: new string('a', 64),
            organizationCredentialIds: ids);

    private static BrokerApprovalOperationRequest Request(
        string credentialId = "",
        string targetId = "credential-library",
        BrokerApprovalOperation operation = BrokerApprovalOperation.Organize)
        => Request(new[] { "a", "b" }, credentialId, targetId, operation);
}