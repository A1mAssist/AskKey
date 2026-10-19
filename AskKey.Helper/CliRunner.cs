using System.Diagnostics;
using AskKey.Broker.Approval;
using AskKey.Broker.Protocol;
using AskKey.Broker.Runtime;
using AskKey.Broker.Transport;

namespace AskKey.Helper;

internal static class CliRunner
{
    public static int Run(string[] args)
    {
        var credentials = new List<string>();
        var operationId = Guid.NewGuid().ToString();
        string? callerName = null;
        string? callerPurpose = null;
        var waitForApproval = false;
        var index = 1;
        while (index < args.Length && args[index] != "--")
        {
            if (args[index] == "--wait-for-approval")
            {
                waitForApproval = true;
                index++;
                continue;
            }
            if (index + 1 >= args.Length) throw new HelperUsageException();
            switch (args[index])
            {
                case "--credential": credentials.Add(args[index + 1]); break;
                case "--operation-id": operationId = args[index + 1]; break;
                case "--caller-name": callerName = args[index + 1]; break;
                case "--caller-purpose": callerPurpose = args[index + 1]; break;
                default: throw new HelperUsageException();
            }
            index += 2;
        }
        if (index >= args.Length || args[index] != "--") throw new HelperUsageException();
        var command = args.Skip(index + 1).ToArray();
        var request = new BrokerTextRunRequest(
            operationId,
            command,
            credentials,
            Directory.GetCurrentDirectory(),
            CurrentEnvironment(),
            callerName,
            callerPurpose);
        var client = BrokerSocketClient.FromConfiguration();
        BrokerTextRunResult result;
        if (waitForApproval)
        {
            result = WaitingRunner.Run(request, client);
        }
        else
        {
            using var cancellation = new CancellationBridge();
            using var signals = new SignalBridge(cancellation);
            result = RunOnce(request, client, cancellation);
        }
        return Finish(result);
    }

    internal static IReadOnlyDictionary<string, string> CurrentEnvironment()
        => BrokerTextRunRequest.FilteredInheritedEnvironment(
            Environment.GetEnvironmentVariables()
                .Cast<global::System.Collections.DictionaryEntry>()
                .Where(entry => entry.Key is string && entry.Value is string)
                .ToDictionary(
                    entry => (string)entry.Key,
                    entry => (string)entry.Value!,
                    StringComparer.Ordinal));

    internal static int Finish(BrokerTextRunResult result)
    {
        switch (result.ResultKind)
        {
            case BrokerTextRunResult.Kind.Exited:
                return result.ExitCode;
            case BrokerTextRunResult.Kind.OutcomeUnknown:
                HelperIO.WriteError(HelperCopy.OutcomeUnknown);
                return 1;
            default:
                HelperIO.WriteJson(BrokerJson.EncodeTextRunResult(result));
                throw new HelperApprovalRequiredException();
        }
    }

    internal static BrokerTextRunResult RunOnce(
        BrokerTextRunRequest request,
        BrokerSocketClient client,
        CancellationBridge cancellation)
    {
        using var channel = BrokerRuntimeChannel.CreateCaller();
        using var stopStdin = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        var stdin = ForwardStandardInput(channel, stopStdin.Token);
        var stdout = ForwardToStandardOutput(channel, channel.Stdout, HelperIO.StandardOutput);
        var stderr = ForwardToStandardOutput(channel, channel.Stderr, HelperIO.StandardError);
        var brokerCancellation = new BrokerCancellation();
        cancellation.OnCancel(brokerCancellation.Cancel);
        brokerCancellation.OnCancel(() => stopStdin.Cancel());
        try
        {
            return client.Run(request, channel, brokerCancellation);
        }
        finally
        {
            stopStdin.Cancel();
            try { channel.Stdin.Close(); } catch (IOException) { } catch (ObjectDisposedException) { }
            WaitForForwarding(stdout, stderr);
        }
    }

    private static async Task ForwardStandardInput(BrokerRuntimeChannel channel, CancellationToken cancellation)
    {
        try
        {
            if (!await channel.Connected.WaitAsync(cancellation).ConfigureAwait(false)) return;
            using var input = Console.OpenStandardInput();
            await input.CopyToAsync(channel.Stdin, cancellation).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
        }
        finally
        {
            try { channel.Stdin.Close(); } catch (IOException) { } catch (ObjectDisposedException) { }
        }
    }

