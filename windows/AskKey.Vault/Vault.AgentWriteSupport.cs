using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AskKey.Broker.Approval;
using AskKey.Broker.Protocol;

namespace AskKey.Vault;

public sealed partial class Vault
{
    private void ValidateAgentTextWrite(AgentTextWriteRequest request)
    {
        if (!request.IsBounded) throw new BrokerApprovalException(BrokerApprovalError.InvalidRequest);
    }

    internal string AgentTextWriteDigest(AgentTextWriteRequest request)
        => VaultJson.Digest(VaultJson.WithoutNulls(BrokerJson.EncodeTextWriteRequest(request)));

    public AgentTextWriteResult CommitAgentTextWrite(
        AgentTextWriteRequest request,
        string requestId,
        string capability)
    {
        if (request.Action.ActionKind == AgentTextWriteActionKind.Organize)
        {
            return CommitAgentOrganization(request, requestId, capability);
        }

        var accessOperation = request.Action.ActionKind switch
        {
            AgentTextWriteActionKind.Create or AgentTextWriteActionKind.CreateBundle => CredentialAccessOperation.Create,
            AgentTextWriteActionKind.Delete => CredentialAccessOperation.Delete,
            _ => CredentialAccessOperation.Modify,
        };
        var credentialId = AgentTextWrites.Entry(request.OperationId)?.ApprovalRequest.CredentialId;
        var succeeded = false;
        try
        {
            var wasPaused = AgentAccessGate.BeginExclusiveAgentChange();
            try
            {
                var result = AgentTextWrites.Synchronized(() =>
                    CommitAgentTextWriteSynchronized(request, requestId, capability));
                credentialId = result.CredentialId;
                FileDeliveryManager.Revoke(result.CredentialId);
                succeeded = true;
                return result;
            }
            finally
            {
                AgentAccessGate.EndExclusiveChange(paused: wasPaused);
            }
        }
        finally
        {
            RecordCredentialAccess(CredentialAccessEvent.Create(CurrentDate, credentialId, accessOperation,
                succeeded ? CredentialAccessResult.Allowed : CredentialAccessResult.Failed,
                request.CallerName, request.CallerPurpose));
        }
    }

    private AgentTextWriteResult CommitAgentTextWriteSynchronized(
        AgentTextWriteRequest request,
        string requestId,
        string capability)
    {
        var digest = AgentTextWriteDigest(request);
        if (CompletedAgentTextWrite(request.OperationId, digest, requestId, capability) is { } completed)
        {
            return completed;
        }

        var frozen = AgentTextWrites.Entry(request.OperationId)
            ?? throw new BrokerApprovalException(BrokerApprovalError.RequestNotFound);
        if (frozen.Digest != digest)
        {
            throw new BrokerApprovalException(BrokerApprovalError.PayloadMismatch);
        }
        if (frozen.CredentialExpiresAt is { } expiresAt && expiresAt <= CurrentDate)
        {
            CancelAgentWritesForExpiry(frozen.ApprovalRequest.CredentialId);
            throw VaultException.Unavailable();
        }

        try
        {
            var result = ApprovalRequests.ConsumeApproved(
                requestId,
                capability,
                frozen.ApprovalRequest,
                () => StoreCommit(frozen, requestId, AgentTextWriteCapabilityDigest(capability),
                    credentialGroupsKey: RequireKey()));
            AgentTextWrites.Remove(request.OperationId);
            BrokerRequests.CancelPending(result.CredentialId);
            ApprovalRequests.CancelPending(result.CredentialId);
            NotifySnapshotRelevantChange();
            return result;
        }
        catch (VaultException exception) when (exception.Code == VaultErrorCode.CredentialUnavailable)
        {
            CancelAgentWritesForExpiry(frozen.ApprovalRequest.CredentialId);
            throw;
        }
        catch (BrokerApprovalException exception) when (exception.Error == BrokerApprovalError.AlreadyConsumed)
        {
            if (CompletedAgentTextWrite(request.OperationId, digest, requestId, capability) is { } replay)
            {
                return replay;
            }
            throw;
        }
        catch
        {
            try
            {
                var state = ApprovalRequests.Status(requestId, capability);
                if (state is not (BrokerRequestState.Pending or BrokerRequestState.Approved))
                {
                    AgentTextWrites.Remove(request.OperationId);
                }
            }
            catch
            {
                AgentTextWrites.Remove(request.OperationId);
            }
            throw;
        }
    }

