using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AskKey.Broker.Approval;
using AskKey.Broker.FileWrite;
using AskKey.Broker.Protocol;

namespace AskKey.Vault;

public sealed partial class Vault
{
    public AgentTextWriteRequestOutcome RequestAgentTextWrite(AgentTextWriteRequest request,
        Func<BrokerComponentFileReference, BrokerFrozenComponentFile>? fileResolver = null)
    {
        ValidateAgentTextWrite(request);
        if (request.Action.ActionKind == AgentTextWriteActionKind.Organize)
        {
            return RequestAgentOrganization(request);
        }
        var alreadyFrozen = AgentTextWrites.Entry(request.OperationId) is not null;
        var alreadyCommitted = Store.FetchAgentWriteOperation(request.OperationId) is not null;
        var files = new Dictionary<BrokerComponentFileReference, BrokerFrozenComponentFile>();
        if (!alreadyFrozen && !alreadyCommitted)
        {
            var resolver = fileResolver
                ?? (_ => throw new BrokerApprovalException(BrokerApprovalError.InvalidRequest));
            var totalFileBytes = 0;
            foreach (var reference in request.ComponentFileReferences)
            {
                var file = resolver(reference);
                totalFileBytes += file.Bytes.Length;
                if (totalFileBytes > BrokerFileWriteCoordinator.MaximumByteCount)
                {
                    throw new BrokerApprovalException(BrokerApprovalError.InvalidRequest);
                }
                if (file.Digest != reference.Digest || Sha256Hex(file.Bytes) != reference.Digest)
                {
                    throw new BrokerApprovalException(BrokerApprovalError.PayloadMismatch);
                }
                files[reference] = file;
            }
        }
        AgentAccessGate.BeginAgentOperation();
        try
        {
            return AgentTextWrites.Synchronized(() => RequestAgentTextWriteSynchronized(request, files));
        }
        finally
        {
            AgentAccessGate.EndAgentOperation();
        }
    }

    private AgentTextWriteRequestOutcome RequestAgentTextWriteSynchronized(AgentTextWriteRequest request,
        IReadOnlyDictionary<BrokerComponentFileReference, BrokerFrozenComponentFile> files)
    {
        ValidateAgentTextWrite(request);
        var digest = AgentTextWriteDigest(request);
        if (Store.FetchAgentWriteOperation(request.OperationId) is { } committed)
        {
            if (committed.PayloadDigest != digest)
            {
                throw new BrokerApprovalException(BrokerApprovalError.PayloadMismatch);
            }
            return AgentTextWriteRequestOutcome.Completed(
                new AgentTextWriteResult(committed.OperationId, committed.CredentialId));
        }
        if (ApprovalRequests.TerminalRetransmission(request.OperationId, digest) is { } retransmission)
        {
            return AgentTextWriteRequestOutcome.Submitted(Submission(request.OperationId, retransmission));
        }
        var key = RequireKey();
        FrozenAgentTextWrite candidate;
        if (AgentTextWrites.Entry(request.OperationId) is { } existing)
        {
            if (existing.Digest != digest)
            {
                throw new BrokerApprovalException(BrokerApprovalError.PayloadMismatch);
            }
            candidate = existing;
        }
        else
        {
            candidate = FreezeAgentTextWrite(request, digest, key, files);
        }
        var reservation = AgentTextWrites.Store(candidate);
        try
        {
            var ticket = ApprovalRequests.Submit(reservation.Write.ApprovalRequest,
                reservation.Write.CredentialExpiresAt is { } expiresAt
                    ? BrokerCredentialDeadline.ExpiresAt(expiresAt)
                    : BrokerCredentialDeadline.None);
            return AgentTextWriteRequestOutcome.Submitted(Submission(request.OperationId, ticket));
        }
        catch
        {
            if (reservation.Inserted) AgentTextWrites.Remove(request.OperationId);
            throw;
        }
    }

    private static AgentTextWriteSubmission Submission(string operationId, BrokerApprovalTicket ticket)
        => new(operationId, ticket.RequestId, ticket.Capability, ticket.State, ticket.RetryCount);

