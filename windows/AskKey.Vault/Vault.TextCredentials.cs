using System.Security.Cryptography;
using System.Text;
using AskKey.Broker.Approval;

namespace AskKey.Vault;

public sealed partial class Vault
{
    internal T PerformCredentialCreation<T>(ManagementAuthenticator authenticator, Func<T> create)
    {
        _ = RequireKey();
        var wasPaused = AgentAccessGate.BeginExclusiveChange();
        try
        {
            var isFirst = Store.FetchAllCredentials().Count == 0
                && Store.FetchRecycledCredentials().Count == 0;
            if (!isFirst)
            {
                RequireManagementSession();
                AuthorizeManagement(authenticator, CredentialManagementCopy.ManageReason);
            }
            var created = create();
            NotifySnapshotRelevantChange();
            return created;
        }
        finally
        {
            AgentAccessGate.EndExclusiveChange(paused: wasPaused);
        }
    }

    internal T PerformCredentialMutation<T>(string id, Func<T> mutation)
    {
        var deliveryManager = FileDeliveryManager.Get();
        var wasPaused = AgentAccessGate.BeginExclusiveChange();
        try
        {
            var result = mutation();
            BrokerRequests.CancelPending(id);
            ApprovalRequests.CancelPending(id);
            deliveryManager.Revoke(id);
            NotifySnapshotRelevantChange();
            return result;
        }
        finally
        {
            AgentAccessGate.EndExclusiveChange(paused: wasPaused);
        }
    }

    public ManagedTextCredential CreateTextCredential(TextCredentialInput input,
        ManagementAuthenticator authenticator)
        => PerformCredentialCreation(authenticator, () =>
        {
            var key = RequireKey();
            var prepared = PreparedRecord(input, Guid.NewGuid().ToString(), key, existing: null);
            Store.InsertCredential(prepared);
            return ManagedCredential(prepared, key, includeSecrets: false);
        });

    public void UpdateCredentialPermission(string id, CredentialPermission permission,
        ManagementAuthenticator authenticator)
    {
        RequireManagementSession();
        AuthorizeManagement(authenticator, CredentialManagementCopy.ManageReason);
        _ = RequireKey();
        PerformCredentialMutation(id, () =>
        {
            var existing = Store.FetchCredential(id) ?? throw VaultException.NotFound(id);
            existing.Permission = permission.WireValue();
            existing.UpdatedAt = Timestamp(CurrentDate);
            Store.UpdateCredential(existing);
            return 0;
        });
    }

    public void UpdateCredentialMetadata(string id, string name, string usageInstructions, string? groupName,
        CredentialPermission permission, DateTimeOffset? expiresAt, ManagementAuthenticator authenticator)
    {
        RequireManagementSession();
        AuthorizeManagement(authenticator, CredentialManagementCopy.ManageReason);
        var key = RequireKey();
        var displayName = CredentialName.DisplayName(name);
        var nameIndex = CredentialIndex.Hash(CredentialName.Normalized(displayName), key);
        CredentialFieldValidation.UsageInstructions(usageInstructions);
        var group = CredentialName.OptionalDisplayName(groupName);
        PerformCredentialMutation(id, () =>
        {
            var existing = Store.FetchCredential(id) ?? throw VaultException.NotFound(id);
            if (Store.FetchCredentialIncludingRecycledByNameIndex(nameIndex) is { } conflict && conflict.Id != id)
            {
                throw VaultException.NameConflict(displayName);
            }
            existing.NameIndex = nameIndex;
            existing.EncryptedDisplayName = VaultCrypto.Encrypt(displayName, key);
            existing.EncryptedUsageInstructions = VaultCrypto.Encrypt(usageInstructions, key);
            existing.EncryptedGroupName = group is null ? null : VaultCrypto.Encrypt(group, key);
            existing.Permission = permission.WireValue();
            existing.ExpiresAt = expiresAt is { } expiry ? Timestamp(expiry) : null;
            existing.UpdatedAt = Timestamp(CurrentDate);
            Store.UpdateCredential(existing);
            return 0;
        });
    }

