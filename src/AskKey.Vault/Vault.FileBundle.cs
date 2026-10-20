using System.Security.Cryptography;
using System.Text;
using AskKey.Broker.Approval;
using AskKey.Broker.FileWrite;
using AskKey.Broker.Protocol;

namespace AskKey.Vault;

public sealed partial class Vault
{
    public string BrokerFileContentDigest(string credentialId, DateTimeOffset? now = null)
    {
        AgentAccessGate.BeginAgentOperation();
        try
        {
            return CurrentFileContentDigest(credentialId, now ?? CurrentDate);
        }
        finally
        {
            AgentAccessGate.EndAgentOperation();
        }
    }

    public string NormalizeAgentCreateCredentialName(string raw) => CredentialName.DisplayName(raw);

    public BrokerApprovalTicket SubmitFileWriteApprovalIfCurrent(string credentialId,
        string? expectedPreviousDigest, BrokerApprovalOperationRequest request)
        => SubmitFileWriteApprovalIfCurrent(credentialId, expectedPreviousDigest, request, beforeSubmit: () => { });

    internal BrokerApprovalTicket SubmitFileWriteApprovalIfCurrent(string credentialId,
        string? expectedPreviousDigest, BrokerApprovalOperationRequest request, Action beforeSubmit)
    {
        AgentAccessGate.BeginAgentOperation();
        try
        {
            if (request.CredentialId != credentialId)
            {
                throw new BrokerApprovalException(BrokerApprovalError.InvalidRequest);
            }
            string? currentDigest;
            string? trustedCredentialName;
            switch (request.Operation)
            {
                case BrokerApprovalOperation.Modify:
                    currentDigest = CurrentFileContentDigest(credentialId, CurrentDate);
                    var key = RequireKey();
                    var record = Store.FetchCredential(credentialId) ?? throw VaultException.Unavailable();
                    trustedCredentialName = VaultCrypto.Decrypt(record.EncryptedDisplayName, key);
                    break;
                case BrokerApprovalOperation.Create:
                    currentDigest = null;
                    trustedCredentialName = null;
                    break;
                default:
                    throw new BrokerApprovalException(BrokerApprovalError.InvalidRequest);
            }
            if (!EqualDigest(currentDigest, expectedPreviousDigest))
            {
                throw new BrokerFileWriteException(BrokerFileWriteError.TargetChanged);
            }
            beforeSubmit();
            return ApprovalRequests.Submit(request, BrokerCredentialDeadline.None, trustedCredentialName);
        }
        finally
        {
            AgentAccessGate.EndAgentOperation();
        }
    }

    private string CurrentFileContentDigest(string credentialId, DateTimeOffset now)
    {
        var record = Store.FetchCredential(credentialId);
        if (record is null
            || record.Permission == CredentialPermission.Hidden.WireValue()
            || record.PayloadKind != CredentialPayloadKind.File.WireValue()
            || record.ContentDigest is null)
        {
            throw VaultException.Unavailable();
        }
        if (record.ExpiresAt is { } storedExpiry && ParseExpiry(storedExpiry) <= now)
        {
            throw VaultException.Unavailable();
        }
        return Convert.ToHexString(record.ContentDigest).ToLowerInvariant();
    }

    public bool CompletedAgentFileWrite(string requestId, string capability, string expectedDigest)
    {
        _ = DigestData(expectedDigest);
        var receipt = ValidatedAgentFileWriteReceipt(requestId, capability);
        if (receipt is null) return false;
        if (receipt.ResultDigest != expectedDigest)
        {
            throw new BrokerApprovalException(BrokerApprovalError.RequestNotFound);
        }
        return true;
    }

    public BrokerRequestState? CommittedAgentFileWriteStatus(string requestId, string capability)
        => ValidatedAgentFileWriteReceipt(requestId, capability) is null ? null : BrokerRequestState.Completed;

