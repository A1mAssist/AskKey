using AskKey.Broker.Protocol;
using AskKey.Broker.Transport;

namespace AskKey.Broker.Tests;

public sealed class BrokerProtocolMethodTests
{
    [Fact]
    public void VersionedHealthAndUnknownMethodsFailClosed()
    {
        var broker = new BrokerRequestHandler(catalog: _ => Array.Empty<BrokerCatalogItem>(), requestStatus: (_, _) => null);

        Assert.Equal(
            BrokerResponse.Success(BrokerPayload.FromHealth(new BrokerHealth(BrokerProtocolVersion.Current, "ok"))),
            broker.Handle(ProtocolTestSupport.Health()));
        Assert.Equal(
            BrokerResponse.Failure(BrokerErrorCode.UnsupportedVersion),
            broker.Handle(ProtocolTestSupport.Health(BrokerProtocolVersion.Current + 1)));
        Assert.Equal(
            BrokerResponse.Failure(BrokerErrorCode.MethodNotAllowed),
            broker.Handle(new BrokerRequest { Version = BrokerProtocolVersion.Current, Method = "reveal" }));
    }

    [Fact]
    public void CatalogAndCapabilityBoundRequestStatusAreTheOnlyDataMethods()
    {
        var item = new BrokerCatalogItem
        {
            Name = "API Key",
            PayloadKind = BrokerCatalogPayloadKind.Text,
            UsageInstructions = "Use for builds",
            EnvironmentVariable = "API_KEY",
            Expired = false,
        };
        var broker = new BrokerRequestHandler(
            catalog: _ => new[] { item },
            requestStatus: (id, capability) =>
                id == "request-1" && capability == "unguessable" ? BrokerRequestState.Pending : null);

        Assert.Equal(
            BrokerResponse.Success(BrokerPayload.FromCatalog(new[] { item })),
            broker.Handle(new BrokerRequest { Version = 1, Method = "catalog" }));
        Assert.Equal(
            BrokerResponse.Failure(BrokerErrorCode.RequestNotFound),
            broker.Handle(new BrokerRequest
            {
                Version = 1, Method = "request.status", RequestId = "request-1", Capability = "wrong",
            }));
        Assert.Equal(
            BrokerResponse.Success(BrokerPayload.FromRequestStatus(BrokerRequestState.Pending)),
            broker.Handle(new BrokerRequest
            {
                Version = 1, Method = "request.status", RequestId = "request-1", Capability = "unguessable",
            }));

        foreach (var forbidden in new[]
                 {
                     "credential.reveal", "permission.list", "permission.set", "group.list",
                     "access-record.list", "backup", "restore", "erase", "resume",
                     "permanent-delete", "get", "copy", "export", "listProjects",
                 })
        {
            Assert.Equal(
                BrokerResponse.Failure(BrokerErrorCode.MethodNotAllowed),
                broker.Handle(new BrokerRequest { Version = 1, Method = forbidden }));
        }
    }

    [Fact]
    public void PausedCatalogFailsExplicitlyWhileHealthRemainsAvailable()
    {
        var pipe = ProtocolTestSupport.NewPipeName();
        var handler = new BrokerRequestHandler(
            catalog: _ => throw new BrokerProviderException(BrokerProviderError.AgentAccessPaused),
            requestStatus: (_, _) => null);
        using var server = new BrokerSocketServer(pipe, handler);
        server.Start();

        var client = new BrokerSocketClient(pipe);
        Assert.Equal(
            BrokerResponse.Failure(BrokerErrorCode.AgentAccessPaused),
            client.Send(new BrokerRequest { Version = 1, Method = "catalog" }));
        Assert.Equal(
            BrokerResponse.Success(BrokerPayload.FromHealth(new BrokerHealth(1, "ok"))),
            client.Send(ProtocolTestSupport.Health()));
    }

    [Fact]
    public void HealthRoundTripsOverLengthPrefixedNamedPipe()
    {
        var pipe = ProtocolTestSupport.NewPipeName();
        using var server = new BrokerSocketServer(pipe, new BrokerRequestHandler(
            catalog: _ => Array.Empty<BrokerCatalogItem>(), requestStatus: (_, _) => null));
        server.Start();

        Assert.Equal(
            BrokerResponse.Success(BrokerPayload.FromHealth(new BrokerHealth(BrokerProtocolVersion.Current, "ok"))),
            new BrokerSocketClient(pipe).Send(ProtocolTestSupport.Health()));
    }

    [Fact]
    public void RequestRegistryBindsStatusAndCancellationToCapabilityAndCapacity()
    {
        var registry = new BrokerRequestRegistry();
        registry.Register("request-1", "unguessable");
        var handler = new BrokerRequestHandler(
            catalog: _ => Array.Empty<BrokerCatalogItem>(),
            requestStatus: registry.Status,
            cancelRequest: registry.Cancel);

        Assert.Equal(
            BrokerResponse.Failure(BrokerErrorCode.RequestNotFound),
            handler.Handle(new BrokerRequest
            {
                Version = 1, Method = "request.cancel", RequestId = "request-1", Capability = "wrong",
            }));
        Assert.Equal(
            BrokerResponse.Success(BrokerPayload.FromRequestStatus(BrokerRequestState.Cancelled)),
            handler.Handle(new BrokerRequest
            {
                Version = 1, Method = "request.cancel", RequestId = "request-1", Capability = "unguessable",
            }));
        Assert.False(registry.SetState("request-1", "unguessable", BrokerRequestState.Completed));
        Assert.Equal(BrokerRequestState.Cancelled, registry.Status("request-1", "unguessable"));

        for (var index = 1; index <= BrokerLimits.MaximumPendingApprovalRequests; index++)
        {
            registry.Register($"pending-{index}", $"capability-{index}");
        }
        var overflow = Assert.Throws<BrokerRequestRegistryException>(
            () => registry.Register("overflow", "overflow"));
        Assert.Equal(BrokerRequestRegistryError.CapacityReached, overflow.Error);

        for (var index = 1; index <= BrokerLimits.MaximumPendingApprovalRequests; index++)
        {
            Assert.True(registry.SetState($"pending-{index}", $"capability-{index}", BrokerRequestState.Denied));
        }
        Assert.Equal(BrokerRequestState.Denied, registry.Status("pending-1", "capability-1"));
        registry.Register("replacement", "replacement");

        for (var index = 1; index <= BrokerLimits.MaximumRetainedRequestStates; index++)
        {
            var id = $"retained-{index}";
            registry.Register(id, id);
            Assert.True(registry.SetState(id, id, BrokerRequestState.Completed));
        }
        Assert.Null(registry.Status("request-1", "unguessable"));
    }

    [Fact]
    public void RequestRegistryCancelsEveryPendingRequestWithoutChangingTerminalState()
    {
        var registry = new BrokerRequestRegistry();
        registry.Register("pending-1", "cap-1");
        registry.Register("pending-2", "cap-2");
        registry.Register("completed", "cap-3");
        Assert.True(registry.SetState("completed", "cap-3", BrokerRequestState.Completed));

        Assert.Equal(2, registry.CancelAllPending());
        Assert.Equal(BrokerRequestState.Cancelled, registry.Status("pending-1", "cap-1"));
        Assert.Equal(BrokerRequestState.Cancelled, registry.Status("pending-2", "cap-2"));
        Assert.Equal(BrokerRequestState.Completed, registry.Status("completed", "cap-3"));
        Assert.Equal(0, registry.CancelAllPending());
    }
}
