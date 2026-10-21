using System.Security.Cryptography;

namespace AskKey.Guard;

/// <summary>32-byte one-time nonces, five-minute lifetime, four pending per SID.</summary>
public sealed class GuardNonceStore
{
    // Windows Hello prompts can stay open longer than a minute on slow devices.
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    public const int MaximumPendingPerSid = 4;

    private readonly object _gate = new();
    private readonly Dictionary<string, List<Entry>> _pending = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public GuardNonceStore(TimeProvider? timeProvider = null)
        => _time = timeProvider ?? TimeProvider.System;

    public byte[] Issue(string sid)
    {
        var nonce = RandomNumberGenerator.GetBytes(32);
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (!_pending.TryGetValue(sid, out var entries))
            {
                entries = new List<Entry>();
                _pending[sid] = entries;
            }
            Purge(entries, now);
            if (entries.Count >= MaximumPendingPerSid) entries.RemoveAt(0);
            entries.Add(new Entry(nonce, now + Lifetime));
        }
        return nonce;
    }

    public bool Consume(string sid, ReadOnlySpan<byte> nonce)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (!_pending.TryGetValue(sid, out var entries)) return false;
            for (var index = 0; index < entries.Count; index++)
            {
                if (!CryptographicOperations.FixedTimeEquals(entries[index].Nonce, nonce)) continue;
                var expiry = entries[index].ExpiresAt;
                entries.RemoveAt(index);
                if (entries.Count == 0) _pending.Remove(sid);
                return expiry > now;
            }
            Purge(entries, now);
            if (entries.Count == 0) _pending.Remove(sid);
            return false;
        }
    }

    private static void Purge(List<Entry> entries, DateTimeOffset now)
        => entries.RemoveAll(entry => entry.ExpiresAt <= now);

    private readonly record struct Entry(byte[] Nonce, DateTimeOffset ExpiresAt);
}
