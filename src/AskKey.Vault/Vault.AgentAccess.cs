using AskKey.Broker.Approval;
using AskKey.Broker.Protocol;

namespace AskKey.Vault;

public sealed partial class Vault
{
    internal const string AgentAccessPausedConfigKey = "agent_access_paused";

    /// <summary>
    /// Writes the authenticated policy. Only the vault, only with the unlocked key, so a
    /// hand-edited database or settings.json cannot reach these values.
    /// </summary>
    private void PersistPolicy(AgentAccessPolicy policy, byte[] key)
    {
        if (!AgentAccessPolicy.TryWrite(policy, Store.SetConfigValue, key))
            throw VaultException.Database("Could not save the security policy.");
        lock (_stateLock) _policy = policy;
    }

    /// <summary>Applies the timed-allowance window from the authenticated policy.</summary>
    public void UpdateDefaultTimedAllowanceMinutes(int minutes)
    {
        var clamped = minutes is >= 1 and <= AgentAccessPolicy.MaximumTimedAllowanceMinutes
            ? minutes
            : AgentAccessPolicy.FallbackTimedAllowanceMinutes;
        ApprovalRequests.UpdateDefaultTimedAllowance(TimeSpan.FromSeconds(clamped * 60));
        NotifySnapshotRelevantChange();
    }

    /// <summary>
    /// Persists the security-relevant settings the UI confirmed. Values outside the range
    /// Settings can produce fall back to the strict defaults rather than being stored.
    /// </summary>
    public void SaveSecuritySettings(
        ManagementAuthenticator authenticator,
        bool readApprovalAuthenticationEnabled,
        bool timedAllowanceEnabled,
        int defaultTimedAllowanceMinutes,
        int sessionTimeoutSeconds)
    {
        AuthorizeManagement(authenticator, CredentialManagementCopy.ManageReason);
        var key = RequireKey();
        var policy = EffectivePolicy.Copy();
        policy.ReadApprovalAuthenticationEnabled = readApprovalAuthenticationEnabled;
        policy.TimedAllowanceEnabled = timedAllowanceEnabled;
        policy.DefaultTimedAllowanceMinutes =
            defaultTimedAllowanceMinutes is >= 1 and <= AgentAccessPolicy.MaximumTimedAllowanceMinutes
                ? defaultTimedAllowanceMinutes
                : AgentAccessPolicy.FallbackTimedAllowanceMinutes;
        policy.SessionTimeoutSeconds =
            sessionTimeoutSeconds is >= 60 and <= AgentAccessPolicy.MaximumSessionTimeoutSeconds
                ? sessionTimeoutSeconds
                : AgentAccessPolicy.DefaultSessionTimeoutSeconds;
        PersistPolicy(policy, key);

        ApprovalRequests.SetReadAuthenticationEnabled(policy.ReadApprovalAuthenticationEnabled);
        UpdateDefaultTimedAllowanceMinutes(policy.DefaultTimedAllowanceMinutes);
        SetManagementSessionIdleLimit(TimeSpan.FromSeconds(policy.SessionTimeoutSeconds));
    }

    public void ValidateStoreAvailability() => _ = BootstrapState();

    public VaultBootstrapState BootstrapState() => VaultBootstrap.State(BootstrapPaths);

    public int CredentialCountForBootstrap()
    {
        VaultStore? current;
        lock (_stateLock) current = _store;
        if (current is not null)
        {
            return checked((int)current.ScalarLong("SELECT COUNT(*) FROM credentials;"));
        }
        return VaultBootstrap.CredentialCount(BootstrapPaths);
    }

    internal void ConfigureAgentTextWriteCleanup()
    {
        ApprovalRequests.AddOperationStateChanged((operationId, request, state) =>
        {
            if (state is BrokerRequestState.Pending or BrokerRequestState.Approved) return;
            if (request?.Operation == BrokerApprovalOperation.Organize)
            {
                AgentOrganizations.Remove(operationId);
                if (request is not null
                    && state is BrokerRequestState.Denied or BrokerRequestState.Cancelled or BrokerRequestState.Expired)
                {
                    RecordAgentOrganizationAccess(request,
                        state == BrokerRequestState.Expired
                            ? CredentialAccessResult.Expired
                            : CredentialAccessResult.Denied);
                }
                return;
            }
            var frozen = AgentTextWrites.Entry(operationId);
            AgentTextWrites.Remove(operationId);
            var approvalRequest = request ?? frozen?.ApprovalRequest;
            if (approvalRequest is null
                || state is not (BrokerRequestState.Denied or BrokerRequestState.Cancelled or BrokerRequestState.Expired))
            {
                return;
            }
            var operation = approvalRequest.Operation switch
            {
                BrokerApprovalOperation.Create => CredentialAccessOperation.Create,
                BrokerApprovalOperation.Modify => CredentialAccessOperation.Modify,
                BrokerApprovalOperation.Organize => CredentialAccessOperation.Modify,
                BrokerApprovalOperation.Delete => CredentialAccessOperation.Delete,
                _ => CredentialAccessOperation.RuntimeRead,
            };
            RecordCredentialAccess(CredentialAccessEvent.Create(CurrentDate, approvalRequest.CredentialId,
                operation,
                state == BrokerRequestState.Expired ? CredentialAccessResult.Expired : CredentialAccessResult.Denied,
                approvalRequest.CallerName, approvalRequest.CallerPurpose,
                operation == CredentialAccessOperation.RuntimeRead
                    ? approvalRequest.DisplayValue?.ExecutableBasename
                    : null));
        });
    }