    private AgentWriteOperationRecord? ValidatedAgentFileWriteReceipt(string requestId, string capability)
    {
        if (requestId.Length == 0 || capability.Length == 0
            || Encoding.UTF8.GetByteCount(requestId) > BrokerLimits.MaximumFieldBytes
            || Encoding.UTF8.GetByteCount(capability) > BrokerLimits.MaximumFieldBytes)
        {
            throw new BrokerApprovalException(BrokerApprovalError.InvalidRequest);
        }
        _ = RequireKey();
        var receipt = Store.FetchAgentFileWriteReceipt(requestId);
        if (receipt is null || receipt.ResultDigest is null) return null;
        if (receipt.CapabilityDigest != FileWriteCapabilityDigest(capability)
            || receipt.Operation != BrokerApprovalOperation.Create.WireOperation()
                && receipt.Operation != BrokerApprovalOperation.Modify.WireOperation())
        {
            throw new BrokerApprovalException(BrokerApprovalError.RequestNotFound);
        }
        return receipt;
    }

    public void CommitAgentFileWrite(BrokerFrozenFile frozen)
    {
        var accessOperation = frozen.Operation switch
        {
            BrokerApprovalOperation.Create => CredentialAccessOperation.Create,
            BrokerApprovalOperation.Modify => CredentialAccessOperation.Modify,
            _ => throw new BrokerApprovalException(BrokerApprovalError.InvalidRequest),
        };
        if (string.IsNullOrEmpty(frozen.ApprovalRequestId) || string.IsNullOrEmpty(frozen.ApprovalCapability))
        {
            throw new BrokerApprovalException(BrokerApprovalError.InvalidRequest);
        }
        var requestId = frozen.ApprovalRequestId;
        var capability = frozen.ApprovalCapability;
        var succeeded = false;
        try
        {
            if (frozen.ByteCount != frozen.Bytes.Length)
            {
                throw VaultException.File(FileCredentialIssue.DigestMismatch);
            }
            var contentDigest = SHA256.HashData(frozen.Bytes);
            if (!contentDigest.AsSpan().SequenceEqual(DigestData(frozen.Digest)))
            {
                throw VaultException.File(FileCredentialIssue.DigestMismatch);
            }
            var wasPaused = AgentAccessGate.BeginExclusiveAgentChange();
            try
            {
                var digest = frozen.ApprovalPayloadDigest;
                var capabilityDigest = FileWriteCapabilityDigest(capability);
                var existingReceipt = Store.FetchAgentWriteOperation(frozen.OperationId);
                if (existingReceipt is not null)
                {
                    if (existingReceipt.PayloadDigest != digest
                        || existingReceipt.CredentialId != frozen.CredentialId
                        || existingReceipt.Operation != frozen.Operation.WireOperation()
                        || existingReceipt.ResultDigest != frozen.Digest)
                    {
                        throw new BrokerApprovalException(BrokerApprovalError.PayloadMismatch);
                    }
                    if (existingReceipt.RequestId != requestId || existingReceipt.CapabilityDigest != capabilityDigest)
                    {
                        throw new BrokerApprovalException(BrokerApprovalError.RequestNotFound);
                    }
                    succeeded = true;
                    return;
                }
                var approval = new BrokerApprovalOperationRequest(frozen.OperationId, frozen.CredentialId,
                    frozen.TargetId, frozen.Operation, digest);
                if (ApprovalRequests.Status(requestId, capability, approval) != BrokerRequestState.Approved)
                {
                    throw new BrokerApprovalException(BrokerApprovalError.InvalidDecision);
                }
                var key = RequireKey();
                FrozenAgentTextMutation mutation;
                byte[]? expectedFileDigest;
                switch (frozen.Operation)
                {
                    case BrokerApprovalOperation.Create:
                        if (frozen.PreviousDigest is not null)
                        {
                            throw VaultException.Changed();
                        }
                        var prepared = PreparedFileRecord(new FileCredentialInput
                        {
                            Name = frozen.TargetId,
                            Snapshot = new FileImport.FrozenFile(frozen.OriginalFilename, frozen.Bytes, contentDigest),
                            Permission = CredentialPermission.Ask,
                        }, frozen.CredentialId, key, existing: null);
                        mutation = new FrozenAgentTextMutation.Create(prepared);
                        expectedFileDigest = null;
                        break;
                    case BrokerApprovalOperation.Modify:
                        if (frozen.TargetId != frozen.CredentialId || frozen.PreviousDigest is null)
                        {
                            throw VaultException.Changed();
                        }
                        var existing = Store.FetchCredential(frozen.CredentialId);
                        if (existing is null
                            || existing.PayloadKind != CredentialPayloadKind.File.WireValue()
                            || existing.Permission == CredentialPermission.Hidden.WireValue())
                        {
                            throw VaultException.Changed();
                        }
                        expectedFileDigest = DigestData(frozen.PreviousDigest);
                        if (existing.ContentDigest is null
                            || !CryptographicOperations.FixedTimeEquals(existing.ContentDigest, expectedFileDigest))
                        {
                            throw VaultException.Changed();
                        }
                        var updated = existing.Clone();
                        updated.EncryptedPayload = VaultCrypto.Encrypt(frozen.Bytes, key);
                        updated.EncryptedOriginalFilename = VaultCrypto.Encrypt(frozen.OriginalFilename, key);
                        updated.ByteSize = frozen.ByteCount;
                        updated.ContentDigest = contentDigest;
                        updated.UpdatedAt = Timestamp(CurrentDate);
                        mutation = new FrozenAgentTextMutation.Modify(updated, existing.UpdatedAt);
                        break;
                    default:
                        throw new BrokerApprovalException(BrokerApprovalError.InvalidRequest);
                }
                var write = new FrozenAgentTextWrite
                {
                    OperationId = frozen.OperationId,
                    Digest = digest,
                    ApprovalRequest = approval,
                    CredentialExpiresAt = null,
                    Mutation = mutation,
                };
                _ = StoreCommit(write, requestId, capabilityDigest, resultDigest: frozen.Digest,
                    expectedFileDigest: expectedFileDigest);
                BrokerRequests.CancelPending(frozen.CredentialId);
                ApprovalRequests.CancelPending(frozen.CredentialId);
                FileDeliveryManager.Revoke(frozen.CredentialId);
                succeeded = true;
                NotifySnapshotRelevantChange();
            }
            finally
            {
                AgentAccessGate.EndExclusiveChange(paused: wasPaused);
            }
        }
        finally
        {
            RecordCredentialAccess(CredentialAccessEvent.Create(CurrentDate, frozen.CredentialId,
                accessOperation,
                succeeded ? CredentialAccessResult.Allowed : CredentialAccessResult.Failed,
                null, null));
        }
    }

