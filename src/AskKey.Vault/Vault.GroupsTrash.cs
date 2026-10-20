using AskKey.Broker.Approval;

namespace AskKey.Vault;

public sealed partial class Vault
{
    public void UpdateCredentialGroup(string id, string? groupName, ManagementAuthenticator authenticator)
    {
        RequireManagementSession();
        AuthorizeManagement(authenticator, CredentialManagementCopy.ManageReason);
        var key = RequireKey();
        var group = CredentialName.OptionalDisplayName(groupName);
        PerformCredentialMutation(id, () =>
        {
            var existing = Store.FetchCredential(id) ?? throw VaultException.NotFound(id);
            existing.EncryptedGroupName = group is null ? null : VaultCrypto.Encrypt(group, key);
            existing.UpdatedAt = Timestamp(CurrentDate);
            Store.UpdateCredential(existing);
            return 0;
        });
    }

    public List<string> ListCredentialGroups()
    {
        RequireManagementSession();
        return CredentialGroupNames(RequireKey());
    }

    internal List<string> CredentialGroupNames(byte[] key)
    {
        var groups = new HashSet<string>(VaultGroups.StoredGroups(Store.ConfigValue(VaultGroups.ConfigKey), key),
            StringComparer.Ordinal);
        foreach (var record in Store.FetchAllCredentials().Concat(Store.FetchRecycledCredentials()))
        {
            if (record.EncryptedGroupName is { } encrypted)
            {
                groups.Add(VaultCrypto.Decrypt(encrypted, key));
            }
        }
        return groups.OrderBy(value => value, StringComparer.Ordinal).ToList();
    }

    public void CreateCredentialGroup(string rawName, ManagementAuthenticator authenticator)
    {
        RequireManagementSession();
        AuthorizeManagement(authenticator, CredentialManagementCopy.ManageReason);
        var key = RequireKey();
        var name = CredentialName.DisplayName(rawName);
        var wasPaused = AgentAccessGate.BeginExclusiveChange();
        try
        {
            _ = Store.EditCredentialGroup(name, key, deleting: false, Timestamp(CurrentDate));
            NotifySnapshotRelevantChange();
        }
        finally
        {
            AgentAccessGate.EndExclusiveChange(paused: wasPaused);
        }
    }

    public void DeleteCredentialGroup(string rawName, ManagementAuthenticator authenticator)
    {
        RequireManagementSession();
        AuthorizeManagement(authenticator, CredentialManagementCopy.ManageReason);
        var key = RequireKey();
        var name = CredentialName.DisplayName(rawName);
        var deliveryManager = FileDeliveryManager.Get();
        var wasPaused = AgentAccessGate.BeginExclusiveChange();
        try
        {
            var matchingIds = Store.EditCredentialGroup(name, key, deleting: true, Timestamp(CurrentDate));
            foreach (var id in matchingIds)
            {
                BrokerRequests.CancelPending(id);
                ApprovalRequests.CancelPending(id);
                deliveryManager.Revoke(id);
            }
            NotifySnapshotRelevantChange();
        }
        finally
        {
            AgentAccessGate.EndExclusiveChange(paused: wasPaused);
        }
    }

    public List<ManagedTextCredential> ListRecycledTextCredentials()
    {
        RequireManagementSession();
        var key = RequireKey();
        return Store.FetchRecycledCredentials()
            .Select(record => ManagedCredential(record, key, includeSecrets: false))
            .ToList();
    }

    public void RestoreRecycledTextCredential(string id, ManagementAuthenticator authenticator)
    {
        RequireManagementSession();
        AuthorizeManagement(authenticator, CredentialManagementCopy.ManageReason);
        _ = RequireKey();
        PerformRecycledCredentialMutation(id, () =>
        {
            Store.RestoreCredential(id);
            return 0;
        });
    }

    public void PermanentlyDeleteRecycledTextCredential(string id, ManagementAuthenticator authenticator)
    {
        RequireManagementSession();
        AuthorizeManagement(authenticator, CredentialManagementCopy.ManageReason);
        _ = RequireKey();
        PerformRecycledCredentialMutation(id, () =>
        {
            Store.DeleteRecycledCredential(id);
            return 0;
        });
    }

    public int PurgeRecycledTextCredentials(DateTimeOffset olderThan, ManagementAuthenticator authenticator)
    {
        RequireManagementSession();
        AuthorizeManagement(authenticator, CredentialManagementCopy.ManageReason);
        _ = RequireKey();
        var cutoff = olderThan - TimeSpan.FromDays(30);
        var removed = Store.PurgeRecycledCredentials(Timestamp(cutoff));
        if (removed > 0) NotifySnapshotRelevantChange();
        return removed;
    }

    public int PurgeExpiredRecycledCredentials()
    {
        var wasPaused = AgentAccessGate.BeginExclusiveChange();
        try
        {
            var cutoff = CurrentDate - TimeSpan.FromDays(30);
            var removed = Store.PurgeRecycledCredentials(Timestamp(cutoff));
            if (removed > 0) NotifySnapshotRelevantChange();
            return removed;
        }
        finally
        {
            AgentAccessGate.EndExclusiveChange(paused: wasPaused);
        }
    }

    private void PerformRecycledCredentialMutation(string id, Func<int> mutation)
    {
        var wasPaused = AgentAccessGate.BeginExclusiveChange();
        try
        {
            _ = mutation();
            BrokerRequests.CancelPending(id);
            ApprovalRequests.CancelPending(id);
            NotifySnapshotRelevantChange();
        }
        finally
        {
            AgentAccessGate.EndExclusiveChange(paused: wasPaused);
        }
    }
}
