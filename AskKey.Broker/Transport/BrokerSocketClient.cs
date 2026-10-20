using System.IO.Pipes;
using AskKey.Broker.Approval;
using AskKey.Broker.Protocol;

namespace AskKey.Broker.Transport;

/// <summary>Named-pipe client. One connection per call, exactly like the Swift socket client.</summary>
public sealed class BrokerSocketClient
{
    private readonly string _pipeName;
    private readonly BrokerServerIdentity _serverIdentity;

    public BrokerSocketClient(string pipeName)
        : this(pipeName, BrokerServerIdentity.Production)
    {
    }

    /// <summary>Test seam: a testhost-owned pipe is not AskKey.exe, so tests inject a looser gate.</summary>
    public BrokerSocketClient(string pipeName, BrokerServerIdentity serverIdentity)
    {
        if (!BrokerPipeName.IsValid(pipeName))
        {
            throw new BrokerSocketException(BrokerSocketError.PathTooLong, detail: "pipe name");
        }
        _pipeName = pipeName;
        _serverIdentity = serverIdentity;
    }

    /// <summary>Resolves the server-written pipe name from the runtime directory.</summary>
    public static BrokerSocketClient FromConfiguration()
        => new(BrokerConfiguration.ResolvePipeName(), BrokerServerIdentity.Production);

    public BrokerResponse Send(BrokerRequest request, TimeSpan? connectTimeout = null)
    {
        var encoded = BrokerJson.Encode(request);
        if (encoded.Length > BrokerLimits.MaximumFrameBytes)
        {
            throw new BrokerSocketException(BrokerSocketError.FrameTooLarge);
        }

        using var pipe = Connect(connectTimeout ?? TimeSpan.FromSeconds(2));
        if (!BrokerFraming.WriteFrameAsync(pipe, encoded).GetAwaiter().GetResult())
        {
            throw new BrokerSocketException(BrokerSocketError.SystemError, detail: "write");
        }
        var frame = BrokerFraming.ReadFrameAsync(
                pipe, BrokerLimits.MaximumResponseBytes, BrokerLimits.ReadDeadline)
            .GetAwaiter().GetResult();
        switch (frame.Result)
        {
            case BrokerFrameRead.Kind.End:
                throw new BrokerSocketException(BrokerSocketError.NoResponse);
            case BrokerFrameRead.Kind.Failure:
                throw new BrokerSocketException(frame.Failure == BrokerFrameFailure.TooLarge
                    ? BrokerSocketError.ResponseTooLarge
                    : BrokerSocketError.NoResponse);
            default:
                return BrokerJson.DecodeResponse(frame.Data!)
                    ?? throw new BrokerSocketException(BrokerSocketError.MalformedResponse);
        }
    }

    /// <summary>
    /// Sends <c>runtime.run</c>. The four stdio/control descriptors are replaced by a
    /// caller-owned four-pipe channel named by a random token in the request.
    /// </summary>
    public BrokerTextRunResult Run(
        BrokerTextRunRequest request,
        BrokerRuntimeChannel channel,
        BrokerCancellation? cancellation = null)
    {
        var brokerRequest = new BrokerRequest
        {
            Version = BrokerProtocolVersion.Current,
            Method = "runtime.run",
            TextRun = request,
            RuntimeChannel = channel.Token,
        };
        var encoded = BrokerJson.Encode(brokerRequest);
        if (encoded.Length > BrokerLimits.MaximumFrameBytes)
        {
            throw new BrokerSocketException(BrokerSocketError.FrameTooLarge);
        }
        using var pipe = Connect(TimeSpan.FromSeconds(2));
        if (!BrokerFraming.WriteFrameAsync(pipe, encoded).GetAwaiter().GetResult())
        {
            throw new BrokerSocketException(BrokerSocketError.SystemError, detail: "write");
        }
        // The caller owns the four pipe servers; accept the Broker connects now that
        // the request frame is on the wire. Accepting does not block the response read.
        channel.ConnectCallerAsync().GetAwaiter().GetResult();
        // runtime.run has no one-second request deadline: the Broker answers only
        // once the child exits. A closed pipe is an unknown outcome, never a retry.
        var frame = BrokerFraming.ReadFrameAsync(pipe, BrokerLimits.MaximumResponseBytes, deadline: null)
            .GetAwaiter().GetResult();
        if (frame.Result != BrokerFrameRead.Kind.Success)
        {
            if (frame.Result == BrokerFrameRead.Kind.Failure && frame.Failure == BrokerFrameFailure.TooLarge)
            {
                throw new BrokerSocketException(BrokerSocketError.ResponseTooLarge);
            }
            return BrokerTextRunResult.OutcomeUnknown;
        }
        var response = BrokerJson.DecodeResponse(frame.Data!)
            ?? throw new BrokerSocketException(BrokerSocketError.MalformedResponse);
        if (response.ResponseKind == BrokerResponse.Kind.Failure)
        {
            throw new BrokerSocketException(BrokerSocketError.BrokerFailure, response.Error);
        }
        if (response.Payload?.PayloadKind != BrokerPayload.Kind.TextRun || response.Payload.TextRun is null)
        {
            throw new BrokerSocketException(BrokerSocketError.MalformedResponse);
        }
        return response.Payload.TextRun;
    }
    private NamedPipeClientStream Connect(TimeSpan timeout)
    {
        var pipe = new NamedPipeClientStream(
            ".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            pipe.Connect((int)Math.Max(1, timeout.TotalMilliseconds));
        }
        catch (TimeoutException)
        {
            pipe.Dispose();
            throw new BrokerSocketException(BrokerSocketError.NotRunning);
        }
        catch (IOException)
        {
            pipe.Dispose();
            throw new BrokerSocketException(BrokerSocketError.NotRunning);
        }

        // A same-user process can squat the pipe name, so confirm the peer really is
        // the installed Ask Key host before any request, let alone a credential, is sent.
        if (!_serverIdentity.IsTrusted(pipe))
        {
            pipe.Dispose();
            throw new BrokerSocketException(BrokerSocketError.UntrustedServer);
        }
        return pipe;
    }
}