    internal static string FileWriteCapabilityDigest(string capability)
        => Sha256Hex(Encoding.UTF8.GetBytes(capability));

    public ManagedTextCredential CreateFileCredential(FileCredentialInput input,
        ManagementAuthenticator authenticator)
        => PerformCredentialCreation(authenticator, () =>
        {
            var key = RequireKey();
            var prepared = PreparedFileRecord(input, Guid.NewGuid().ToString(), key, existing: null);
            Store.InsertCredential(prepared);
            return ManagedCredential(prepared, key, includeSecrets: false);
        }, input.Permission == CredentialPermission.Allowed
            ? CredentialManagementCopy.AllowPermissionReason
            : CredentialManagementCopy.ManageReason);

    public ManagedTextCredential UpdateFileCredential(string id, FileCredentialInput input,
        ManagementAuthenticator authenticator)
    {
        RequireManagementSession();
        var current = Store.FetchCredential(id) ?? throw VaultException.NotFound(id);
        AuthorizeManagement(authenticator, GrantsAllowed(current, input.Permission)
            ? CredentialManagementCopy.AllowPermissionReason
            : CredentialManagementCopy.ManageReason);
        var key = RequireKey();
        var prepared = PerformCredentialMutation(id, () =>
        {
            var existing = Store.FetchCredential(id) ?? throw VaultException.NotFound(id);
            var updated = PreparedFileRecord(input, existing.Id, key, existing);
            Store.UpdateCredential(updated);
            return updated;
        });
        return ManagedCredential(prepared, key, includeSecrets: false);
    }

