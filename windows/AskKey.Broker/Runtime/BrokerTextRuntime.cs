using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AskKey.Broker.Approval;
using AskKey.Broker.Protocol;
using AskKey.Broker.Transport;

namespace AskKey.Broker.Runtime;

public enum BrokerRuntimeSignal : byte
{
    Interrupt = 2,
    Terminate = 15,
}

/// <summary>
/// Vault-delivery lease held through the exact spawn boundary. Disposal releases
/// an abandoned lease the way Swift's deinit does.
/// </summary>
public sealed class BrokerTextDeliveryLease : IDisposable
{
    private readonly object _lock = new();
    private readonly Action _beginSpawnAuthorization;
    private readonly Action _validate;
    private readonly Action _endSpawnAuthorization;
    private readonly BrokerRuntimeReadAuthorization? _runtimeAuthorization;
    private Action? _finishAction;
    private Action? _cleanupAction;

    public DateTimeOffset? SpawnDeadline { get; }

    public BrokerTextDeliveryLease(
        Action beginSpawnAuthorization,
        Action validate,
        Action endSpawnAuthorization,
        DateTimeOffset? spawnDeadline,
        BrokerRuntimeReadAuthorization? runtimeAuthorization,
        Action finish,
        Action cleanup)
    {
        _beginSpawnAuthorization = beginSpawnAuthorization;
        _validate = validate;
        _endSpawnAuthorization = endSpawnAuthorization;
        SpawnDeadline = spawnDeadline;
        _runtimeAuthorization = runtimeAuthorization;
        _finishAction = finish;
        _cleanupAction = cleanup;
    }

    public T PerformAuthorizedSpawn<T>(Action afterAuthorization, Func<T> spawn)
    {
        lock (_lock)
        {
            if (_finishAction is null) throw new BrokerTextRuntimeException(BrokerTextRuntimeError.InvalidRequest);
            _beginSpawnAuthorization();
            try
            {
                if (_runtimeAuthorization is not null)
                {
                    // Lock order is lease -> Vault gate -> approval. Revocation never
                    // acquires the Vault gate or waits for this lease's active operation.
                    return _runtimeAuthorization.PerformAuthorizedSpawn(() =>
                    {
                        afterAuthorization();
                        _validate();
                        return spawn();
                    });
                }
                afterAuthorization();
                _validate();
                return spawn();
            }
            finally
            {
                _endSpawnAuthorization();
            }
        }
    }

    public void Finish()
    {
        Action? action;
        lock (_lock)
        {
            action = _finishAction;
            _finishAction = null;
        }
        action?.Invoke();
    }

    public void Cleanup()
    {
        Action? action;
        lock (_lock)
        {
            action = _cleanupAction;
            _cleanupAction = null;
        }
        action?.Invoke();
        _runtimeAuthorization?.Finish();
    }

    public void Dispose()
    {
        Finish();
        Cleanup();
    }
}

public sealed class BrokerTextCredentialResolution
{
    public enum Kind
    {
        Resolved,
        ApprovalRequired,
    }

    public Kind ResolutionKind { get; }
    public IReadOnlyList<BrokerTextCredential> Credentials { get; }
    public int ResolvedRequestCount { get; }
    public BrokerTextDeliveryLease? DeliveryLease { get; }
    public IReadOnlyList<BrokerApprovalTicket> Tickets { get; }

    private BrokerTextCredentialResolution(
        Kind kind,
        IReadOnlyList<BrokerTextCredential> credentials,
        int resolvedRequestCount,
        BrokerTextDeliveryLease? deliveryLease,
        IReadOnlyList<BrokerApprovalTicket> tickets)
    {
        ResolutionKind = kind;
        Credentials = credentials;
        ResolvedRequestCount = resolvedRequestCount;
        DeliveryLease = deliveryLease;
        Tickets = tickets;
    }

