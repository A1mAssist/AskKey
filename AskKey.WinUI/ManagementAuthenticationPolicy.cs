using AskKey.Vault;

namespace AskKeyApp;

internal static class ManagementAuthenticationPolicy
{
    public static bool RequiresConfirmation(bool hasActiveSession, string reason)
        => !hasActiveSession || Vault.RequiresFreshAuthentication(reason);

    public static ManagementAuthenticator From(Func<string, bool> confirm, Func<bool> hasActiveSession)
    {
        var confirmed = new HashSet<string>(StringComparer.Ordinal);
        return new(reason =>
        {
            var active = hasActiveSession();
            var required = RequiresConfirmation(active, reason);
            if (!required) return true;
            // A confirmation is reusable only for the exact operation reason and only
            // while the management session that it opened is still valid.
            if (active && confirmed.Contains(reason)) return true;
            var result = confirm(reason);
            if (result) confirmed.Add(reason);
            return result;
        });
    }
}