    internal CredentialRecord PreparedFileRecord(FileCredentialInput input, string id, byte[] key,
        CredentialRecord? existing)
    {
        var displayName = CredentialName.DisplayName(input.Name);
        var nameIndex = CredentialIndex.Hash(CredentialName.Normalized(displayName), key);
        if (Store.FetchCredentialIncludingRecycledByNameIndex(nameIndex) is { } conflict && conflict.Id != id)
        {
            throw VaultException.NameConflict(displayName);
        }
        byte[] encryptedPayload;
        byte[] encryptedOriginalFilename;
        int byteSize;
        byte[] contentDigest;
        if (input.Snapshot is { } snapshot)
        {
            encryptedPayload = VaultCrypto.Encrypt(snapshot.Bytes, key);
            encryptedOriginalFilename = VaultCrypto.Encrypt(snapshot.OriginalFilename, key);
            byteSize = snapshot.Bytes.Length;
            contentDigest = SHA256.HashData(snapshot.Bytes);
        }
        else if (existing?.EncryptedOriginalFilename is { } existingFilename
                 && existing.ByteSize is { } existingSize
                 && existing.ContentDigest is { } existingDigest)
        {
            encryptedPayload = existing.EncryptedPayload;
            encryptedOriginalFilename = existingFilename;
            byteSize = existingSize;
            contentDigest = existingDigest;
        }
        else
        {
            throw VaultException.File(FileCredentialIssue.MissingFile);
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
            EncryptedPayload = encryptedPayload,
            EncryptedUsageInstructions = VaultCrypto.Encrypt(input.UsageInstructions, key),
            EncryptedPrivateNotes = VaultCrypto.Encrypt(input.PrivateNotes, key),
            EncryptedGroupName = group is null ? null : VaultCrypto.Encrypt(group, key),
            EncryptedEnvironmentVariable = environmentVariable is null
                ? null
                : VaultCrypto.Encrypt(environmentVariable, key),
            PayloadKind = CredentialPayloadKind.File.WireValue(),
            Permission = input.Permission.WireValue(),
            ExpiresAt = input.ExpiresAt is { } expiry ? Timestamp(expiry) : null,
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now,
            EncryptedOriginalFilename = encryptedOriginalFilename,
            ByteSize = byteSize,
            ContentDigest = contentDigest,
            DeletedAt = existing?.DeletedAt,
        };
    }

    public ManagedTextCredential CreateBundleCredential(BundleCredentialInput input,
        ManagementAuthenticator authenticator)
        => PerformCredentialCreation(authenticator, () =>
        {
            var key = RequireKey();
            var prepared = PreparedBundleRecord(input, Guid.NewGuid().ToString(), key, existing: null);
            Store.InsertCredential(prepared);
            return ManagedCredential(prepared, key, includeSecrets: false);
        }, input.Permission == CredentialPermission.Allowed
            ? CredentialManagementCopy.AllowPermissionReason
            : CredentialManagementCopy.ManageReason);

    public ManagedTextCredential UpdateBundleCredential(string id, BundleCredentialInput input,
        ManagementAuthenticator authenticator)
    {
        RequireManagementSession();
        var current = Store.FetchCredential(id) ?? throw VaultException.NotFound(id);
        AuthorizeManagement(authenticator, GrantsAllowed(current, input.Permission)
            ? CredentialManagementCopy.AllowPermissionReason
            : CredentialManagementCopy.ManageReason);
        var key = RequireKey();
        var prepared = PerformCredentialMutation(id, () =>
        {
            var existing = Store.FetchCredential(id) ?? throw VaultException.NotFound(id);
            var updated = PreparedBundleRecord(input, id, key, existing);
            Store.UpdateCredential(updated);
            return updated;
        });
        return ManagedCredential(prepared, key, includeSecrets: false);
    }

