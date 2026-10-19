using System.IO.Pipes;

namespace AskKey.Broker.Transport;

/// <summary>
/// Descriptor stand-in for one <c>runtime.run</c>. Windows has no SCM_RIGHTS, so
/// the caller creates four named-pipe servers keyed by a random token and sends
/// only the token in the request envelope. The Broker connects as the client.
/// </summary>
public sealed class BrokerRuntimeChannel : IDisposable
{
    public const int StdinIndex = 0;
    public const int StdoutIndex = 1;
    public const int StderrIndex = 2;
    public const int ControlIndex = 3;

    private readonly NamedPipeServerStream[] _servers;
    private readonly Stream[] _streams;
    private readonly IDisposable[] _owned;
    private readonly TaskCompletionSource<bool> _connectedSignal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _connected;

    private BrokerRuntimeChannel(string token, Stream[] streams, NamedPipeServerStream[] servers)
    {
        Token = token;
        _streams = streams;
        _servers = servers;
        _owned = streams.OfType<IDisposable>().Concat(servers).Distinct().ToArray();
    }

    /// <summary>
    /// True once the Broker has connected all four pipes; false if the channel was
    /// disposed before that happened.
    /// </summary>
    public Task<bool> Connected => _connectedSignal.Task;

    /// <summary>
    /// /dev/null stdin: closes the caller's stdin server after the Broker connects,
    /// so the child observes EOF immediately instead of waiting on a pipe nobody
    /// will write to.
    /// </summary>
    public async Task CloseStdinAfterConnectAsync(CancellationToken cancellationToken = default)
    {
        if (await Connected.WaitAsync(cancellationToken).ConfigureAwait(false))
        {
            Stdin.Dispose();
        }
    }

    public string Token { get; }

    /// <summary>Test/diagnostic seam: the accept task started for a self-connected channel.</summary>
    internal Task? AcceptTask { get; set; }

    public Stream Stdin => _streams[StdinIndex];

    public Stream Stdout => _streams[StdoutIndex];

    public Stream Stderr => _streams[StderrIndex];

    public Stream Control => _streams[ControlIndex];

    public static string NewToken() => global::System.Convert.ToHexStringLower(
        global::System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

    public static string PipeName(string token, int index)
        => $"{token}-{(index == StdinIndex ? "in" : index == StdoutIndex ? "out" : index == StderrIndex ? "err" : "ctl")}";

    public static bool IsValidToken(string token)
        => token.Length == 32 && token.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>
    /// Caller side. Pipes exist before the request is written, so the Broker's
    /// connect cannot race creation; <see cref="ConnectCallerAsync"/> completes
    /// them once the request is in flight.
    /// </summary>
    public static BrokerRuntimeChannel CreateCaller(string? token = null)
    {
        token ??= NewToken();
        if (!IsValidToken(token)) throw new ArgumentException("invalid runtime channel token", nameof(token));
        var servers = new NamedPipeServerStream[4];
        try
        {
            for (var index = 0; index < 4; index++)
            {
                servers[index] = NamedPipeServerStreamAcl.Create(
                    PipeName(token, index),
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    0,
                    0,
                    BrokerAcl.PrivatePipeSecurity(),
                    HandleInheritability.None);
            }
            return new BrokerRuntimeChannel(token, servers.Cast<Stream>().ToArray(), servers);
        }
        catch
        {
            foreach (var server in servers) server?.Dispose();
            throw;
        }
    }

    /// <summary>Accepts the Broker's four connections. Call after the request frame is written.</summary>
    public async Task ConnectCallerAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _connected, 1) != 0)
        {
            await _connectedSignal.Task.ConfigureAwait(false);
            return;
        }
        var waits = _servers.Select(server => server.WaitForConnectionAsync(cancellationToken)).ToArray();
        await Task.WhenAll(waits).ConfigureAwait(false);
        _connectedSignal.TrySetResult(true);
    }

    /// <summary>Broker side: connects to the four caller-created pipes.</summary>
    public static BrokerRuntimeChannel ConnectBroker(string token, TimeSpan timeout)
    {
        if (!IsValidToken(token)) throw new FormatException("runtime channel token");
        var clients = new NamedPipeClientStream[4];
        try
        {
            for (var index = 0; index < 4; index++)
            {
                clients[index] = new NamedPipeClientStream(
                    ".", PipeName(token, index), PipeDirection.InOut, PipeOptions.Asynchronous);
                clients[index].Connect((int)Math.Max(1, timeout.TotalMilliseconds));
            }
            return new BrokerRuntimeChannel(
                token, clients.Cast<Stream>().ToArray(), Array.Empty<NamedPipeServerStream>());
        }
        catch
        {
            foreach (var client in clients) client?.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        // Unblock CloseStdinAfterConnectAsync when the request never reached the Broker.
        _connectedSignal.TrySetResult(false);
        foreach (var owned in _owned)
        {
            try { owned.Dispose(); } catch (IOException) { }
        }
    }
}