    internal void SynchronizeAgentAccessState()
    {
        // Pause state comes from the authenticated policy, not from a plain config row. A
        // missing or unverifiable record reads as paused, so deleting the row pauses Agent
        // access instead of granting it.
        var paused = EffectivePolicy.AgentAccessPaused;
        AgentAccessGate.Synchronize(paused);
        if (paused)
        {
            BrokerRequests.PauseAndCancelAll();
            ApprovalRequests.PauseAndCancelAll();
        }
        else
        {
            BrokerRequests.Resume();
            ApprovalRequests.Resume();
        }
    }

    public bool IsAgentAccessPaused() => AgentAccessGate.IsPaused();

    public void PauseAgentAccess(ManagementAuthenticator authenticator)
    {
        AuthorizeManagement(authenticator, CredentialManagementCopy.PauseReason);
        var key = RequireKey();
        var wasPaused = AgentAccessGate.BeginExclusiveChange();
        try
        {
            Store.SetConfigValue(AgentAccessPausedConfigKey, null);
            var policy = EffectivePolicy.Copy();
            policy.AgentAccessPaused = true;
            PersistPolicy(policy, key);
            BrokerRequests.PauseAndCancelAll();
            ApprovalRequests.PauseAndCancelAll();
            CleanupRuntimeFileDeliveries();
            AgentAccessGate.EndExclusiveChange(paused: true);
        }
        catch
        {
            AgentAccessGate.EndExclusiveChange(paused: wasPaused);
            throw;
        }
    }

    public void ResumeAgentAccess(ManagementAuthenticator authenticator)
    {
        AuthorizeManagement(authenticator, CredentialManagementCopy.ResumeReason);
        var key = RequireKey();
        var wasPaused = AgentAccessGate.BeginExclusiveChange();
        try
        {
            Store.SetConfigValue(AgentAccessPausedConfigKey, null);
            var policy = EffectivePolicy.Copy();
            policy.AgentAccessPaused = false;
            PersistPolicy(policy, key);
            BrokerRequests.Resume();
            ApprovalRequests.Resume();
            AgentAccessGate.EndExclusiveChange(paused: false);
        }
        catch
        {
            AgentAccessGate.EndExclusiveChange(paused: wasPaused);
            throw;
        }
    }

    public void CleanupRuntimeFileDeliveries() => FileDeliveryManager.CleanupAll();

    public bool HasRuntimeFileCleanupFailures => FileDeliveryManager.HasFailures;

    public AgentCredentialAuthorization AuthorizeAgentCredential(string rawName, AgentCredentialOperation operation,
        BrokerCallerClaim caller, DateTimeOffset? now = null)
    {
        AgentAccessGate.BeginAgentOperation();
        try
        {
            _ = caller;
            var key = RequireKey();
            string displayName;
            try
            {
                displayName = CredentialName.DisplayName(rawName);
            }
            catch (VaultException)
            {
                throw VaultException.Unavailable();
            }
            var nameIndex = CredentialIndex.Hash(CredentialName.Normalized(displayName), key);
            var record = Store.FetchCredentialByNameIndex(nameIndex);
            if (record is null
                || !CredentialPermissionExtensions.TryParse(record.Permission, out var permission)
                || permission == CredentialPermission.Hidden)
            {
                throw VaultException.Unavailable();
            }
            if (record.ExpiresAt is { } storedExpiry)
            {
                var expiry = ParseExpiry(storedExpiry);
                if (expiry <= (now ?? CurrentDate))
                {
                    ApprovalRequests.CancelPending(record.Id);
                    throw VaultException.Unavailable();
                }
            }
            if (operation != AgentCredentialOperation.Read) return AgentCredentialAuthorization.RequiresApproval;
            return permission == CredentialPermission.Allowed
                ? AgentCredentialAuthorization.Allowed
                : AgentCredentialAuthorization.RequiresApproval;
        }
        finally
        {
            AgentAccessGate.EndAgentOperation();
        }
    }
}