    public List<ManagedTextCredential> ListTextCredentials()
    {
        RequireManagementSession();
        var key = RequireKey();
        return Store.FetchAllCredentials()
            .Select(record => ManagedCredential(record, key, includeSecrets: false))
            .OrderBy(credential => credential.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public List<CredentialExpirySnapshot> ListCredentialExpirySnapshots()
        => Store.FetchAllCredentials()
            .Select(record => new CredentialExpirySnapshot(record.Id,
                record.ExpiresAt is null ? null : ParseExpiry(record.ExpiresAt)))
            .ToList();

    public int StoredCredentialCount() => CredentialCountForBootstrap();

    public ManagedTextCredential RevealTextCredential(string id, ManagementAuthenticator authenticator)
    {
        RequireManagementSession();
        AuthorizeManagement(authenticator, CredentialManagementCopy.RevealReason);
        var key = RequireKey();
        var record = Store.FetchCredential(id) ?? throw VaultException.NotFound(id);
        return ManagedCredential(record, key, includeSecrets: true);
    }

    public ManagedTextCredential UpdateTextCredential(string id, TextCredentialInput input,
        ManagementAuthenticator authenticator)
    {
        RequireManagementSession();
        AuthorizeManagement(authenticator, CredentialManagementCopy.ManageReason);
        var key = RequireKey();
        var prepared = PerformCredentialMutation(id, () =>
        {
            var existing = Store.FetchCredential(id) ?? throw VaultException.NotFound(id);
            var updated = PreparedRecord(input, existing.Id, key, existing);
            Store.UpdateCredential(updated);
            return updated;
        });
        return ManagedCredential(prepared, key, includeSecrets: false);
    }

    public void DeleteTextCredential(string id, ManagementAuthenticator authenticator)
    {
        RequireManagementSession();
        AuthorizeManagement(authenticator, CredentialManagementCopy.ManageReason);
        _ = RequireKey();
        PerformCredentialMutation(id, () =>
        {
            Store.RecycleCredential(id, Timestamp(CurrentDate));
            return 0;
        });
    }

    internal CredentialRecord PreparedRecord(TextCredentialInput input, string id, byte[] key,
        CredentialRecord? existing)
    {
        var displayName = CredentialName.DisplayName(input.Name);
        var nameIndex = CredentialIndex.Hash(CredentialName.Normalized(displayName), key);
        if (Store.FetchCredentialIncludingRecycledByNameIndex(nameIndex) is { } conflict && conflict.Id != id)
        {
            throw VaultException.NameConflict(displayName);
        }
        var group = CredentialName.OptionalDisplayName(input.GroupName);
        CredentialFieldValidation.UsageInstructions(input.UsageInstructions);
        var environmentVariable = OptionalEnvironmentVariable(input.EnvironmentVariable);
        var now = Timestamp(CurrentDate);
        return new CredentialRecord
        {
            Id = id,
            NameIndex = nameIndex,
            EncryptedDisplayName = VaultCrypto.Encrypt(displayName, key),
            EncryptedPayload = VaultCrypto.Encrypt(input.Value, key),
            EncryptedUsageInstructions = VaultCrypto.Encrypt(input.UsageInstructions, key),
            EncryptedPrivateNotes = VaultCrypto.Encrypt(input.PrivateNotes, key),
            EncryptedGroupName = group is null ? null : VaultCrypto.Encrypt(group, key),
            EncryptedEnvironmentVariable = environmentVariable is null
                ? null
                : VaultCrypto.Encrypt(environmentVariable, key),
            PayloadKind = CredentialPayloadKind.Text.WireValue(),
            Permission = input.Permission.WireValue(),
            ExpiresAt = input.ExpiresAt is { } expiry ? Timestamp(expiry) : null,
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now,
            DeletedAt = existing?.DeletedAt,
        };
    }

    internal ManagedTextCredential ManagedCredential(CredentialRecord record, byte[] key, bool includeSecrets)
    {
        var permission = CredentialPermissionExtensions.TryParse(record.Permission, out var parsedPermission)
            ? parsedPermission
            : CredentialPermission.Ask;
        var payloadKind = CredentialPayloadKindExtensions.TryParse(record.PayloadKind, out var parsedKind)
            ? parsedKind
            : CredentialPayloadKind.Text;
        var components = new List<ManagedCredentialComponent>();
        string? value = null;
        byte[]? fileBytes = null;
        string? originalFilename = null;
        string? privateNotes = null;
        if (payloadKind == CredentialPayloadKind.Bundle)
        {
            var decoded = CredentialComponentsCodec.Decode(record.EncryptedPayload is { Length: > 0 }
                ? VaultCrypto.DecryptData(record.EncryptedPayload, key)
                : Array.Empty<byte>());
            components.AddRange(decoded.Select(input => new ManagedCredentialComponent
            {
                Name = input.Name,
                Kind = input.Value.PayloadKind,
                Value = includeSecrets ? input.Value : null,
                Delivery = input.Delivery,
                Masked = input.Masked,
            }));
        }
        if (includeSecrets)
        {
            privateNotes = VaultCrypto.Decrypt(record.EncryptedPrivateNotes, key);
            if (payloadKind == CredentialPayloadKind.File)
            {
                var bytes = VaultCrypto.DecryptData(record.EncryptedPayload, key);
                if (record.ContentDigest is null
                    || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), record.ContentDigest))
                {
                    throw VaultException.File(FileCredentialIssue.DigestMismatch);
                }
                fileBytes = bytes;
                originalFilename = record.EncryptedOriginalFilename is null
                    ? null
                    : VaultCrypto.Decrypt(record.EncryptedOriginalFilename, key);
            }
            else if (payloadKind == CredentialPayloadKind.Text)
            {
                value = VaultCrypto.Decrypt(record.EncryptedPayload, key);
            }
        }
        return new ManagedTextCredential
        {
            Id = record.Id,
            Name = VaultCrypto.Decrypt(record.EncryptedDisplayName, key),
            Value = value,
            UsageInstructions = VaultCrypto.Decrypt(record.EncryptedUsageInstructions, key),
            PrivateNotes = privateNotes,
            GroupName = record.EncryptedGroupName is null ? null : VaultCrypto.Decrypt(record.EncryptedGroupName, key),
            EnvironmentVariable = record.EncryptedEnvironmentVariable is null
                ? null
                : VaultCrypto.Decrypt(record.EncryptedEnvironmentVariable, key),
            Permission = permission,
            ExpiresAt = record.ExpiresAt is null ? null : ParseExpiry(record.ExpiresAt),
            PayloadKind = payloadKind,
            OriginalFilename = originalFilename,
            ByteSize = record.ByteSize,
            ContentDigest = record.ContentDigest is null ? null : Convert.ToHexString(record.ContentDigest).ToLowerInvariant(),
            FileBytes = fileBytes,
            Components = components,
            DeletedAt = record.DeletedAt is null ? null : ParseExpiry(record.DeletedAt),
        };
    }