    public static BrokerTextCredentialResolution Resolved(
        IReadOnlyList<BrokerTextCredential> credentials,
        int? resolvedRequestCount = null,
        BrokerTextDeliveryLease? deliveryLease = null)
        => new(
            Kind.Resolved,
            credentials,
            resolvedRequestCount ?? (credentials.Count == 0 ? 0 : 1),
            deliveryLease,
            Array.Empty<BrokerApprovalTicket>());

    public static BrokerTextCredentialResolution ApprovalRequired(IReadOnlyList<BrokerApprovalTicket> tickets)
        => new(Kind.ApprovalRequired, Array.Empty<BrokerTextCredential>(), 0, null, tickets);
}

/// <summary>
/// Starts the target directly from the Broker. Target streams remain connected to
/// caller-owned pipes; this type never reads or buffers their contents.
/// </summary>
public sealed class BrokerTextRuntime
{
    public delegate BrokerTextCredentialResolution CredentialResolver(
        BrokerTextRunRequest request, BrokerCancellation cancellation);

    private readonly BrokerRuntimeOperations _operations;
    private readonly CredentialResolver _resolveCredentials;
    private readonly Action _beforeSpawn;
    private readonly Action _afterAuthorization;
    private readonly Action _beforeSystemSpawn;
    private readonly Action _afterSpawn;
    private readonly TimeSpan _terminationGrace;
    private readonly TimeSpan _forceKillGrace;

    public BrokerTextRuntime(
        CredentialResolver resolveCredentials,
        Action? beforeSpawn = null,
        Action? afterAuthorization = null,
        Action? beforeSystemSpawn = null,
        Action? afterSpawn = null,
        TimeSpan? terminationGrace = null,
        TimeSpan? forceKillGrace = null,
        int receiptCapacity = BrokerLimits.MaximumRuntimeReceiptCount)
    {
        _operations = new BrokerRuntimeOperations(receiptCapacity);
        _resolveCredentials = resolveCredentials;
        _beforeSpawn = beforeSpawn ?? (() => { });
        _afterAuthorization = afterAuthorization ?? (() => { });
        _beforeSystemSpawn = beforeSystemSpawn ?? (() => { });
        _afterSpawn = afterSpawn ?? (() => { });
        _terminationGrace = ValidDuration(terminationGrace) ?? TimeSpan.FromSeconds(0.5);
        _forceKillGrace = ValidDuration(forceKillGrace) ?? TimeSpan.FromSeconds(1);
    }

    internal static void ValidateRequestBeforeReceipt(BrokerTextRunRequest request)
    {
        if (request.Command.Count == 0) throw new BrokerTextRuntimeException(BrokerTextRuntimeError.MissingCommand);
        if (request.CredentialNames.Count == 0)
        {
            throw new BrokerTextRuntimeException(BrokerTextRuntimeError.MissingCredentials);
        }
        if (!ValidField(request.OperationId)
            || !request.Command.All(ValidField)
            || !request.CredentialNames.All(ValidField)
            || request.CredentialNames.Distinct(StringComparer.Ordinal).Count() != request.CredentialNames.Count
            || !request.DeclarationsAreValid
            || BrokerTextRunRequest.FilteredInheritedEnvironment(request.InheritedEnvironment)
                .Values.Any(value => value.Contains('\0')))
        {
            throw new BrokerTextRuntimeException(BrokerTextRuntimeError.InvalidRequest);
        }
        if (request.WorkingDirectory is { } directory)
        {
            if (!ValidField(directory) || !Path.IsPathFullyQualified(directory))
            {
                throw new BrokerTextRuntimeException(BrokerTextRuntimeError.InvalidWorkingDirectory);
            }
        }
    }

    public BrokerTextRunResult Run(
        BrokerTextRunRequest request,
        BrokerRuntimeChannel channel,
        BrokerCancellation? cancellation = null)
    {
        cancellation ??= new BrokerCancellation();
        return _operations.Perform(request, cancellation, () => RunOnce(request, channel, cancellation));
    }

