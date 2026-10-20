namespace AskKey.Broker.Approval;

public sealed partial class BrokerApprovalStateMachine
{
    internal void WithRuntimeAuthorization(Guid id)
        => WithRuntimeAuthorization<object?>(id, static () => null);

    internal T WithRuntimeAuthorization<T>(Guid id, Func<T> body)
    {
        lock (_lock)
        {
            if (!_runtimeAuthorizations.TryGetValue(id, out var authorization)
                || !authorization.Valid
                || _paused
                || _clock() >= authorization.ExpiresAt)
            {
                throw new BrokerProviderException(BrokerProviderError.RequestRejected);
            }
            // No observer, file IO, or acquisition of the Vault gate while locked.
            return body();
        }
    }

    internal void RegisterRuntimeCleanup(Guid id, Action cleanup)
    {
        var registered = false;
        lock (_lock)
        {
            if (_runtimeAuthorizations.TryGetValue(id, out var authorization)
                && authorization.Valid
                && !_paused
                && _clock() < authorization.ExpiresAt)
            {
                authorization.Cleanups.Add(cleanup);
                registered = true;
            }
        }
        if (registered) return;
        // Invalidation already won: clean the resource now, outside the approval lock.
        cleanup();
        throw new BrokerProviderException(BrokerProviderError.RequestRejected);
    }

    internal void ReleaseRuntimeAuthorization(Guid id)
    {
        var cleanups = Mutate(() =>
        {
            if (_runtimeAuthorizations.Remove(id, out var authorization))
            {
                return authorization.Cleanups.ToList();
            }
            return new List<Action>();
        });
        foreach (var cleanup in cleanups) cleanup();
    }

    internal void ReleaseAbandonedRuntimeAuthorization(Guid id)
    {
        List<Action> cleanups;
        lock (_lock)
        {
            if (!_runtimeAuthorizations.Remove(id, out var authorization)) return;
            cleanups = authorization.Cleanups;
        }
        foreach (var cleanup in cleanups)
        {
            // A finalizer cannot surface failures; still attempt every registered cleanup.
            try { cleanup(); }
            catch { }
        }
    }

    private void InvalidateRuntimeAuthorizationsLocked(string? credentialId = null)
    {
        foreach (var authorization in _runtimeAuthorizations.Values)
        {
            if (credentialId is not null && !authorization.CredentialIds.Contains(credentialId)) continue;
            authorization.Valid = false;
        }
    }

    private List<Action> RuntimeInvalidationCleanupsLocked(DateTimeOffset now)
    {
        var cleanups = new List<Action>();
        foreach (var authorization in _runtimeAuthorizations.Values)
        {
            if (authorization.Valid && now < authorization.ExpiresAt) continue;
            authorization.Valid = false;
            cleanups.AddRange(authorization.Cleanups);
            authorization.Cleanups.Clear();
        }
        return cleanups;
    }
}
