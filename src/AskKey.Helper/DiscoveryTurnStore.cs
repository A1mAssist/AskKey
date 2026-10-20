using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using AskKey.Broker.Transport;

namespace AskKey.Helper;

/// <summary>
/// Short-lived routing metadata shared by separate command-hook processes.
/// Only hashes, timestamps and completion flags reach disk. This is not an
/// authorization store and never participates in Broker credential decisions.
/// </summary>
internal sealed class DiscoveryTurnStore
{
    internal enum Phase
    {
        Before,
        After,
    }

    internal enum Failure
    {
        Unsafe,
        Busy,
        Invalid,
    }

    private sealed class TurnState
    {
        [JsonPropertyName("pending")]
        public HashSet<string> Pending { get; set; } = new(StringComparer.Ordinal);

        [JsonPropertyName("completed")]
        public bool Completed { get; set; }

        [JsonPropertyName("touched")]
        public double Touched { get; set; }
    }

    private sealed class Generation
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        [JsonPropertyName("touched")]
        public double Touched { get; set; }
    }

    private sealed class State
    {
        [JsonPropertyName("turns")]
        public Dictionary<string, TurnState> Turns { get; set; } = new(StringComparer.Ordinal);

        [JsonPropertyName("grokTurns")]
        public Dictionary<string, Generation> GrokTurns { get; set; } = new(StringComparer.Ordinal);

        [JsonPropertyName("grokCalls")]
        public Dictionary<string, Generation> GrokCalls { get; set; } = new(StringComparer.Ordinal);

        [JsonPropertyName("claudeTurns")]
        public Dictionary<string, Generation> ClaudeTurns { get; set; } = new(StringComparer.Ordinal);

        [JsonPropertyName("claudeCalls")]
        public Dictionary<string, Generation> ClaudeCalls { get; set; } = new(StringComparer.Ordinal);
    }

    private const int MaximumStateBytes = 1_048_576;
    private const double DaySeconds = 86_400;
    private const double NoProgressSeconds = 30;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly Func<double> _now;

    public DiscoveryTurnStore(string directory, Func<double>? now = null)
    {
        Directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d);
    }

    public string Directory { get; }

    public void BeginClaudeTurn(string session)
    {
        Validate(session);
        WithState(state =>
        {
            var turns = state.ClaudeTurns ??= new Dictionary<string, Generation>(StringComparer.Ordinal);
            turns[Hash("claude", session)] = new Generation
            {
                Key = Guid.NewGuid().ToString(),
                Touched = Now(),
            };
            return true;
        });
    }

    public void BeginGrokTurn(string session, string promptId)
    {
        Validate(session);
        Validate(promptId);
        WithState(state =>
        {
            var turns = state.GrokTurns ??= new Dictionary<string, Generation>(StringComparer.Ordinal);
            turns[Hash("grok", session)] = new Generation
            {
                Key = Hash("grok", session, promptId),
                Touched = Now(),
            };
            return true;
        });
    }

    public bool CatalogAttemptSettled(
        string client,
        string session,
        string? turn,
        string? callId,
        Phase phase,
        bool catalog)
    {
        Validate(session);
        if (turn is not null) Validate(turn);
        if (callId is not null) Validate(callId);

        return WithState(state =>
        {
            var now = Now();
            PruneTurns(state, now);
            var call = callId is null ? null : Hash(client, session, callId);
            string key;
            if (turn is { Length: > 0 })
            {
                key = Hash(client, session, turn);
            }
            else if (client == "grok")
            {
                if (catalog && phase == Phase.After && call is not null)
                {
                    if (!state.GrokCalls.Remove(call, out var pending)) return false;
                    key = pending.Key;
                }
                else
                {
                    var sessionKey = Hash(client, session);
                    if (!state.GrokTurns.TryGetValue(sessionKey, out var generation))
                    {
                        generation = new Generation { Key = Guid.NewGuid().ToString(), Touched = now };
                        state.GrokTurns[sessionKey] = generation;
                    }
                    key = generation.Key;
                    if (catalog && call is not null)
                    {
                        state.GrokCalls[call] = new Generation { Key = key, Touched = now };
                    }
                }
            }
            else if (client == "claude")
            {
                var turns = state.ClaudeTurns ??= new Dictionary<string, Generation>(StringComparer.Ordinal);
                var calls = state.ClaudeCalls ??= new Dictionary<string, Generation>(StringComparer.Ordinal);
                if (catalog && phase == Phase.After && call is not null)
                {
                    if (!calls.Remove(call, out var pending)) return false;
                    key = pending.Key;
                }
                else
                {
                    var sessionKey = Hash(client, session);
                    if (!turns.TryGetValue(sessionKey, out var generation))
                    {
                        generation = new Generation { Key = Guid.NewGuid().ToString(), Touched = now };
                        turns[sessionKey] = generation;
                    }
                    key = generation.Key;
                    if (catalog && call is not null)
                    {
                        calls[call] = new Generation { Key = key, Touched = now };
                    }
                }
            }
            else
            {
                throw new InvalidOperationException("unsupported discovery client");
            }

            if (!state.Turns.TryGetValue(key, out var entry))
            {
                entry = new TurnState { Touched = now };
                state.Turns[key] = entry;
            }
            if (catalog && !string.IsNullOrEmpty(callId) && call is not null)
            {
                var progressed = false;
                switch (phase)
                {
                    case Phase.Before:
                        if (entry.Pending.Count < 64) entry.Pending.Add(call);
                        progressed = true;
                        break;
                    case Phase.After:
                        progressed = entry.Pending.Remove(call);
                        if (progressed) entry.Completed = true;
                        break;
                }
                // Codex's unmatched callbacks are not catalog progress and
                // must not extend its lost-callback deadline.
                if (client != "codex" || progressed) entry.Touched = now;
            }
            PruneTurns(state, now);
            return entry.Completed || now - entry.Touched >= NoProgressSeconds;
        });
    }

    private double Now() => _now();

    private T WithState<T>(Func<State, T> update)
    {
        EnsureDirectory();
        using var lockFile = AcquireLock();
        var original = ReadState();
        State state;
        try
        {
            state = original.Length == 0
                ? new State()
                : JsonSerializer.Deserialize<State>(original, JsonOptions) ?? new State();
        }
        catch (JsonException)
        {
            throw new FailureException(Failure.Invalid);
        }
        var result = update(state);
        var now = Now();
        PruneGenerations(state.GrokTurns, now);
        PruneGenerations(state.GrokCalls, now);
        PruneGenerations(state.ClaudeTurns, now);
        PruneGenerations(state.ClaudeCalls, now);
        PruneTurns(state, now);
        byte[] next;
        try
        {
            next = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        }
        catch (Exception error) when (error is JsonException or NotSupportedException)
        {
            throw new FailureException(Failure.Invalid);
        }
        if (next.Length > MaximumStateBytes) throw new FailureException(Failure.Invalid);
        if (!next.AsSpan().SequenceEqual(original)) WriteState(next);
        return result;
    }

    private void EnsureDirectory()
    {
        if (File.Exists(Directory)) throw new FailureException(Failure.Unsafe);
        if (!global::System.IO.Directory.Exists(Directory)) global::System.IO.Directory.CreateDirectory(Directory);
        if (IsReparsePoint(Directory)) throw new FailureException(Failure.Unsafe);
        if (!BrokerAcl.IsPrivateDirectory(Directory))
        {
            try { BrokerAcl.SecureDirectory(Directory); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                throw new FailureException(Failure.Unsafe);
            }
        }
        if (!BrokerAcl.IsPrivateDirectory(Directory)) throw new FailureException(Failure.Unsafe);
    }

    private FileStream AcquireLock()
    {
        var path = Path.Combine(Directory, "lock");
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(200);
        while (true)
        {
            try
            {
                var stream = new FileStream(
                    path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
                try
                {
                    if (!IsPrivateFile(path)) BrokerAcl.SecureFile(path);
                    return stream;
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(5);
            }
            catch (IOException)
            {
                throw new FailureException(Failure.Busy);
            }
            catch (UnauthorizedAccessException)
            {
                throw new FailureException(Failure.Unsafe);
            }
        }
    }

    private byte[] ReadState()
    {
        var path = Path.Combine(Directory, "state.json");
        if (!File.Exists(path)) return Array.Empty<byte>();
        if (IsReparsePoint(path) || !IsPrivateFile(path)) throw new FailureException(Failure.Unsafe);
        var length = new FileInfo(path).Length;
        if (length > MaximumStateBytes) throw new FailureException(Failure.Invalid);
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > MaximumStateBytes) throw new FailureException(Failure.Invalid);
            return bytes;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new FailureException(Failure.Unsafe);
        }
    }

    private void WriteState(byte[] bytes)
    {
        var name = ".pending-" + Guid.NewGuid().ToString("N");
        var temporary = Path.Combine(Directory, name);
        try
        {
            using (var stream = new FileStream(
                       temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            BrokerAcl.SecureFile(temporary);
            File.Move(temporary, Path.Combine(Directory, "state.json"), overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new FailureException(Failure.Unsafe);
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void PruneTurns(State state, double now)
    {
        state.Turns = state.Turns
            .Where(pair => now - pair.Value.Touched < DaySeconds)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (state.Turns.Count <= 256) return;
        foreach (var key in state.Turns.OrderBy(pair => pair.Value.Touched).Take(state.Turns.Count - 256)
                     .Select(pair => pair.Key).ToArray())
        {
            state.Turns.Remove(key);
        }
    }

    private static void PruneGenerations(Dictionary<string, Generation>? entries, double now)
    {
        if (entries is null) return;
        foreach (var key in entries.Where(pair => now - pair.Value.Touched >= DaySeconds)
                     .Select(pair => pair.Key).ToArray())
        {
            entries.Remove(key);
        }
        if (entries.Count <= 512) return;
        foreach (var key in entries.OrderBy(pair => pair.Value.Touched).Take(entries.Count - 512)
                     .Select(pair => pair.Key).ToArray())
        {
            entries.Remove(key);
        }
    }

    private static bool IsPrivateFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return false;
        FileSecurity security;
        try { security = info.GetAccessControl(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        if (!security.AreAccessRulesProtected) return false;
        var current = BrokerAcl.CurrentUser;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (!rule.IdentityReference.Equals(current)) return false;
        }
        return true;
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            return false;
        }
    }

    private static string Hash(params string[] components)
        => Convert.ToHexStringLower(
            global::System.Security.Cryptography.SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(components)));

    private static void Validate(string value)
    {
        if (value.Length == 0 || global::System.Text.Encoding.UTF8.GetByteCount(value) > 4096)
        {
            throw new FailureException(Failure.Invalid);
        }
    }

    internal sealed class FailureException : Exception
    {
        public FailureException(Failure failure) : base(failure.ToString()) => Error = failure;

        public Failure Error { get; }
    }
}

