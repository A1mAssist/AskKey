using AskKey.Broker.Protocol;
using AskKey.Broker.Runtime;
using AskKey.Broker.Transport;

namespace AskKey.Broker.Tests;

/// <summary>
/// Proves the server-identity gate can actually name a peer: it resolves the server
/// process behind a live pipe, so a failure here means the API is unavailable and the
/// gate would silently reject every released client.
/// </summary>
public sealed class BrokerServerProcessTests
{
    [Fact]
    public void TheServerProcessBehindAPipeIsResolvable()
    {
        var pipe = ProtocolTestSupport.NewPipeName();
        using var server = new BrokerSocketServer(pipe, new BrokerRequestHandler(
            catalog: _ => Array.Empty<BrokerCatalogItem>(), requestStatus: (_, _) => null));
        server.Start();

        using var client = new System.IO.Pipes.NamedPipeClientStream(
            ".", pipe, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
        client.Connect(2000);

        Assert.True(
            BrokerServerIdentity.TryGetServerProcessId(client, out var processId),
            "GetNamedPipeServerProcessId failed");
        Assert.Equal((uint)Environment.ProcessId, processId);
    }

    [Fact]
    public void TheTestHostIsNotAnInstalledHelper()
    {
        // This is what lets the released helper run against a harness-owned pipe.
        Assert.False(BrokerServerIdentity.IsOfficialHelperPath(Environment.ProcessPath!));
    }
}
