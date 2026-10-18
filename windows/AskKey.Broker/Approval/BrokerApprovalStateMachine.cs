using System.Runtime.ExceptionServices;

namespace AskKey.Broker.Approval;

/// <summary>
/// Owns operation idempotency and approval state for the App's single Broker.
/// Mutable state is protected by <c>_lock</c>; callers never receive credential data.
/// Ported 1:1 from AskKey Sources/AskKeyBroker/BrokerApprovalStateMachine*.swift.
/// </summary>
public sealed partial class BrokerApprovalStateMachine : IDisposable
{
    private sealed class RuntimeAuthorization
    {
        public HashSet<string> CredentialIds { get; init; } = new(StringComparer.Ordinal);
        public DateTimeOffset ExpiresAt { get; init; }
        public bool Valid { get; set; } = true;
        public List<Action> Cleanups { get; } = new();
    }

    private sealed class Entry
    {
        public string? TrustedCredentialName;
        public BrokerApprovalOperationRequest Request = null!;
        public string RequestId = string.Empty;
        public string Capability = string.Empty;
        public DateTimeOffset ExpiresAt;
        public DateTimeOffset? CredentialExpiresAt;
        public BrokerRequestState State;
        public bool ApprovedByTimedAllowance;
        public int RetryCount;
        public bool Committing;

        /// <summary>Swift copies the struct out of the store; the copy keeps that on throw.</summary>
        public Entry Copy() => (Entry)MemberwiseClone();
    }

    private readonly object _lock = new();
    private readonly object _observerLock = new();
    // Serializes observer delivery the way Swift's serial observerQueue does, so a
    // blocked callback cannot let a newer count overtake an older one.
    private readonly object _deliveryLock = new();
    private readonly Timer _expirationTimer;
    private readonly object _timerGate = new();
    private bool _timerSuspended;

    private Func<BrokerAuthenticationPurpose, bool> _authenticate;
    private readonly Func<DateTimeOffset> _clock;
    private Action<BrokerPrivacyNotification> _notify;
    private Action<int> _pendingCountChanged;
    private Action<string> _timedAllowanceRevoked;
    private Action<string, BrokerApprovalOperationRequest?, BrokerRequestState> _operationStateChanged = (_, _, _) => { };
    private Action<string, BrokerApprovalOperationRequest?, BrokerRequestState> _operationStateSubscribers = (_, _, _) => { };
    private readonly TimeSpan _requestTtl;
    private TimeSpan _defaultTimedAllowance;
    private bool _readAuthenticationEnabled;
    private readonly Dictionary<string, Entry> _entriesByOperationId = new(StringComparer.Ordinal);
    private readonly List<string> _operationOrder = new();
    private readonly Dictionary<string, DateTimeOffset> _timedAllowances = new(StringComparer.Ordinal);
    // Active leases are independent of the bounded, evictable ticket history.
    private readonly Dictionary<Guid, RuntimeAuthorization> _runtimeAuthorizations = new();
    private int _reportedPendingCount;
    private ulong _stateRevision;
    private ulong _reportedRevision;
    private bool _hasScheduledExpiration;
    private bool _paused;

    /// <summary>System.Threading.Timer caps its due time; a clamped arm re-arms when it fires.</summary>
    private static readonly TimeSpan MaximumTimerDelay = TimeSpan.FromMilliseconds(int.MaxValue);

