using AskKey.Broker.Protocol;
using AskKey.Broker.Transport;

namespace AskKey.Broker.Tests;

public sealed class BrokerProtocolFrameTests
{
    private static System.IO.Pipes.NamedPipeClientStream ConnectRaw(string pipeName)
    {
        var pipe = new System.IO.Pipes.NamedPipeClientStream(
            ".", pipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
        pipe.Connect(2000);
        return pipe;
    }

    private static BrokerResponse ReadResponse(Stream stream)
        => BrokerJson.DecodeResponse(BrokerFraming
            .ReadFrameAsync(stream, BrokerLimits.MaximumResponseBytes, TimeSpan.FromSeconds(5))
            .GetAwaiter().GetResult().Data!)!;

    private static void WriteHeader(Stream stream, int length)
    {
        var header = new byte[4];
        header[0] = (byte)((uint)length >> 24);
        header[1] = (byte)((uint)length >> 16);
        header[2] = (byte)((uint)length >> 8);
        header[3] = (byte)length;
        stream.Write(header);
        stream.Flush();
    }

    [Fact]
    public void OversizedTruncatedAndSlowFramesFailClosed()
    {
        var pipe = ProtocolTestSupport.NewPipeName();
        using var server = new BrokerSocketServer(pipe, new BrokerRequestHandler(
            catalog: _ => Array.Empty<BrokerCatalogItem>(), requestStatus: (_, _) => null));
        server.Start();

        using (var oversized = ConnectRaw(pipe))
        {
            WriteHeader(oversized, BrokerLimits.MaximumFrameBytes + 1);
            Assert.Equal(BrokerResponse.Failure(BrokerErrorCode.ResourceExhausted), ReadResponse(oversized));
        }

        using (var truncated = ConnectRaw(pipe))
        {
            WriteHeader(truncated, 10);
            truncated.Write("{}"u8);
            truncated.Flush();
            // Named pipes have no half-close, so a truncated frame ends as EOF.
            // The server must clear the partial bytes; it cannot answer a closed pipe.
            truncated.Dispose();
        }

        using (var slow = ConnectRaw(pipe))
        {
            slow.WriteByte(0);
            slow.Flush();
            Assert.Equal(BrokerResponse.Failure(BrokerErrorCode.DeadlineExceeded), ReadResponse(slow));
        }
    }

    [Fact]
    public void PartialPayloadEOFTimeoutAndReadErrorClearBytesAlreadyRead()
    {
        var pipe = ProtocolTestSupport.NewPipeName();
        var cleared = new List<BrokerClearedReadBuffer>();
        using var server = new BrokerSocketServer(
            pipe,
            runtimeDirectory: null,
            new BrokerRequestHandler(catalog: _ => Array.Empty<BrokerCatalogItem>(), requestStatus: (_, _) => null),
            failedReadBufferCleared: buffer =>
            {
                lock (cleared) cleared.Add(buffer);
            });
        server.Start();
        var partial = "caller-known-partial-value"u8.ToArray();

        using (var eof = ConnectRaw(pipe))
        {
            WriteHeader(eof, partial.Length + 16);
            eof.Write(partial);
            eof.Flush();
            eof.Dispose();
        }
        // Named pipes cannot half-close: the client close ends the connection and
        // the server clears the partial buffer asynchronously, so wait for it.
        var waitUntil = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (true)
        {
            lock (cleared)
            {
                if (cleared.Count > 0) break;
            }
            Assert.True(DateTime.UtcNow < waitUntil, "server never reported the cleared buffer");
            Thread.Sleep(10);
        }
        BrokerClearedReadBuffer first;
        lock (cleared) first = cleared[0];
        Assert.Equal(partial.Length, first.BytesRead);
        Assert.Equal(BrokerClearedReadFailure.EndOfFile, first.Failure);
        Assert.All(first.Buffer, value => Assert.Equal(0, value));

        using (var timeout = ConnectRaw(pipe))
        {
            WriteHeader(timeout, partial.Length + 16);
            timeout.Write(partial);
            timeout.Flush();
            Assert.Equal(BrokerResponse.Failure(BrokerErrorCode.DeadlineExceeded), ReadResponse(timeout));
        }
        BrokerClearedReadBuffer second;
        lock (cleared) second = cleared[1];
        Assert.Equal(partial.Length, second.BytesRead);
        Assert.Equal(BrokerClearedReadFailure.Deadline, second.Failure);
        Assert.All(second.Buffer, value => Assert.Equal(0, value));
    }

    [Fact]
    public void OversizedFieldsAndResponsesFailClosed()
    {
        var oversizedField = new string('x', BrokerLimits.MaximumFieldBytes + 1);
        var handler = new BrokerRequestHandler(
            catalog: _ => Enumerable.Range(0, BrokerLimits.MaximumResponseBytes).Select(index => new BrokerCatalogItem
            {
                Name = $"credential-{index}",
                PayloadKind = BrokerCatalogPayloadKind.Text,
                UsageInstructions = string.Empty,
                Expired = false,
            }).ToArray(),
            requestStatus: (_, _) => null);
        Assert.Equal(
            BrokerResponse.Failure(BrokerErrorCode.InvalidRequest),
            handler.Handle(new BrokerRequest
            {
                Version = 1, Method = "request.status", RequestId = oversizedField, Capability = "c",
            }));

        var pipe = ProtocolTestSupport.NewPipeName();
        using var server = new BrokerSocketServer(pipe, handler);
        server.Start();
        Assert.Equal(
            BrokerResponse.Failure(BrokerErrorCode.ResponseTooLarge),
            new BrokerSocketClient(pipe).Send(new BrokerRequest { Version = 1, Method = "catalog" }));
    }
}