    internal List<CredentialComponentInput> CredentialComponents(CredentialRecord record, byte[] key)
    {
        var components = new List<CredentialComponentInput>();
        switch (CredentialPayloadKindExtensions.TryParse(record.PayloadKind, out var kind) ? kind : (CredentialPayloadKind?)null)
        {
            case CredentialPayloadKind.Bundle:
                components.AddRange(CredentialComponentsCodec.Decode(VaultCrypto.DecryptData(record.EncryptedPayload, key)));
                break;
            case CredentialPayloadKind.Text:
            {
                var variable = record.EncryptedEnvironmentVariable is null
                    ? null
                    : VaultCrypto.Decrypt(record.EncryptedEnvironmentVariable, key);
                components.Add(new CredentialComponentInput(
                    LegacyComponentName(variable, "VALUE"),
                    new CredentialComponentValue.Text(VaultCrypto.Decrypt(record.EncryptedPayload, key)),
                    variable is null ? CredentialComponentDelivery.None
                        : CredentialComponentDelivery.EnvironmentVariable(variable)));
                break;
            }
            case CredentialPayloadKind.File:
            {
                var bytes = VaultCrypto.DecryptData(record.EncryptedPayload, key);
                if (record.ContentDigest is null
                    || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), record.ContentDigest)
                    || record.EncryptedOriginalFilename is null)
                {
                    throw VaultException.File(FileCredentialIssue.DigestMismatch);
                }
                var variable = record.EncryptedEnvironmentVariable is null
                    ? null
                    : VaultCrypto.Decrypt(record.EncryptedEnvironmentVariable, key);
                components.Add(new CredentialComponentInput(
                    LegacyComponentName(variable, "FILE"),
                    new CredentialComponentValue.File(
                        VaultCrypto.Decrypt(record.EncryptedOriginalFilename, key), bytes),
                    variable is null ? CredentialComponentDelivery.None
                        : CredentialComponentDelivery.TemporaryFile(variable)));
                break;
            }
            default:
                throw VaultException.Database("Credential has an unknown payload kind.");
        }
        return CredentialBundleValidator.ValidatedComponents(components).ToList();
    }

    private static string LegacyComponentName(string? mapping, string fallback)
    {
        if (mapping is null) return fallback;
        try
        {
            return CredentialName.DisplayName(mapping);
        }
        catch (VaultException)
        {
            return fallback;
        }
    }

    internal static string Sha256Hex(string value) => Sha256Hex(Encoding.UTF8.GetBytes(value));
}
