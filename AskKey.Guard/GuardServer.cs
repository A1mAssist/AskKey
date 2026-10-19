using System.IO.Pipes;
using AskKey.Broker.Transport;

namespace AskKey.Guard;

/// <summary>
/// Fixed-pipe server. The caller identity is resolved once per connection from the
/// client process token; the request body never supplies a SID.
/// </summary>
public sealed class GuardServer : IDisposable
{
    private const int MaximumInstances = 8;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    private readonly string _pipeName;
    private readonly GuardStateStore _store;
    private readonly IGuardCallerVerifier _callerVerifier;
    private readonly GuardNonceStore _nonces;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _acceptLoop;
    private bool _running;
    private bool _disposed;

    public GuardServer(
        GuardStateStore store,
        IGuardCallerVerifier callerVerifier,
        string pipeName = GuardProtocol.PipeName,
        TimeProvider? timeProvider = null)
    {
        if (!BrokerPipeName.IsValid(pipeName)) throw new ArgumentException("pipe name", nameof(pipeName));
        _pipeName = pipeName;
        _store = store;
        _callerVerifier = callerVerifier;
        _nonces = new GuardNonceStore(timeProvider);
    }

    public string PipeName => _pipeName;

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running) throw new InvalidOperationException("server already started");
            _running = true;
        }
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_disposed || !_running) return;
            _running = false;
        }
        _stop.Cancel();
        try { _acceptLoop?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
    }

    public void Dispose()
    {
        Stop();
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (IsRunning)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = GuardPipe.Create(
                    _pipeName, MaximumInstances, GuardAcl.CreateServicePipeSecurity());
            }
            catch (IOException) when (!IsRunning)
            {
                return;
            }
            catch (Exception)
            {
                if (!IsRunning) return;
                await Task.Delay(250).ConfigureAwait(false);
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                pipe.Dispose();
                return;
            }
            catch (IOException)
            {
                pipe.Dispose();
                continue;
            }
            catch (Exception) when (!IsRunning)
            {
                pipe.Dispose();
                return;
            }

            _ = Task.Run(() => ServeAsync(pipe));
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        try
        {
            var caller = GuardProcess.TryGetClientProcessId(pipe, out var processId)
                ? _callerVerifier.Verify(processId)
                : null;
            if (caller is null)
            {
                // Reject before reading so an unverified local process cannot hold
                // one of the fixed pipe instances for the full frame timeout.
                await WriteAsync(pipe, GuardResponse.Failure(GuardErrors.CallerRejected))
                    .ConfigureAwait(false);
                return;
            }

            var frame = await GuardFraming.ReadAsync(pipe, ReadTimeout, _stop.Token)
                .ConfigureAwait(false);
            if (frame.Kind != GuardFrameReadKind.Success || frame.Data is null)
            {
                await WriteAsync(pipe, GuardResponse.Failure(GuardErrors.Protocol))
                    .ConfigureAwait(false);
                return;
            }

            GuardRequest? request;
            try
            {
                request = GuardJson.DecodeRequest(frame.Data);
            }
            finally
            {
                Array.Clear(frame.Data);
            }

            GuardResponse response;
            try
            {
                response = request is null
                    ? GuardResponse.Failure(GuardErrors.Protocol)
                    : Handle(caller, request);
            }
            catch (Exception)
            {
                response = GuardResponse.Failure(GuardErrors.Protocol);
            }
            finally
            {
                ClearRequest(request);
            }
            try
            {
                await WriteAsync(pipe, response).ConfigureAwait(false);
            }
            finally
            {
                if (response.Kek is { } kek) Array.Clear(kek);
            }
        }
        catch (Exception)
        {
            // The connection closed, timed out, or the service is stopping.
        }
        finally
        {
            pipe.Dispose();
        }
    }

    private GuardResponse Handle(GuardCaller caller, GuardRequest request)
    {
        switch (request.Operation)
        {
            case GuardOperations.Status:
                return GuardResponse.Success(enrolled: _store.IsEnrolled(caller.Sid));

            case GuardOperations.Challenge:
                return GuardResponse.Success(nonce: _nonces.Issue(caller.Sid));

            case GuardOperations.Enroll:
                if (request.PublicKey is null || request.Nonce is null
                    || request.Signature is null || request.Proof is null)
                {
                    return GuardResponse.Failure(GuardErrors.Protocol);
                }
                if (request.Proof.Length != 32)
                {
                    return GuardResponse.Failure(GuardErrors.BadSignature);
                }
                if (!GuardSignature.Verify(request.PublicKey, request.Nonce, request.Signature))
                {
                    return GuardResponse.Failure(GuardErrors.BadSignature);
                }
                // Check the replacement proof before consuming the nonce so a bad
                // proof does not burn the caller's challenge.
                if (!_store.CanEnroll(caller.Sid, request.Proof))
                {
                    return GuardResponse.Failure(GuardErrors.BadSignature);
                }
                if (!_nonces.Consume(caller.Sid, request.Nonce))
                {
                    return GuardResponse.Failure(GuardErrors.BadNonce);
                }
                if (!_store.TryEnroll(caller.Sid, request.PublicKey, request.Proof))
                {
                    return GuardResponse.Failure(GuardErrors.BadSignature);
                }
                return GuardResponse.Success(
                    kek: _store.DeriveUserKek(caller.Sid), enrolled: true);

            case GuardOperations.Unlock:
                if (request.Nonce is null || request.Signature is null)
                {
                    return GuardResponse.Failure(GuardErrors.Protocol);
                }
                if (!_store.IsEnrolled(caller.Sid))
                {
                    return GuardResponse.Failure(GuardErrors.NotEnrolled);
                }
                var publicKey = _store.LoadPublicKey(caller.Sid);
                if (publicKey is null)
                {
                    return GuardResponse.Failure(GuardErrors.NotEnrolled);
                }
                if (!GuardSignature.Verify(publicKey, request.Nonce, request.Signature))
                {
                    return GuardResponse.Failure(GuardErrors.BadSignature);
                }
                if (!_nonces.Consume(caller.Sid, request.Nonce))
                {
                    return GuardResponse.Failure(GuardErrors.BadNonce);
                }
                return GuardResponse.Success(kek: _store.DeriveUserKek(caller.Sid));

            case GuardOperations.Unenroll:
                if (request.Proof is null)
                {
                    return GuardResponse.Failure(GuardErrors.Protocol);
                }
                if (!_store.IsEnrolled(caller.Sid))
                {
                    return GuardResponse.Failure(GuardErrors.NotEnrolled);
                }
                if (!_store.MatchesProof(caller.Sid, request.Proof))
                {
                    return GuardResponse.Failure(GuardErrors.BadSignature);
                }
                if (!_store.TryUnenroll(caller.Sid, request.Proof))
                {
                    return GuardResponse.Failure(GuardErrors.BadSignature);
                }
                return GuardResponse.Success(enrolled: false);

            default:
                return GuardResponse.Failure(GuardErrors.Protocol);
        }
    }

    private static void ClearRequest(GuardRequest? request)
    {
        if (request is null) return;
        if (request.Nonce is { } nonce) Array.Clear(nonce);
        if (request.PublicKey is { } publicKey) Array.Clear(publicKey);
        if (request.Signature is { } signature) Array.Clear(signature);
        if (request.Proof is { } proof) Array.Clear(proof);
    }

    private async Task WriteAsync(Stream pipe, GuardResponse response)
    {
        var encoded = GuardJson.EncodeResponse(response);
        try
        {
            if (encoded.Length > GuardProtocol.MaximumFrameBytes)
            {
                Array.Clear(encoded);
                encoded = GuardJson.EncodeResponse(GuardResponse.Failure(GuardErrors.Protocol));
            }
            await GuardFraming.WriteAsync(pipe, encoded, _stop.Token).ConfigureAwait(false);
        }
        finally
        {
            Array.Clear(encoded);
        }
    }

    private bool IsRunning
    {
        get
        {
            lock (_gate) return _running;
        }
    }
}