    private BrokerTextRunResult RunOnce(
        BrokerTextRunRequest request,
        BrokerRuntimeChannel channel,
        BrokerCancellation cancellation)
    {
        if (request.WorkingDirectory is { } directory)
        {
            if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
            {
                throw new BrokerTextRuntimeException(BrokerTextRuntimeError.InvalidWorkingDirectory);
            }
        }
        var resolution = _resolveCredentials(request, cancellation);
        if (resolution.ResolutionKind == BrokerTextCredentialResolution.Kind.ApprovalRequired)
        {
            return BrokerTextRunResult.ApprovalRequired(request.OperationId, resolution.Tickets);
        }
        var credentials = resolution.Credentials;
        var lease = resolution.DeliveryLease;
        try
        {
            if (resolution.ResolvedRequestCount != request.CredentialNames.Count
                || !credentials.All(credential => ValidEnvironmentName(credential.EnvironmentVariable))
                || credentials.Select(credential => credential.EnvironmentVariable)
                    .Distinct(StringComparer.Ordinal).Count() != credentials.Count)
            {
                throw new BrokerTextRuntimeException(BrokerTextRuntimeError.InvalidCredentialMapping);
            }

            var environment = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in BrokerTextRunRequest.FilteredInheritedEnvironment(request.InheritedEnvironment))
            {
                environment[key] = value;
            }
            foreach (var credential in credentials) environment[credential.EnvironmentVariable] = credential.Value;
            if (environment.Values.Any(value => value.Contains('\0')))
            {
                throw new BrokerTextRuntimeException(BrokerTextRuntimeError.InvalidCredentialMapping);
            }

            _beforeSpawn();
            System.Diagnostics.Process? process = null;
            var spawn = () =>
            {
                cancellation.Check();
                process = Spawn(request, environment, channel);
                return process;
            };
            if (lease is not null)
            {
                _ = lease.PerformAuthorizedSpawn(_afterAuthorization, spawn);
            }
            else
            {
                _afterAuthorization();
                _ = spawn();
            }
            lease?.Finish();
            if (process is null) return BrokerTextRunResult.OutcomeUnknown;

            using var controlStop = new CancellationTokenSource();
            var control = Task.Run(() => PumpControl(channel.Control, process, cancellation, controlStop.Token));
            try
            {
                try
                {
                    _afterSpawn();
                }
                catch (Exception)
                {
                    _ = Terminate(process);
                    return BrokerTextRunResult.OutcomeUnknown;
                }
                var exitCode = WaitForExit(process, cancellation);
                if (exitCode is null) return BrokerTextRunResult.OutcomeUnknown;
                return BrokerTextRunResult.Exited(exitCode.Value);
            }
            finally
            {
                controlStop.Cancel();
                try { control.Wait(TimeSpan.FromSeconds(1)); } catch (AggregateException) { }
            }
        }
        finally
        {
            lease?.Finish();
            lease?.Cleanup();
        }
    }

    private static int? WaitForExit(System.Diagnostics.Process process, BrokerCancellation cancellation)
    {
        while (true)
        {
            if (cancellation.IsCancelled)
            {
                // Terminating the tree also releases the stdout/stderr bridge tasks.
                if (!Terminate(process)) return null;
                break;
            }
            if (process.WaitForExit(10)) break;
        }
        try { process.WaitForExit(); } catch (InvalidOperationException) { return null; }
        try { return process.ExitCode; } catch (InvalidOperationException) { return null; }
    }

    private static bool Terminate(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            return process.WaitForExit(TimeSpan.FromSeconds(2));
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (global::System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private void PumpControl(
        Stream control,
        System.Diagnostics.Process process,
        BrokerCancellation cancellation,
        CancellationToken stop)
    {
        var buffer = new byte[1];
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var read = control.ReadAsync(buffer, stop).AsTask();
                read.Wait();
                if (read.Result == 0)
                {
                    // EOF on the control pipe cancels the runtime, exactly like
                    // Swift's zero-length control read.
                    cancellation.Cancel();
                    return;
                }
                switch ((BrokerRuntimeSignal)buffer[0])
                {
                    case BrokerRuntimeSignal.Interrupt:
                        SendInterrupt(process);
                        break;
                    case BrokerRuntimeSignal.Terminate:
                        _ = Terminate(process);
                        return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (AggregateException) { }
    }

    private static void SendInterrupt(System.Diagnostics.Process process)
    {
        // Windows has no SIGINT for an arbitrary child without a console group;
        // Ctrl+C cannot be delivered to a detached redirected process, so the
        // closest portable control is a graceful tree termination request.
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (global::System.ComponentModel.Win32Exception) { }
    }

    private System.Diagnostics.Process Spawn(
        BrokerTextRunRequest request,
        IReadOnlyDictionary<string, string> environment,
        BrokerRuntimeChannel channel)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = request.Command[0],
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        for (var index = 1; index < request.Command.Count; index++)
        {
            startInfo.ArgumentList.Add(request.Command[index]);
        }
        startInfo.Environment.Clear();
        foreach (var (key, value) in environment) startInfo.Environment[key] = value;
        if (request.WorkingDirectory is { } directory) startInfo.WorkingDirectory = directory;

        var process = new System.Diagnostics.Process { StartInfo = startInfo };
        _beforeSystemSpawn();
        try
        {
            if (!process.Start()) throw new BrokerTextRuntimeException(BrokerTextRuntimeError.SpawnFailed);
        }
        catch (global::System.ComponentModel.Win32Exception)
        {
            process.Dispose();
            throw new BrokerTextRuntimeException(BrokerTextRuntimeError.SpawnFailed);
        }

        // stdio is inherited by redirection inside the helper, replacing the four
        // SCM_RIGHTS descriptors with the four named pipes of the runtime channel.
        _ = BridgeAsync(channel.Stdin, process.StandardInput.BaseStream, closeDestination: true);
        _ = BridgeAsync(process.StandardOutput.BaseStream, channel.Stdout, closeDestination: false);
        _ = BridgeAsync(process.StandardError.BaseStream, channel.Stderr, closeDestination: false);
        return process;
    }

    private static async Task BridgeAsync(Stream source, Stream destination, bool closeDestination)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var count = await source.ReadAsync(buffer).ConfigureAwait(false);
                if (count <= 0) break;
                await destination.WriteAsync(buffer.AsMemory(0, count)).ConfigureAwait(false);
                await destination.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            if (closeDestination)
            {
                try { destination.Close(); } catch (IOException) { }
            }
        }
    }

    private static bool ValidField(string value)
        => value.Length > 0 && BrokerWire.Utf8Length(value) <= BrokerLimits.MaximumFieldBytes
            && !value.Contains('\0');

    private static TimeSpan? ValidDuration(TimeSpan? value)
        => value is { } duration && duration > TimeSpan.Zero && duration <= TimeSpan.FromSeconds(60)
            ? duration
            : null;

    private static bool ValidEnvironmentName(string value)
    {
        if (value.Length == 0) return false;
        var first = value[0];
        if (!(char.IsLetter(first) || first == '_')) return false;
        return value.Skip(1).All(character => char.IsLetterOrDigit(character) || character == '_');
    }
}

