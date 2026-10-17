using AskKey.Broker.Approval;

namespace AskKey.Broker.Tests;

/// <summary>Thread-safe injectable clock, mirroring the Swift TestClock.</summary>
internal sealed class TestClock
{
    private readonly object _lock = new();
    private DateTimeOffset _value;

    public TestClock(DateTimeOffset value) => _value = value;

    public DateTimeOffset Now
    {
        get
        {
            lock (_lock) return _value;
        }
    }

    public void Advance(TimeSpan interval)
    {
        lock (_lock) _value += interval;
    }
}

/// <summary>Mirrors Swift's NotificationRecorder.</summary>
internal sealed class NotificationRecorder
{
    private readonly object _lock = new();
    private readonly List<BrokerPrivacyNotification> _notifications = new();
    private readonly List<int> _counts = new();

    public IReadOnlyList<BrokerPrivacyNotification> Notifications
    {
        get
        {
            lock (_lock) return _notifications.ToArray();
        }
    }

    public IReadOnlyList<int> Counts
    {
        get
        {
            lock (_lock) return _counts.ToArray();
        }
    }

    public void Record(BrokerPrivacyNotification notification)
    {
        lock (_lock) _notifications.Add(notification);
    }

    public void Record(int count)
    {
        lock (_lock) _counts.Add(count);
    }
}

/// <summary>Blocks the first count callback until released, mirroring Swift's OrderedCountRecorder.</summary>
internal sealed class OrderedCountRecorder
{
    public readonly SemaphoreSlim FirstEntered = new(0);
    public readonly SemaphoreSlim ReleaseFirst = new(0);
    public readonly SemaphoreSlim SecondEntered = new(0);

    private readonly object _lock = new();
    private readonly List<int> _storage = new();

    public IReadOnlyList<int> Values
    {
        get
        {
            lock (_lock) return _storage.ToArray();
        }
    }

    public void Record(int count)
    {
        if (count == 1)
        {
            FirstEntered.Release();
            ReleaseFirst.Wait(TimeSpan.FromSeconds(5));
        }
        if (count == 2) SecondEntered.Release();
        lock (_lock) _storage.Add(count);
    }
}

/// <summary>Mirrors Swift's AuthenticationRecorder.</summary>
internal sealed class AuthenticationRecorder
{
    private readonly object _lock = new();
    private readonly List<BrokerAuthenticationPurpose> _storage = new();

    public IReadOnlyList<BrokerAuthenticationPurpose> Values
    {
        get
        {
            lock (_lock) return _storage.ToArray();
        }
    }

    public void Record(BrokerAuthenticationPurpose purpose)
    {
        lock (_lock) _storage.Add(purpose);
    }
}

/// <summary>Mirrors Swift's ExpirationObserverRecorder; the event signals on the first zero count.</summary>
internal sealed class ExpirationObserverRecorder
{
    private readonly object _lock = new();
    private readonly ManualResetEventSlim _expired = new(false);
    private readonly List<int> _counts = new();
    private readonly List<BrokerPrivacyNotification> _notifications = new();
    private bool _didFulfill;

    public ManualResetEventSlim Expired => _expired;

    public IReadOnlyList<int> Counts
    {
        get
        {
            lock (_lock) return _counts.ToArray();
        }
    }

    public IReadOnlyList<BrokerPrivacyNotification> Notifications
    {
        get
        {
            lock (_lock) return _notifications.ToArray();
        }
    }

    public void Record(int count)
    {
        bool shouldFulfill;
        lock (_lock)
        {
            _counts.Add(count);
            shouldFulfill = count == 0 && !_didFulfill;
            if (shouldFulfill) _didFulfill = true;
        }
        if (shouldFulfill) _expired.Set();
    }

    public void Record(BrokerPrivacyNotification notification)
    {
        lock (_lock) _notifications.Add(notification);
    }
}

/// <summary>Shared request builder, mirroring Swift's ApprovalStateMachineTestCase.request.</summary>
internal abstract class ApprovalStateMachineTestCase
{
    protected static BrokerApprovalOperationRequest Request(
        string operationId,
        string credentialId = "credential-1",
        BrokerApprovalOperation operation = BrokerApprovalOperation.Read,
        string? digest = null)
        => new(
            operationId: operationId,
            credentialId: credentialId,
            targetId: credentialId,
            operation: operation,
            payloadDigest: digest ?? new string('a', 64));
}