    private static async Task ForwardToStandardOutput(BrokerRuntimeChannel channel, Stream source, Stream destination)
    {
        try
        {
            if (!await channel.Connected.ConfigureAwait(false)) return;
            await source.CopyToAsync(destination).ConfigureAwait(false);
            await destination.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private static void WaitForForwarding(params Task[] tasks)
    {
        try
        {
            if (!Task.WaitAll(tasks, TimeSpan.FromSeconds(10)))
            {
                // The Broker response arrived after the child exited; give the
                // pipe drains one final bounded chance, then close the channel.
                _ = Task.WhenAll(tasks).ContinueWith(
                    static finished => _ = finished.Exception,
                    TaskContinuationOptions.OnlyOnFaulted);
            }
        }
        catch (AggregateException)
        {
        }
    }
}

internal sealed class CancellationBridge : IDisposable
{
    private readonly CancellationTokenSource _source = new();
    private readonly List<Action> _callbacks = new();
    private readonly object _lock = new();

    public CancellationToken Token => _source.Token;

    public void OnCancel(Action callback)
    {
        lock (_lock)
        {
            if (!_source.IsCancellationRequested)
            {
                _callbacks.Add(callback);
                return;
            }
        }
        callback();
    }

    public void Cancel()
    {
        List<Action> callbacks;
        lock (_lock)
        {
            if (_source.IsCancellationRequested) return;
            callbacks = new List<Action>(_callbacks);
            _callbacks.Clear();
        }
        _source.Cancel();
        foreach (var callback in callbacks)
        {
            try { callback(); } catch (Exception) { }
        }
    }

    public void Dispose() => _source.Dispose();
}

internal static class WaitingRunner
{
    private static readonly TimeSpan WaitDeadline = TimeSpan.FromMinutes(5);

    public static BrokerTextRunResult Run(BrokerTextRunRequest request, BrokerSocketClient client)
    {
        using var cancellation = new CancellationBridge();
        using var signals = new SignalBridge(cancellation);
        try
        {
            return RunWaiting(request, client, cancellation);
        }
        catch (OperationCanceledException)
        {
            throw new HelperWaitException(string.Format(
                global::System.Globalization.CultureInfo.InvariantCulture,
                HelperCopy.WaitStoppedFormat,
                "cancelled"));
        }
    }

    private static BrokerTextRunResult RunWaiting(
        BrokerTextRunRequest request,
        BrokerSocketClient client,
        CancellationBridge cancellation)
    {
        var result = CliRunner.RunOnce(request, client, cancellation);
        var deadline = DateTimeOffset.UtcNow + WaitDeadline;
        var announced = false;
        while (result.ResultKind == BrokerTextRunResult.Kind.ApprovalRequired)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            if (result.OperationId != request.OperationId || result.Tickets.Count == 0)
            {
                throw new HelperWaitException(HelperCopy.WaitInvalid);
            }
            if (!announced)
            {
                HelperIO.WriteError(HelperCopy.Waiting);
                announced = true;
            }
            while (true)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    throw new HelperWaitException(HelperCopy.WaitExpired);
                }
                var allApproved = true;
                foreach (var ticket in result.Tickets)
                {
                    var response = client.Send(new BrokerRequest
                    {
                        Version = BrokerProtocolVersion.Current,
                        Method = "request.status",
                        RequestId = ticket.RequestId,
                        Capability = ticket.Capability,
                    });
                    if (response.ResponseKind != BrokerResponse.Kind.Success
                        || response.Payload?.PayloadKind != BrokerPayload.Kind.RequestStatus)
                    {
                        throw new BrokerSocketException(BrokerSocketError.MalformedResponse);
                    }
                    switch (response.Payload.RequestState)
                    {
                        case BrokerRequestState.Pending:
                            allApproved = false;
                            break;
                        case BrokerRequestState.Approved:
                            break;
                        default:
                            throw new HelperWaitException(string.Format(
                                global::System.Globalization.CultureInfo.InvariantCulture,
                                HelperCopy.WaitStoppedFormat,
                                BrokerWire.ToWire(response.Payload.RequestState)));
                    }
                }
                if (allApproved) break;
                cancellation.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(500));
            }
            result = CliRunner.RunOnce(request, client, cancellation);
        }
        return result;
    }
}

internal sealed class SignalBridge : IDisposable
{
    private readonly CancellationBridge _cancellation;
    private readonly List<global::System.Runtime.InteropServices.PosixSignalRegistration> _registrations = new();
    private readonly ConsoleCancelEventHandler? _cancelKeyPress;

    public SignalBridge(CancellationBridge cancellation)
    {
        _cancellation = cancellation;
        try
        {
            foreach (var signal in new[]
                     {
                         global::System.Runtime.InteropServices.PosixSignal.SIGINT,
                         global::System.Runtime.InteropServices.PosixSignal.SIGTERM,
                     })
            {
                _registrations.Add(global::System.Runtime.InteropServices.PosixSignalRegistration.Create(signal, context =>
                {
                    context.Cancel = true;
                    _cancellation.Cancel();
                }));
            }
        }
        catch (PlatformNotSupportedException)
        {
            _cancelKeyPress = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                _cancellation.Cancel();
            };
            Console.CancelKeyPress += _cancelKeyPress;
        }
    }

    public void Dispose()
    {
        foreach (var registration in _registrations) registration.Dispose();
        if (_cancelKeyPress is not null) Console.CancelKeyPress -= _cancelKeyPress;
    }
}