/// <summary>
/// Runtime identity is independent of permission and approval identity. One
/// instance belongs to the App runtime, covering silent and approved deliveries.
/// </summary>
internal sealed class BrokerRuntimeOperations
{
    private sealed class Entry
    {
        public required byte[] Digest { get; init; }
        public bool InFlight { get; set; }
        public BrokerTextRunResult? Result { get; set; }
        public Exception? Error { get; set; }
    }

    private readonly object _lock = new();
    private readonly int _receiptCapacity;
    // Retain compact identity/result receipts for this runtime's lifetime. They
    // never contain credential material, commands or inherited environment.
    // Completed work must neither consume concurrency slots nor be forgotten;
    // capacity rejects only new identities, so existing receipts remain replayable.
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private int _inFlightCount;

    public BrokerRuntimeOperations(int receiptCapacity = BrokerLimits.MaximumRuntimeReceiptCount)
        => _receiptCapacity = Math.Min(BrokerLimits.MaximumRuntimeReceiptCount, Math.Max(1, receiptCapacity));

    public BrokerTextRunResult Perform(
        BrokerTextRunRequest request,
        BrokerCancellation cancellation,
        Func<BrokerTextRunResult> operation)
    {
        BrokerTextRuntime.ValidateRequestBeforeReceipt(request);
        // Swift keys the receipt by SHA256(operationID); a hex projection keeps
        // the same identity without exposing credential material.
        var identifier = Convert.ToHexStringLower(Digest(Encoding.UTF8.GetBytes(request.OperationId)));
        var digest = Digest(CanonicalRequest(request));
        var waited = false;
        Entry? entry;
        lock (_lock)
        {
            while (_entries.TryGetValue(identifier, out var existing))
            {
                if (!existing.Digest.AsSpan().SequenceEqual(digest))
                {
                    throw new BrokerApprovalException(BrokerApprovalError.PayloadMismatch);
                }
                if (existing.InFlight)
                {
                    if (cancellation.IsCancelled) throw new BrokerCancellationException();
                    waited = true;
                    Monitor.Wait(_lock, 50);
                    continue;
                }
                if (existing.Error is not null) throw existing.Error;
                if (existing.Result is { } completed)
                {
                    // An approval response is resumable on a later invocation. A
                    // concurrent retransmission only observes this invocation's result.
                    if (completed.ResultKind == BrokerTextRunResult.Kind.ApprovalRequired && !waited)
                    {
                        if (_inFlightCount >= BrokerLimits.MaximumConcurrentRequests)
                        {
                            throw new BrokerProviderException(BrokerProviderError.ResourceExhausted);
                        }
                        existing.InFlight = true;
                        _inFlightCount++;
                        break;
                    }
                    return completed;
                }
            }
            if (!_entries.TryGetValue(identifier, out entry))
            {
                if (_entries.Count >= _receiptCapacity)
                {
                    throw new BrokerProviderException(BrokerProviderError.ResourceExhausted);
                }
                if (_inFlightCount >= BrokerLimits.MaximumConcurrentRequests)
                {
                    throw new BrokerProviderException(BrokerProviderError.ResourceExhausted);
                }
                entry = new Entry { Digest = digest, InFlight = true };
                _entries[identifier] = entry;
                _inFlightCount++;
            }
        }

        BrokerTextRunResult? result = null;
        Exception? failure = null;
        try
        {
            result = operation();
        }
        catch (Exception error)
        {
            failure = error;
        }
        lock (_lock)
        {
            entry.Result = result;
            entry.Error = failure;
            entry.InFlight = false;
            _inFlightCount--;
            Monitor.PulseAll(_lock);
        }
        if (failure is not null)
        {
            global::System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        return result!;
    }

    /// <summary>Sorted-key JSON digest: SHA256 over the Swift-encoded request.</summary>
    internal static byte[] CanonicalRequest(BrokerTextRunRequest request)
    {
        var environment = new JsonObject();
        foreach (var key in request.InheritedEnvironment.Keys.OrderBy(key => key, StringComparer.Ordinal))
        {
            environment[key] = request.InheritedEnvironment[key];
        }
        var json = new JsonObject
        {
            ["operationID"] = request.OperationId,
            ["command"] = new JsonArray(request.Command.Select(value => JsonValue.Create(value)).ToArray()),
            ["credentialNames"] = new JsonArray(request.CredentialNames.Select(value => JsonValue.Create(value)).ToArray()),
        };
        if (request.WorkingDirectory is { } directory) json["workingDirectory"] = directory;
        json["inheritedEnvironment"] = environment;
        if (request.CallerName is { } callerName) json["callerName"] = callerName;
        if (request.CallerPurpose is { } callerPurpose) json["callerPurpose"] = callerPurpose;
        return Encoding.UTF8.GetBytes(json.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
    }

    private static byte[] Digest(byte[] data) => SHA256.HashData(data);
}
