using AskKey.Vault;

namespace AskKeyApp;

internal static class ManagementAuthenticationPolicy
{
    public static bool RequiresConfirmation(bool hasActiveSession, string reason)
        => !hasActiveSession || Vault.RequiresFreshAuthentication(reason);

    /// <summary>
    /// A read approval inherits the open management session: the user already proved
    /// presence with Windows Hello when they unlocked, and the session window is the
    /// reuse period they chose. A write approval always asks again, because it hands
    /// an Agent plaintext or grants a lasting permission.
    /// </summary>
    public static bool RequiresApprovalConfirmation(
        bool hasActiveSession, AskKey.Broker.Approval.BrokerAuthenticationPurpose purpose)
        => purpose != AskKey.Broker.Approval.BrokerAuthenticationPurpose.ReadApproval
            || !hasActiveSession;

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