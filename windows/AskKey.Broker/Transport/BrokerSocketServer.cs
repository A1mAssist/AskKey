using System.IO.Pipes;
using AskKey.Broker.Approval;
using AskKey.Broker.Protocol;

namespace AskKey.Broker.Transport;

/// <summary>
/// Named-pipe server with the Swift socket server's caps and fail-closed framing.
/// One pipe instance per connection; the pipe name carries a random suffix and is
/// published in the runtime directory.
/// </summary>
public sealed class BrokerSocketServer : IDisposable
{
    private readonly string _pipeName;
    private readonly string? _runtimeDirectory;
    private readonly BrokerRequestHandler _handler;
    private readonly Action<BrokerClearedReadBuffer> _failedReadBufferCleared;
    private readonly object _stateLock = new();
    private readonly CancellationTokenSource _stop = new();
    private int _activeConnections;
    private int _activeRequests;
    private bool _running;
    private Task? _acceptLoop;

    public BrokerSocketServer(string pipeName, BrokerRequestHandler handler)
        : this(pipeName, runtimeDirectory: null, handler, _ => { })
    {
    }

    public BrokerSocketServer(
        string pipeName,
        string? runtimeDirectory,
        BrokerRequestHandler handler,
        Action<BrokerClearedReadBuffer> failedReadBufferCleared)
    {
        if (!BrokerPipeName.IsValid(pipeName))
        {
            throw new BrokerSocketException(BrokerSocketError.PathTooLong, detail: "pipe name");
        }
        _pipeName = pipeName;
        _runtimeDirectory = runtimeDirectory;
        _handler = handler;
        _failedReadBufferCleared = failedReadBufferCleared;
    }

    public string PipeName => _pipeName;