    private AgentTextWriteResult? CompletedAgentTextWrite(
        string operationId,
        string digest,
        string requestId,
        string capability)
    {
        var committed = Store.FetchAgentWriteOperation(operationId);
        if (committed is null) return null;
        if (committed.PayloadDigest != digest)
        {
            throw new BrokerApprovalException(BrokerApprovalError.PayloadMismatch);
        }
        if (committed.RequestId != requestId
            || committed.CapabilityDigest != AgentTextWriteCapabilityDigest(capability))
        {
            throw new BrokerApprovalException(BrokerApprovalError.RequestNotFound);
        }
        return new AgentTextWriteResult(committed.OperationId, committed.CredentialId);
    }

    private void CancelAgentWritesForExpiry(string credentialId)
    {
        BrokerRequests.CancelPending(credentialId);
        ApprovalRequests.CancelPendingForExpiry(credentialId);
    }

    private static string AgentTextWriteCapabilityDigest(string capability)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(capability)));

    public AgentTextWriteAction RevealAgentTextWrite(
        string operationId,
        string requestId,
        string capability,
        ManagementAuthenticator authenticator)
    {
        AuthorizeManagement(authenticator, CredentialManagementCopy.RevealReason);
        var frozen = AgentTextWrites.Entry(operationId)
            ?? throw new BrokerApprovalException(BrokerApprovalError.RequestNotFound);
        var state = ApprovalRequests.Status(requestId, capability, frozen.ApprovalRequest);
        if (state is not (BrokerRequestState.Pending or BrokerRequestState.Approved))
        {
            throw new BrokerApprovalException(BrokerApprovalError.InvalidDecision);
        }

        var key = RequireKey();
        return frozen.Mutation switch
        {
            FrozenAgentTextMutation.Create create => AgentTextWriteAction.Create(
                VaultCrypto.Decrypt(create.Record.EncryptedDisplayName, key),
                VaultCrypto.Decrypt(create.Record.EncryptedPayload, key)),
            FrozenAgentTextMutation.Modify modify => AgentTextWriteAction.Modify(
                VaultCrypto.Decrypt(modify.Record.EncryptedDisplayName, key),
                VaultCrypto.Decrypt(modify.Record.EncryptedPayload, key)),
            FrozenAgentTextMutation.Delete delete => RevealDeleteAction(delete, key),
            _ => throw new BrokerApprovalException(BrokerApprovalError.InvalidRequest),
        };
    }

    private AgentTextWriteAction RevealDeleteAction(FrozenAgentTextMutation.Delete delete, byte[] key)
    {
        var record = Store.FetchCredential(delete.CredentialId)
            ?? throw VaultException.NotFound(delete.CredentialId);
        return AgentTextWriteAction.Delete(VaultCrypto.Decrypt(record.EncryptedDisplayName, key));
    }

    public BrokerCredentialWriteSummary FrozenAgentWriteSummary(
        string operationId,
        string requestId,
        string capability)
    {
        var frozen = ActiveFrozenWrite(operationId, requestId, capability);
        return frozen.Summary ?? throw new BrokerApprovalException(BrokerApprovalError.InvalidRequest);
    }

    public FrozenCredentialWrite RevealFrozenCredentialWrite(
        string operationId,
        string requestId,
        string capability,
        ManagementAuthenticator authenticator)
    {
        AuthorizeManagement(authenticator, CredentialManagementCopy.RevealReason);
        return AgentTextWrites.Synchronized(() =>
        {
            var frozen = ActiveFrozenWrite(operationId, requestId, capability);
            var key = RequireKey();
            var before = frozen.BeforeRecord is null
                ? new List<CredentialComponentInput>()
                : CredentialComponents(frozen.BeforeRecord, key);
            var after = frozen.Mutation switch
            {
                FrozenAgentTextMutation.Create create => CredentialComponents(create.Record, key),
                FrozenAgentTextMutation.Modify modify => CredentialComponents(modify.Record, key),
                _ => new List<CredentialComponentInput>(),
            };
            return new FrozenCredentialWrite
            {
                CredentialName = frozen.ApprovalRequest.CredentialName ?? "",
                Operation = frozen.ApprovalRequest.Operation,
                Before = before,
                After = after,
            };
        });
    }

    private FrozenAgentTextWrite ActiveFrozenWrite(string operationId, string requestId, string capability)
    {
        var frozen = AgentTextWrites.Entry(operationId)
            ?? throw new BrokerApprovalException(BrokerApprovalError.RequestNotFound);
        var state = ApprovalRequests.Status(requestId, capability, frozen.ApprovalRequest);
        if (state is not (BrokerRequestState.Pending or BrokerRequestState.Approved))
        {
            throw new BrokerApprovalException(BrokerApprovalError.InvalidDecision);
        }
        return frozen;
    }

    private BrokerCredentialWriteSummary ComponentSummary(
        string name,
        BrokerApprovalOperation operation,
        IReadOnlyList<CredentialComponentInput> before,
        IReadOnlyList<CredentialComponentInput> after,
        CredentialRecord? beforeRecord,
        CredentialRecord? afterRecord,
        bool createsGroup,
        byte[] key)
    {
        var digestKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, key, 32, Array.Empty<byte>(),
            Encoding.UTF8.GetBytes("AskKey approval component value digest v1"));
        var beforeProjection = ProjectComponents(before, digestKey);
        var afterProjection = ProjectComponents(after, digestKey);
        return new BrokerCredentialWriteSummary
        {
            CredentialName = name,
            Operation = operation,
            Before = beforeProjection,
            After = afterProjection,
            BeforeDigest = before.Count == 0 ? null : VaultJson.Digest(VaultJson.ComponentInputNodes(before)),
            AfterDigest = after.Count == 0 ? null : VaultJson.Digest(VaultJson.ComponentInputNodes(after)),
            BeforeUsageInstructions = beforeRecord is null
                ? null
                : VaultCrypto.Decrypt(beforeRecord.EncryptedUsageInstructions, key),
            AfterUsageInstructions = afterRecord is null
                ? null
                : VaultCrypto.Decrypt(afterRecord.EncryptedUsageInstructions, key),
            BeforeGroup = beforeRecord?.EncryptedGroupName is { } beforeGroup
                ? VaultCrypto.Decrypt(beforeGroup, key)
                : null,
            AfterGroup = afterRecord?.EncryptedGroupName is { } afterGroup
                ? VaultCrypto.Decrypt(afterGroup, key)
                : null,
            CreatesGroup = createsGroup,
        };
    }

    private static List<BrokerCredentialComponentSummary> ProjectComponents(
        IReadOnlyList<CredentialComponentInput> components,
        byte[] digestKey)
    {
        return components.Select(component =>
        {
            var kind = component.Value is CredentialComponentValue.File
                ? BrokerCatalogPayloadKind.File
                : BrokerCatalogPayloadKind.Text;
            var byteCount = component.Value switch
            {
                CredentialComponentValue.Text text => Encoding.UTF8.GetByteCount(text.Value),
                CredentialComponentValue.File file => file.Bytes.Length,
                _ => 0,
            };
            return new BrokerCredentialComponentSummary
            {
                Name = component.Name,
                PayloadKind = kind,
                ByteCount = byteCount,
                Delivery = component.Delivery,
                Masked = component.Masked,
                ValueDigest = ComponentValueDigest(component.Value, digestKey),
            };
        }).ToList();
    }

    private static string ComponentValueDigest(CredentialComponentValue value, byte[] key)
    {
        byte[] material = value switch
        {
            CredentialComponentValue.Text text => Concatenate(
                Encoding.UTF8.GetBytes("text"), Field(Encoding.UTF8.GetBytes(text.Value))),
            CredentialComponentValue.File file => Concatenate(
                Encoding.UTF8.GetBytes("file"), Field(Encoding.UTF8.GetBytes(file.Filename)),
                Field(file.Bytes)),
            _ => throw VaultException.Database("Credential has an unknown payload kind."),
        };
        using var hmac = new HMACSHA256(key);
        return Convert.ToHexStringLower(hmac.ComputeHash(material));
    }

    private static byte[] Field(byte[] value)
    {
        var result = new byte[sizeof(ulong) + value.Length];
        BinaryPrimitives.WriteUInt64BigEndian(result, (ulong)value.Length);
        value.CopyTo(result, sizeof(ulong));
        return result;
    }

    private static byte[] Concatenate(params byte[][] parts)
    {
        var result = new byte[parts.Sum(part => part.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }
        return result;
    }
}


