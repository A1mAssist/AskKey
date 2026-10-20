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

    /// <summary>Sends one raw big-endian length-prefixed frame and reads the reply.</summary>
    private static BrokerResponse SendRaw(Stream connection, string frame)
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(frame);
        var header = new byte[4];
        header[0] = (byte)((uint)payload.Length >> 24);
        header[1] = (byte)((uint)payload.Length >> 16);
        header[2] = (byte)((uint)payload.Length >> 8);
        header[3] = (byte)payload.Length;
        connection.Write(header);
        connection.Write(payload);
        connection.Flush();
        return BrokerJson.DecodeResponse(BrokerFraming
            .ReadFrameAsync(connection, BrokerLimits.MaximumResponseBytes, TimeSpan.FromSeconds(5))
            .GetAwaiter().GetResult().Data!)!;
    }

    [Fact]
    public void MalformedTypedFramesDoNotStrandRequestSlots()
    {
        var pipe = ProtocolTestSupport.NewPipeName();
        using var server = new BrokerSocketServer(pipe, new BrokerRequestHandler(
            catalog: _ => Array.Empty<BrokerCatalogItem>(), requestStatus: (_, _) => null));
        server.Start();

        // A syntactically valid frame with a wrong JSON type used to throw out of the
        // decoder and strand a concurrency slot, so every later request from a
        // legitimate client was refused with resource_exhausted.
        string[] malformed =
        {
            """{"version":1,"method":"health","requestID":123}""",
            """{"version":1,"method":true,"requestID":"a"}""",
            """{"version":1,"method":"health","textRun":[]}""",
        };

        // More bad frames than the whole slot budget. A leaked slot shows up as
        // resource_exhausted partway through this loop.
        using (var connection = ConnectRaw(pipe))
        {
            for (var index = 0; index < BrokerLimits.MaximumConcurrentRequests + 4; index++)
            {
                var frame = malformed[index % malformed.Length];
                Assert.Equal(BrokerResponse.Failure(BrokerErrorCode.InvalidRequest), SendRaw(connection, frame));
            }
        }

        // The server still answers a well-formed request.
        Assert.Equal(BrokerResponse.Kind.Success, ProtocolTestSupport.Client(pipe).Send(
            new BrokerRequest { Version = 1, Method = "catalog" }).ResponseKind);
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
            ProtocolTestSupport.Client(pipe).Send(new BrokerRequest { Version = 1, Method = "catalog" }));
    }
}