    public ManagedTextCredential ReplaceImportedBundleCredential(string id,
        IReadOnlyList<CredentialComponentInput> components, ManagementAuthenticator authenticator)
    {
        RequireManagementSession();
        AuthorizeManagement(authenticator, CredentialManagementCopy.ManageReason);
        var key = RequireKey();
        var validated = CredentialBundleValidator.ValidatedComponents(components);
        var prepared = PerformCredentialMutation(id, () =>
        {
            var existing = Store.FetchCredential(id) ?? throw VaultException.NotFound(id);
            existing.EncryptedPayload = VaultCrypto.Encrypt(CredentialComponentsCodec.Encode(validated), key);
            existing.PayloadKind = CredentialPayloadKind.Bundle.WireValue();
            existing.EncryptedEnvironmentVariable = null;
            existing.EncryptedOriginalFilename = null;
            existing.ByteSize = null;
            existing.ContentDigest = null;
            existing.UpdatedAt = Timestamp(CurrentDate);
            Store.UpdateCredential(existing);
            return existing;
        });
        return ManagedCredential(prepared, key, includeSecrets: false);
    }

    internal CredentialRecord PreparedBundleRecord(BundleCredentialInput input, string id, byte[] key,
        CredentialRecord? existing)
    {
        var displayName = CredentialName.DisplayName(input.Name);
        var nameIndex = CredentialIndex.Hash(CredentialName.Normalized(displayName), key);
        if (Store.FetchCredentialIncludingRecycledByNameIndex(nameIndex) is { } conflict && conflict.Id != id)
        {
            throw VaultException.NameConflict(displayName);
        }
        var components = CredentialBundleValidator.ValidatedComponents(input.Components);
        var group = CredentialName.OptionalDisplayName(input.GroupName);
        CredentialFieldValidation.UsageInstructions(input.UsageInstructions);
        var now = Timestamp(CurrentDate);
        return new CredentialRecord
        {
            Id = id,
            NameIndex = nameIndex,
            EncryptedDisplayName = VaultCrypto.Encrypt(displayName, key),
            EncryptedPayload = VaultCrypto.Encrypt(CredentialComponentsCodec.Encode(components), key),
            EncryptedUsageInstructions = VaultCrypto.Encrypt(input.UsageInstructions, key),
            EncryptedPrivateNotes = VaultCrypto.Encrypt(input.PrivateNotes, key),
            EncryptedGroupName = group is null ? null : VaultCrypto.Encrypt(group, key),
            PayloadKind = CredentialPayloadKind.Bundle.WireValue(),
            Permission = input.Permission.WireValue(),
            ExpiresAt = input.ExpiresAt is { } expiry ? Timestamp(expiry) : null,
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now,
            DeletedAt = existing?.DeletedAt,
        };
    }

    internal static byte[] DigestData(string hex)
    {
        if (hex.Length != 64) throw VaultException.File(FileCredentialIssue.DigestMismatch);
        var bytes = new byte[32];
        for (var index = 0; index < bytes.Length; index++)
        {
            var high = HexNibble(hex[index * 2]);
            var low = HexNibble(hex[index * 2 + 1]);
            if (high < 0 || low < 0) throw VaultException.File(FileCredentialIssue.DigestMismatch);
            bytes[index] = (byte)((high << 4) | low);
        }
        return bytes;
    }

    private static int HexNibble(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'A' and <= 'F' => value - 'A' + 10,
        >= 'a' and <= 'f' => value - 'a' + 10,
        _ => -1,
    };

    internal static bool EqualDigest(string? left, string? right)
    {
        if (left is null && right is null) return true;
        if (left is null || right is null) return false;
        return CryptographicOperations.FixedTimeEquals(DigestData(left), DigestData(right));
    }
}

internal static class BrokerApprovalOperationWireExtensions
{
    public static string WireOperation(this BrokerApprovalOperation operation)
        => BrokerWire.ToWire(operation);
}
