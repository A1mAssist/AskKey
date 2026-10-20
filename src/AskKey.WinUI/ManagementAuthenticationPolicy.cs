using AskKey.Vault;

namespace AskKeyApp;

internal static class ManagementAuthenticationPolicy
{
    public static bool RequiresConfirmation(bool hasActiveSession, string reason)
        => !hasActiveSession || Vault.RequiresFreshAuthentication(reason);

    public static ManagementAuthenticator From(Func<string, bool> confirm, Func<bool> hasActiveSession)
    {
        // The authenticator is shared by the UI thread and Broker worker threads,
        // so the reuse cache needs its own lock.
        var confirmed = new HashSet<string>(StringComparer.Ordinal);
        var gate = new object();
        return new(reason =>
        {
            var active = hasActiveSession();
            var required = RequiresConfirmation(active, reason);
            if (!required) return true;
            // A confirmation is reusable only for the exact operation reason and only
            // while the management session that it opened is still valid.
            if (active && IsCached(reason)) return true;
            var result = confirm(reason);
            if (result) Cache(reason);
            return result;
        });

        bool IsCached(string reason)
        {
            lock (gate) return confirmed.Contains(reason);
        }

        void Cache(string reason)
        {
            lock (gate) confirmed.Add(reason);
        }
    }
}