    public BrokerRequestState CancelAgentTextWrite(string operationId, string requestId, string capability)
    {
        if (AgentOrganizations.Entry(operationId) is not null)
        {
            return CancelAgentOrganization(operationId, requestId, capability);
        }
        return AgentTextWrites.Synchronized(() =>
        {
            var frozen = AgentTextWrites.Entry(operationId)
                ?? throw new BrokerApprovalException(BrokerApprovalError.RequestNotFound);
            var state = ApprovalRequests.Cancel(requestId, capability, frozen.ApprovalRequest);
            if (state is BrokerRequestState.Cancelled or BrokerRequestState.Denied or BrokerRequestState.Expired)
            {
                AgentTextWrites.Remove(operationId);
            }
            return state;
        });
    }

    internal FrozenAgentTextWrite FreezeAgentTextWrite(AgentTextWriteRequest request, string digest, byte[] key,
        IReadOnlyDictionary<BrokerComponentFileReference, BrokerFrozenComponentFile> files)
    {
        FrozenAgentTextMutation mutation;
        string credentialId;
        DateTimeOffset? credentialExpiresAt;
        BrokerApprovalOperation operation;
        CredentialRecord? beforeRecord = null;
        string? groupAssignment = null;
        var createsGroup = false;
        switch (request.Action.ActionKind)
        {
            case AgentTextWriteActionKind.Organize:
                throw new BrokerApprovalException(BrokerApprovalError.InvalidRequest);
            case AgentTextWriteActionKind.Create:
                credentialId = Guid.NewGuid().ToString();
                var created = PreparedRecord(new TextCredentialInput
                {
                    Name = request.Action.Name!,
                    Value = request.Action.Value!,
                    Permission = CredentialPermission.Ask,
                }, credentialId, key, existing: null);
                mutation = new FrozenAgentTextMutation.Create(created);
                credentialExpiresAt = null;
                operation = BrokerApprovalOperation.Create;
                break;
            case AgentTextWriteActionKind.Modify:
                var existingText = AvailableTextCredential(request.Action.Name!, key, requiresText: true,
                    request.CallerName, request.CallerPurpose);
                beforeRecord = existingText;
                credentialId = existingText.Id;
                credentialExpiresAt = existingText.ExpiresAt is { } textExpiry ? ParseExpiry(textExpiry) : null;
                var updatedText = existingText.Clone();
                updatedText.EncryptedPayload = VaultCrypto.Encrypt(request.Action.Value!, key);
                updatedText.UpdatedAt = Timestamp(CurrentDate);
                mutation = new FrozenAgentTextMutation.Modify(updatedText, existingText.UpdatedAt);
                operation = BrokerApprovalOperation.Modify;
                break;
            case AgentTextWriteActionKind.CreateBundle:
                credentialId = Guid.NewGuid().ToString();
                var components = (request.Action.Components ?? Array.Empty<BrokerCredentialComponentInput>())
                    .Select(input => ComponentInput(input, files)).ToList();
                if (request.Action.Group is { } groupName)
                {
                    (groupAssignment, createsGroup) = ResolvedAgentGroup(groupName, key);
                }
                var bundle = PreparedBundleRecord(new BundleCredentialInput
                {
                    Name = request.Action.Name!,
                    Components = components,
                    UsageInstructions = request.Action.UsageInstructions ?? "",
                    GroupName = groupAssignment,
                    Permission = CredentialPermission.Ask,
                }, credentialId, key, existing: null);
                mutation = new FrozenAgentTextMutation.Create(bundle);
                credentialExpiresAt = null;
                operation = BrokerApprovalOperation.Create;
                break;
            case AgentTextWriteActionKind.ModifyBundle:
                var existingBundle = AvailableTextCredential(request.Action.Name!, key, requiresText: false,
                    request.CallerName, request.CallerPurpose);
                beforeRecord = existingBundle;
                credentialId = existingBundle.Id;
                credentialExpiresAt = existingBundle.ExpiresAt is { } bundleExpiry ? ParseExpiry(bundleExpiry) : null;
                var bundleComponents = CredentialComponents(existingBundle, key).ToList();
                var touched = new HashSet<string>(StringComparer.Ordinal);
                foreach (var change in request.Action.Changes ?? Array.Empty<BrokerCredentialComponentChange>())
                {
                    var rawName = change.Upsert?.Name ?? change.RemoveName
                        ?? throw new BrokerApprovalException(BrokerApprovalError.InvalidRequest);
                    var normalized = CredentialName.Normalized(CredentialName.DisplayName(rawName));
                    if (!touched.Add(normalized))
                    {
                        throw new BrokerApprovalException(BrokerApprovalError.InvalidRequest);
                    }
                    var index = bundleComponents.FindIndex(component =>
                        CredentialName.Normalized(component.Name) == normalized);
                    if (change.Upsert is { } upsert)
                    {
                        var replacement = ComponentInput(upsert, files);
                        if (index >= 0) bundleComponents[index] = replacement;
                        else bundleComponents.Add(replacement);
                    }
                    else
                    {
                        if (index < 0) throw new BrokerApprovalException(BrokerApprovalError.InvalidRequest);
                        bundleComponents.RemoveAt(index);
                    }
                }
                bundleComponents = CredentialBundleValidator.ValidatedComponents(bundleComponents).ToList();
                var updatedBundle = existingBundle.Clone();
                if ((request.Action.Changes?.Count ?? 0) > 0)
                {
                    updatedBundle.PayloadKind = CredentialPayloadKind.Bundle.WireValue();
                    updatedBundle.EncryptedPayload = VaultCrypto.Encrypt(CredentialComponentsCodec.Encode(bundleComponents), key);
                    updatedBundle.EncryptedEnvironmentVariable = null;
                    updatedBundle.EncryptedOriginalFilename = null;
                    updatedBundle.ByteSize = null;
                    updatedBundle.ContentDigest = null;
                }
                if (request.Action.UsageInstructions is { } instructions)
                {
                    CredentialFieldValidation.UsageInstructions(instructions);
                    updatedBundle.EncryptedUsageInstructions = VaultCrypto.Encrypt(instructions, key);
                }
                if (request.Action.GroupChange is { } groupChange)
                {
                    if (groupChange.Ungrouped)
                    {
                        updatedBundle.EncryptedGroupName = null;
                    }
                    else
                    {
                        (groupAssignment, createsGroup) = ResolvedAgentGroup(groupChange.Name!, key);
                        updatedBundle.EncryptedGroupName = groupAssignment is null
                            ? null
                            : VaultCrypto.Encrypt(groupAssignment, key);
                    }
                }
                updatedBundle.UpdatedAt = Timestamp(CurrentDate);
                mutation = new FrozenAgentTextMutation.Modify(updatedBundle, existingBundle.UpdatedAt);
                operation = BrokerApprovalOperation.Modify;
                break;
            case AgentTextWriteActionKind.Delete:
                var deleted = AvailableTextCredential(request.Action.Name!, key, requiresText: false,
                    request.CallerName, request.CallerPurpose);
                beforeRecord = deleted;
                credentialId = deleted.Id;
                credentialExpiresAt = deleted.ExpiresAt is { } deleteExpiry ? ParseExpiry(deleteExpiry) : null;
                mutation = new FrozenAgentTextMutation.Delete(deleted.Id, deleted.UpdatedAt, Timestamp(CurrentDate));
                operation = BrokerApprovalOperation.Delete;
                break;
            default:
                throw new BrokerApprovalException(BrokerApprovalError.InvalidRequest);
        }
        var credentialName = CredentialName.DisplayName(request.Action.CredentialName);
        var before = beforeRecord is null ? new List<CredentialComponentInput>() : CredentialComponents(beforeRecord, key);
        List<CredentialComponentInput> after;
        CredentialRecord? afterRecord;
        switch (mutation)
        {
            case FrozenAgentTextMutation.Create create:
                after = CredentialComponents(create.Record, key);
                afterRecord = create.Record;
                break;
            case FrozenAgentTextMutation.Modify modify:
                after = CredentialComponents(modify.Record, key);
                afterRecord = modify.Record;
                break;
            default:
                after = new List<CredentialComponentInput>();
                afterRecord = null;
                break;
        }
        var summary = ComponentSummary(credentialName, operation, before, after, beforeRecord, afterRecord,
            createsGroup, key);
        var approvalDigest = VaultJson.Digest(new JsonArray(
            JsonValue.Create(digest),
            JsonValue.Create(credentialId),
            JsonValue.Create(summary.BeforeDigest),
            JsonValue.Create(summary.AfterDigest),
            JsonValue.Create(summary.BeforeUsageInstructions),
            JsonValue.Create(summary.AfterUsageInstructions),
            JsonValue.Create(summary.BeforeGroup),
            JsonValue.Create(summary.AfterGroup),
            JsonValue.Create(createsGroup.ToString())));
        var approval = new BrokerApprovalOperationRequest(request.OperationId, credentialId, credentialId,
            operation, approvalDigest, credentialName, request.CallerName, request.CallerPurpose,
            retransmissionDigest: digest);
        return new FrozenAgentTextWrite
        {
            OperationId = request.OperationId,
            Digest = digest,
            ApprovalRequest = approval,
            CredentialExpiresAt = credentialExpiresAt,
            Mutation = mutation,
            BeforeRecord = beforeRecord,
            Summary = summary,
            GroupAssignment = groupAssignment,
        };
    }