    public BrokerApprovalStateMachine(
        TimeSpan? requestTtl = null,
        TimeSpan? defaultTimedAllowance = null,
        bool readAuthenticationEnabled = true,
        Func<DateTimeOffset>? clock = null,
        Func<BrokerAuthenticationPurpose, bool>? authenticate = null,
        Action<BrokerPrivacyNotification>? notify = null,
        Action<int>? pendingCountChanged = null,
        Action<string>? timedAllowanceRevoked = null)
    {
        _requestTtl = requestTtl is { } ttl && ValidDuration(ttl) ? ttl : TimeSpan.FromMinutes(5);
        _defaultTimedAllowance = defaultTimedAllowance is { } allowance && ValidDuration(allowance)
            ? allowance
            : TimeSpan.FromMinutes(30);
        _readAuthenticationEnabled = readAuthenticationEnabled;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _authenticate = authenticate ?? (_ => false);
        _notify = notify ?? (_ => { });
        _pendingCountChanged = pendingCountChanged ?? (_ => { });
        _timedAllowanceRevoked = timedAllowanceRevoked ?? (_ => { });
        _expirationTimer = new Timer(_ => OnExpirationTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Dispose() => _expirationTimer.Dispose();

    /// <summary>Swift accepts any positive finite duration; TimeSpan is always finite. A duration that overflows DateTimeOffset arithmetic throws at use, so nothing is silently downgraded.</summary>
    internal static bool ValidDuration(TimeSpan value) => value > TimeSpan.Zero;

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left >= right ? left : right;

    private void OnExpirationTimer()
    {
        lock (_timerGate)
        {
            if (_timerSuspended)
            {
                // A one-shot Timer is consumed even when the callback returns early;
                // keep the seam's count honest so Resume can re-arm.
                lock (_lock) _hasScheduledExpiration = false;
                return;
            }
        }
        ExpireScheduledRequests();
    }

    /// <summary>Test seam: production reconciliation goes through <see cref="RefreshExpiration"/>.</summary>
    internal void SuspendExpirationTimer()
    {
        lock (_timerGate) _timerSuspended = true;
    }

    /// <summary>Test seam; see <see cref="SuspendExpirationTimer"/>.</summary>
    internal void ResumeExpirationTimer()
    {
        lock (_timerGate)
        {
            _timerSuspended = false;
            // A one-shot callback may have been consumed while suspended; rebuild
            // the arm from the current deadlines instead of waiting for a mutation.
            lock (_lock) RescheduleExpirationLocked(_clock());
        }
    }

    /// <summary>
    /// Runs one state mutation under the lock, then publishes counts, terminal
    /// transitions and runtime cleanups outside it. A throwing body still publishes
    /// the state it left behind, then rethrows.
    /// </summary>
    private TResult Mutate<TResult>(Func<TResult> body)
    {
        (ulong Revision, int Count) publication;
        List<(string OperationId, BrokerApprovalOperationRequest? Request, BrokerRequestState State)> transitions;
        List<Action> cleanups;
        var result = default(TResult);
        ExceptionDispatchInfo? failure = null;

        lock (_lock)
        {
            var schedulingNow = _clock();
            var previousEntries = SnapshotEntriesLocked();
            try
            {
                result = body();
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
            _stateRevision = unchecked(_stateRevision + 1);
            publication = PendingPublicationLocked();
            transitions = TerminalTransitionsLocked(previousEntries);
            cleanups = RuntimeInvalidationCleanupsLocked(schedulingNow);
            RescheduleExpirationLocked(schedulingNow);
        }

        foreach (var cleanup in cleanups) cleanup();
        PublishPendingCount(publication);
        PublishTerminalTransitions(transitions);
        failure?.Throw();
        return result!;
    }

    private void Mutate(Action body) => Mutate<object?>(() =>
    {
        body();
        return null;
    });

    private Dictionary<string, (BrokerRequestState State, BrokerApprovalOperationRequest Request)> SnapshotEntriesLocked()
    {
        var snapshot = new Dictionary<string, (BrokerRequestState, BrokerApprovalOperationRequest)>(
            _entriesByOperationId.Count, StringComparer.Ordinal);
        foreach (var (operationId, entry) in _entriesByOperationId)
        {
            snapshot[operationId] = (entry.State, entry.Request);
        }
        return snapshot;
    }

    private List<(string OperationId, BrokerApprovalOperationRequest? Request, BrokerRequestState State)>
        TerminalTransitionsLocked(
            Dictionary<string, (BrokerRequestState State, BrokerApprovalOperationRequest Request)> previousEntries)
    {
        var transitions = new List<(string, BrokerApprovalOperationRequest?, BrokerRequestState)>();
        foreach (var (operationId, previous) in previousEntries)
        {
            if (previous.State != BrokerRequestState.Pending && previous.State != BrokerRequestState.Approved) continue;
            if (!_entriesByOperationId.TryGetValue(operationId, out var entry))
            {
                // Retention can evict an entry after this mutation expires it.
                transitions.Add((operationId, previous.Request, BrokerRequestState.Expired));
                continue;
            }
            if (entry.State == BrokerRequestState.Pending || entry.State == BrokerRequestState.Approved) continue;
            transitions.Add((operationId, entry.Request, entry.State));
        }
        return transitions;
    }

    private (ulong Revision, int Count) PendingPublicationLocked()
    {
        var count = 0;
        foreach (var entry in _entriesByOperationId.Values)
        {
            if (entry.State == BrokerRequestState.Pending) count++;
        }
        return (_stateRevision, count);
    }
}