    public void Start()
    {
        lock (_stateLock)
        {
            if (_running) throw new InvalidOperationException("server already started");
            if (_runtimeDirectory is { } directory)
            {
                BrokerRuntimeDirectory.Prepare(directory);
                BrokerRuntimeDirectory.WritePipeName(directory, _pipeName);
            }
            _running = true;
        }
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public void Stop()
    {
        lock (_stateLock)
        {
            if (!_running) return;
            _running = false;
        }
        _stop.Cancel();
        try { _acceptLoop?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        if (_runtimeDirectory is { } directory) BrokerRuntimeDirectory.RemovePipeName(directory);
    }

    public void Dispose()
    {
        Stop();
        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (IsRunning)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(
                    _pipeName,
                    PipeDirection.InOut,
                    BrokerLimits.MaximumQueuedRequests,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    0,
                    0,
                    BrokerAcl.PrivatePipeSecurity(),
                    HandleInheritability.None);
            }
            catch (IOException) when (!IsRunning)
            {
                return;
            }
            catch (Exception)
            {
                if (!IsRunning) return;
                await Task.Delay(10).ConfigureAwait(false);
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

            if (!ReserveConnection())
            {
                await WriteAsync(pipe, BrokerResponse.Failure(BrokerErrorCode.ResourceExhausted)).ConfigureAwait(false);
                pipe.Dispose();
                continue;
            }
            _ = Task.Run(async () =>
            {
                try { await ServeAsync(pipe).ConfigureAwait(false); }
                finally
                {
                    pipe.Dispose();
                    ReleaseConnection();
                }
            });
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        for (var index = 0; index < BrokerLimits.MaximumRequestsPerConnection; index++)
        {
            var frame = await BrokerFraming.ReadFrameAsync(
                pipe,
                BrokerLimits.MaximumFrameBytes,
                BrokerLimits.ReadDeadline,
                _failedReadBufferCleared,
                _stop.Token).ConfigureAwait(false);
            switch (frame.Result)
            {
                case BrokerFrameRead.Kind.End:
                    return;
                case BrokerFrameRead.Kind.Failure:
                    await WriteAsync(pipe, BrokerResponse.Failure(frame.Failure == BrokerFrameFailure.TooLarge
                        ? BrokerErrorCode.ResourceExhausted
                        : BrokerErrorCode.DeadlineExceeded)).ConfigureAwait(false);
                    return;
            }

            if (!ReserveRequest())
            {
                await WriteAsync(pipe, BrokerResponse.Failure(BrokerErrorCode.ResourceExhausted)).ConfigureAwait(false);
                return;
            }

            var lease = new BrokerRequestLease(ReleaseRequest);
            BrokerRequest? request;
            try
            {
                request = BrokerJson.DecodeRequest(frame.Data!);
            }
            finally
            {
                global::System.Array.Clear(frame.Data!);
            }
            if (request is null)
            {
                lease.Release();
                if (!await WriteAsync(pipe, BrokerResponse.Failure(BrokerErrorCode.InvalidRequest)).ConfigureAwait(false))
                {
                    return;
                }
                continue;
            }

            BrokerRuntimeChannel? descriptors = null;
            if (request.RuntimeChannel is not null)
            {
                if (request.Method != "runtime.run")
                {
                    lease.Release();
                    if (!await WriteAsync(pipe, BrokerResponse.Failure(BrokerErrorCode.InvalidRequest)).ConfigureAwait(false))
                    {
                        return;
                    }
                    continue;
                }
                try
                {
                    descriptors = BrokerRuntimeChannel.ConnectBroker(request.RuntimeChannel, BrokerLimits.WriteDeadline);
                }
                catch (Exception)
                {
                    lease.Release();
                    if (!await WriteAsync(pipe, BrokerResponse.Failure(BrokerErrorCode.InvalidRequest)).ConfigureAwait(false))
                    {
                        return;
                    }
                    continue;
                }
            }

            using (descriptors)
            {
                var response = await ExecuteAsync(request, descriptors, pipe, lease).ConfigureAwait(false);
                if (!await WriteAsync(pipe, response).ConfigureAwait(false))
                {
                    _handler.CancelUndelivered(request, response);
                    return;
                }
            }
        }
        await WriteAsync(pipe, BrokerResponse.Failure(BrokerErrorCode.ResourceExhausted)).ConfigureAwait(false);
    }

    private async Task<BrokerResponse> ExecuteAsync(
        BrokerRequest request,
        BrokerRuntimeChannel? descriptors,
        NamedPipeServerStream pipe,
        BrokerRequestLease lease)
    {
        var hasDeadline = request.Method != "runtime.run";
        var cancellation = new BrokerCancellation();
        var completed = new TaskCompletionSource<BrokerResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource? disconnect = null;

        if (!hasDeadline)
        {
            // A disconnect or any protocol byte on the request connection cancels
            // the runtime, matching the Swift DispatchSource read handler.
            disconnect = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            var token = disconnect.Token;
            _ = Task.Run(async () =>
            {
                var probe = new byte[1];
                try
                {
                    _ = await pipe.ReadAsync(probe, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException)
                {
                }
                cancellation.Cancel();
            });
        }

        _ = Task.Run(() =>
        {
            BrokerResponse response;
            try
            {
                response = _handler.Handle(request, descriptors, cancellation);
            }
            catch (Exception)
            {
                response = BrokerResponse.Failure(BrokerErrorCode.InternalError);
            }
            finally
            {
                lease.Release();
            }
            completed.TrySetResult(response);
        });

        if (!hasDeadline)
        {
            try
            {
                return await completed.Task.ConfigureAwait(false);
            }
            finally
            {
                disconnect?.Cancel();
                disconnect?.Dispose();
            }
        }

        using var timeout = new CancellationTokenSource();
        var finished = await Task.WhenAny(completed.Task, Task.Delay(BrokerLimits.RequestDeadline, timeout.Token))
            .ConfigureAwait(false);
        if (finished == completed.Task)
        {
            timeout.Cancel();
            return completed.Task.Result;
        }
        cancellation.Cancel();
        lease.Release();
        return BrokerResponse.Failure(BrokerErrorCode.DeadlineExceeded);
    }

    private async Task<bool> WriteAsync(Stream pipe, BrokerResponse response)
    {
        var encoded = BrokerJson.Encode(response);
        if (encoded.Length > BrokerLimits.MaximumResponseBytes)
        {
            encoded = BrokerJson.Encode(BrokerResponse.Failure(BrokerErrorCode.ResponseTooLarge));
        }
        return await BrokerFraming.WriteFrameAsync(pipe, encoded, _stop.Token).ConfigureAwait(false);
    }

    private bool IsRunning
    {
        get
        {
            lock (_stateLock) return _running;
        }
    }

    private bool ReserveConnection()
    {
        lock (_stateLock)
        {
            if (_activeConnections >= BrokerLimits.MaximumConnections) return false;
            _activeConnections++;
            return true;
        }
    }

    private void ReleaseConnection()
    {
        lock (_stateLock) _activeConnections--;
    }

    private bool ReserveRequest()
    {
        lock (_stateLock)
        {
            if (_activeRequests >= BrokerLimits.MaximumConcurrentRequests) return false;
            _activeRequests++;
            return true;
        }
    }

    private void ReleaseRequest()
    {
        lock (_stateLock) _activeRequests--;
    }

    /// <summary>Test seam: exercises the cleared-partial-buffer path without a socket.</summary>
    internal static void ExercisePartialReadErrorForTesting(
        byte[] partial,
        int declaredLength,
        BrokerClearedReadFailure failure,
        Action<BrokerClearedReadBuffer> cleared)
    {
        var buffer = new byte[declaredLength];
        partial.AsSpan().CopyTo(buffer);
        global::System.Array.Clear(buffer);
        cleared(new BrokerClearedReadBuffer
        {
            Buffer = buffer,
            BytesRead = partial.Length,
            Failure = failure,
        });
    }
}