    private (string? Assignment, bool Creates) ResolvedAgentGroup(string rawName, byte[] key)
    {
        var name = CredentialName.DisplayName(rawName);
        var normalized = CredentialName.Normalized(name);
        var existing = CredentialGroupNames(key)
            .FirstOrDefault(group => CredentialName.Normalized(group) == normalized);
        return existing is null ? (name, true) : (existing, false);
    }

    private CredentialRecord AvailableTextCredential(string rawName, byte[] key, bool requiresText,
        string? callerHint, string? declaredPurpose)
    {
        var displayName = CredentialName.DisplayName(rawName);
        var nameIndex = CredentialIndex.Hash(CredentialName.Normalized(displayName), key);
        var record = Store.FetchCredentialByNameIndex(nameIndex);
        if (record is null
            || !CredentialPayloadKindExtensions.TryParse(record.PayloadKind, out var kind)
            || (requiresText && kind != CredentialPayloadKind.Text)
            || !CredentialPermissionExtensions.TryParse(record.Permission, out var permission)
            || permission == CredentialPermission.Hidden)
        {
            RecordHiddenCredentialGuess(callerHint, declaredPurpose);
            throw VaultException.Unavailable();
        }
        if (record.ExpiresAt is { } storedExpiry)
        {
            if (ParseExpiry(storedExpiry) <= CurrentDate)
            {
                ApprovalRequests.CancelPendingForExpiry(record.Id);
                throw VaultException.Unavailable();
            }
        }
        return record;
    }

    private static CredentialComponentInput ComponentInput(BrokerCredentialComponentInput input,
        IReadOnlyDictionary<BrokerComponentFileReference, BrokerFrozenComponentFile> files)
    {
        CredentialComponentValue value;
        if (input.Value.Text is { } text)
        {
            value = new CredentialComponentValue.Text(text);
        }
        else if (input.Value.File is { } reference)
        {
            if (!files.TryGetValue(reference, out var file))
            {
                throw new BrokerApprovalException(BrokerApprovalError.InvalidRequest);
            }
            value = new CredentialComponentValue.File(file.OriginalFilename, file.Bytes);
        }
        else
        {
            throw new BrokerApprovalException(BrokerApprovalError.InvalidRequest);
        }
        var delivery = input.Delivery.DeliveryKind switch
        {
            BrokerComponentDelivery.Kind.EnvironmentVariable =>
                CredentialComponentDelivery.EnvironmentVariable(input.Delivery.Name!),
            BrokerComponentDelivery.Kind.TemporaryFile =>
                CredentialComponentDelivery.TemporaryFile(input.Delivery.Name!),
            _ => CredentialComponentDelivery.None,
        };
        return new CredentialComponentInput(input.Name, value, delivery, input.Masked);
    }
}
