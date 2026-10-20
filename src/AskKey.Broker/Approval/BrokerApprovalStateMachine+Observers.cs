namespace AskKey.Broker.Approval;

public sealed partial class BrokerApprovalStateMachine
{
    public void ConfigureObservers(
        Action<BrokerPrivacyNotification> notify,
        Action<int> pendingCountChanged)
    {
        lock (_observerLock)
        {
            _notify = notify;
            _pendingCountChanged = pendingCountChanged;
            _reportedPendingCount = 0;
            _reportedRevision = 0;
        }
        (ulong Revision, int Count) publication;
        lock (_lock)
        {
            publication = PendingPublicationLocked();
        }
        PublishPendingCount(publication);
    }

    public void ConfigureOperationStateChanged(
        Action<string, BrokerApprovalOperationRequest?, BrokerRequestState> operationStateChanged)
    {
        lock (_observerLock)
        {
            _operationStateChanged = operationStateChanged;
        }
    }

    public void AddOperationStateChanged(
        Action<string, BrokerApprovalOperationRequest?, BrokerRequestState> operationStateChanged)
    {
        lock (_observerLock)
        {
            _operationStateSubscribers += operationStateChanged;
        }
    }

    /// <summary>Test seam. Counts and notifications are delivered synchronously, so
    /// there is never an observer delivery in flight to drain.</summary>
    internal void FlushObservers()
    {
    }

    private void PublishTerminalTransitions(
        List<(string OperationId, BrokerApprovalOperationRequest? Request, BrokerRequestState State)> transitions)
    {
        if (transitions.Count == 0) return;
        Action<string, BrokerApprovalOperationRequest?, BrokerRequestState> operationStateChanged;
        lock (_observerLock)
        {
            operationStateChanged = _operationStateSubscribers + _operationStateChanged;
        }
        foreach (var (operationId, request, state) in transitions)
        {
            operationStateChanged(operationId, request, state);
        }
    }

    // shortcut: observer callbacks run on the mutating thread instead of a serial
    // queue, so a blocking callback stalls mutations; move delivery to a pump if a
    // production callback can block.
    private void PublishPendingCount((ulong Revision, int Count) publication)
    {
        lock (_deliveryLock)
        {
            DeliverPendingCount(publication);
        }
    }

    private void DeliverPendingCount((ulong Revision, int Count) publication)
    {
        Action<int> pendingCountChanged;
        Action<BrokerPrivacyNotification> notify;
        bool shouldNotify;
        bool countChanged;
        lock (_observerLock)
        {
            // Guards against an older revision publishing after a newer one.
            if (publication.Revision <= _reportedRevision) return;
            _reportedRevision = publication.Revision;
            shouldNotify = _reportedPendingCount == 0 && publication.Count > 0;
            countChanged = _reportedPendingCount != publication.Count;
            _reportedPendingCount = publication.Count;
            notify = _notify;
            pendingCountChanged = _pendingCountChanged;
        }
        if (countChanged) pendingCountChanged(publication.Count);
        if (shouldNotify) notify(BrokerPrivacyNotification.ApprovalQueueBecameNonempty);
    }
}
