using AskKey.Broker.Protocol;
using AskKey.Broker.Transport;

namespace AskKey.Broker.Tests;

public sealed class BrokerProtocolCapacityTests
{
    private static System.IO.Pipes.NamedPipeClientStream ConnectRaw(string pipeName)
    {
        var pipe = new System.IO.Pipes.NamedPipeClientStream(
            ".", pipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
        pipe.Connect(2000);
        return pipe;
    }

    [Fact]
    public void ConnectionLimitRejectsExcessClients()
    {
        var pipe = ProtocolTestSupport.NewPipeName();
        using var server = new BrokerSocketServer(pipe, new BrokerRequestHandler(
            catalog: _ => Array.Empty<BrokerCatalogItem>(), requestStatus: (_, _) => null));
        server.Start();

        var held = new List<Stream>();
        try
        {
            for (var index = 0; index < BrokerLimits.MaximumConnections; index++)
            {
                var connection = ConnectRaw(pipe);
                connection.WriteByte(0);
                connection.Flush();
                held.Add(connection);
            }
            using var excess = ConnectRaw(pipe);
            var response = BrokerJson.DecodeResponse(BrokerFraming
                .ReadFrameAsync(excess, BrokerLimits.MaximumResponseBytes, BrokerLimits.ReadDeadline)
                .GetAwaiter().GetResult().Data!)!;
            Assert.Equal(BrokerResponse.Failure(BrokerErrorCode.ResourceExhausted), response);
        }
        finally
        {
            foreach (var connection in held) connection.Dispose();
        }
    }

    [Fact]
    public void RequestExecutionHasAHardDeadline()
    {
        var pipe = ProtocolTestSupport.NewPipeName();
        using var server = new BrokerSocketServer(pipe, new BrokerRequestHandler(
            catalog: cancellation =>
            {
                cancellation.WaitUntilCancelled();
                throw new BrokerCancellationException();
            },
            requestStatus: (_, _) => null));
        server.Start();

        Assert.Equal(
            BrokerResponse.Failure(BrokerErrorCode.DeadlineExceeded),
            new BrokerSocketClient(pipe).Send(new BrokerRequest { Version = 1, Method = "catalog" }));
    }
}
