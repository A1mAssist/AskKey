using System.Text.Json.Nodes;
using AskKey.Broker.Approval;
using AskKey.Broker.Protocol;

namespace AskKey.Vault;

public sealed partial class Vault
{
    internal AgentTextWriteRequestOutcome RequestAgentOrganization(AgentTextWriteRequest request)
    {
        AgentAccessGate.BeginAgentOperation();
        try
        {
            return AgentOrganizations.Synchronized(() => RequestAgentOrganizationSynchronized(request));
        }
        finally
        {
            AgentAccessGate.EndAgentOperation();
        }
    }

    private AgentTextWriteRequestOutcome RequestAgentOrganizationSynchronized(AgentTextWriteRequest request)
    {
        var digest = AgentTextWriteDigest(request);
        if (Store.FetchAgentWriteOperation(request.OperationId) is { } committed)
        {
            if (committed.PayloadDigest != digest
                || committed.Operation != BrokerApprovalOperation.Organize.WireOperation())
            {
                throw new BrokerApprovalException(BrokerApprovalError.PayloadMismatch);
            }
            return AgentTextWriteRequestOutcome.Completed(new AgentTextWriteResult(request.OperationId, ""));
        }
        if (ApprovalRequests.TerminalRetransmission(request.OperationId, digest) is { } retransmission)
        {
            return AgentTextWriteRequestOutcome.Submitted(OrganizationSubmission(request.OperationId, retransmission));
        }

        var existing = AgentOrganizations.Entry(request.OperationId);
        var frozen = existing ?? FreezeAgentOrganization(request, digest, RequireKey());
        if (frozen.Digest != digest) throw new BrokerApprovalException(BrokerApprovalError.PayloadMismatch);
        AgentOrganizations.Store(frozen);
        try
        {
            var ticket = ApprovalRequests.Submit(frozen.ApprovalRequest,
                frozen.CredentialExpiresAt is { } expiresAt
                    ? BrokerCredentialDeadline.ExpiresAt(expiresAt)
                    : BrokerCredentialDeadline.None);
            return AgentTextWriteRequestOutcome.Submitted(OrganizationSubmission(request.OperationId, ticket));
        }
        catch
        {
            if (existing is null) AgentOrganizations.Remove(request.OperationId);
            throw;
        }
    }

    internal BrokerRequestState CancelAgentOrganization(string operationId, string requestId, string capability)
    {
        return AgentOrganizations.Synchronized(() =>
        {
            var frozen = AgentOrganizations.Entry(operationId)
                ?? throw new BrokerApprovalException(BrokerApprovalError.RequestNotFound);
            return ApprovalRequests.Cancel(requestId, capability, frozen.ApprovalRequest);
        });
    }

    public BrokerOrganizationSummary FrozenAgentOrganizationSummary(string operationId, string requestId,
        string capability)
    {
        var frozen = AgentOrganizations.Entry(operationId)
            ?? throw new BrokerApprovalException(BrokerApprovalError.RequestNotFound);
        _ = ApprovalRequests.Status(requestId, capability, frozen.ApprovalRequest);
        return frozen.Summary;
    }

    public AgentTextWriteResult CommitAgentOrganization(AgentTextWriteRequest request, string requestId,
        string capability)
    {
        var wasPaused = AgentAccessGate.BeginExclusiveAgentChange();
        try
        {
            return AgentOrganizations.Synchronized(() =>
            {
                var digest = AgentTextWriteDigest(request);
                var capabilityDigest = AgentTextWriteCapabilityDigest(capability);
                if (Store.FetchAgentWriteOperation(request.OperationId) is { } receipt)
                {
                    if (receipt.PayloadDigest != digest
                        || receipt.Operation != BrokerApprovalOperation.Organize.WireOperation())
                    {
                        throw new BrokerApprovalException(BrokerApprovalError.PayloadMismatch);
                    }
                    if (receipt.RequestId != requestId || receipt.CapabilityDigest != capabilityDigest)
                    {
                        throw new BrokerApprovalException(BrokerApprovalError.RequestNotFound);
                    }
                    return new AgentTextWriteResult(request.OperationId, "");
                }

                var frozen = AgentOrganizations.Entry(request.OperationId)
                    ?? throw new BrokerApprovalException(BrokerApprovalError.RequestNotFound);
                if (frozen.Digest != digest) throw new BrokerApprovalException(BrokerApprovalError.PayloadMismatch);
                var succeeded = false;
                try
                {
                    var result = ApprovalRequests.ConsumeApproved(requestId, capability,
                        frozen.ApprovalRequest,
                        () => StoreCommitOrganization(frozen, requestId, capabilityDigest, RequireKey()));
                    succeeded = true;
                    AgentOrganizations.Remove(request.OperationId);
                    foreach (var id in frozen.ApprovalRequest.OrganizationCredentialIds ?? Array.Empty<string>())
                    {
                        BrokerRequests.CancelPending(id);
                        ApprovalRequests.CancelPending(id);
                        FileDeliveryManager.Revoke(id);
                    }
                    NotifySnapshotRelevantChange();
                    return result;
                }
                finally
                {
                    RecordAgentOrganizationAccess(frozen.ApprovalRequest,
                        succeeded ? CredentialAccessResult.Allowed : CredentialAccessResult.Failed);
                }
            });
        }
        finally
        {
            AgentAccessGate.EndExclusiveChange(paused: wasPaused);
        }
    }

    public void RecordAgentOrganizationAccess(BrokerApprovalOperationRequest request,
        CredentialAccessResult result)
    {
        var ids = request.OrganizationCredentialIds ?? Array.Empty<string>();
        IEnumerable<string?> targets = ids.Count == 0 ? new string?[] { null } : ids.Select(id => (string?)id);
        foreach (var id in targets)
        {
            RecordCredentialAccess(CredentialAccessEvent.Create(CurrentDate, id,
                CredentialAccessOperation.Modify, result, request.CallerName, request.CallerPurpose));
        }
    }

    private AgentTextWriteSubmission OrganizationSubmission(string operationId, BrokerApprovalTicket ticket)
        => new(operationId, ticket.RequestId, ticket.Capability, ticket.State, ticket.RetryCount);

    private FrozenAgentOrganization FreezeAgentOrganization(AgentTextWriteRequest request, string digest, byte[] key)
    {
        if (request.Action.ActionKind != AgentTextWriteActionKind.Organize || !request.IsBounded)
        {
            throw new BrokerApprovalException(BrokerApprovalError.InvalidRequest);
        }
        var snapshot = OrganizationSnapshot(key);
        var assignments = snapshot.Assignments(key);
        var storedGroupNames = new HashSet<string>(snapshot.StoredGroups, StringComparer.Ordinal);
        var groupNames = new HashSet<string>(snapshot.StoredGroups, StringComparer.Ordinal);
        foreach (var value in assignments.Values)
        {
            if (value is not null) groupNames.Add(value);
        }
        var knownGroups = new HashSet<string>(groupNames
            .Where(group =>
            {
                var normalizedGroup = CredentialName.Normalized(group);
                var members = snapshot.Records.Where(record =>
                    assignments.TryGetValue(record.Id, out var assigned)
                    && assigned is not null
                    && CredentialName.Normalized(assigned) == normalizedGroup).ToList();
                return members.Count == 0 || members.Any(record =>
                    record.DeletedAt is null && record.Permission != CredentialPermission.Hidden.WireValue());
            })
            .Select(CredentialName.Normalized), StringComparer.Ordinal);

        var original = snapshot.Records.ToDictionary(record => record.Id, StringComparer.Ordinal);
        var records = original.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
        var affected = new HashSet<string>(StringComparer.Ordinal);
        var moved = new HashSet<string>(StringComparer.Ordinal);
        var namedGroups = new HashSet<string>(StringComparer.Ordinal);
        var summary = new List<BrokerOrganizationSummary.Operation>();
        DateTimeOffset? earliestExpiry = null;

        string Name(string raw) => CredentialName.DisplayName(raw);
        string Normalized(string value) => CredentialName.Normalized(value);

        string ExistingGroup(string raw)
        {
            var display = Name(raw);
            namedGroups.Add(Normalized(display));
            var resolved = groupNames.OrderBy(value => value, StringComparer.Ordinal)
                .FirstOrDefault(value => Normalized(value) == Normalized(display))
                ?? throw VaultException.Unavailable();
            if (!knownGroups.Contains(Normalized(resolved))) throw VaultException.Unavailable();
            return resolved;
        }

        List<CredentialRecord> Members(string group)
        {
            var normalized = Normalized(group);
            return records.Values
                .Where(record => assignments.TryGetValue(record.Id, out var assigned)
                    && assigned is not null
                    && Normalized(assigned) == normalized)
                .OrderBy(record => record.Id, StringComparer.Ordinal)
                .ToList();
        }

        static int Nonvisible(IEnumerable<CredentialRecord> members)
            => members.Count(record => record.DeletedAt is not null
                || record.Permission == CredentialPermission.Hidden.WireValue());

        void Assign(string id, string? group)
        {
            if (!records.TryGetValue(id, out var record)) throw VaultException.Unavailable();
            affected.Add(id);
            if (assignments.TryGetValue(id, out var previous) && previous is not null)
            {
                namedGroups.Add(Normalized(previous));
            }
            if (group is not null) namedGroups.Add(Normalized(group));
            assignments[id] = group;
            record.EncryptedGroupName = group is null ? null : VaultCrypto.Encrypt(group, key);
            record.UpdatedAt = Timestamp(CurrentDate);
            records[id] = record;
        }

        foreach (var operation in request.Action.OrganizationOperations
                     ?? Array.Empty<BrokerOrganizationOperation>())
        {
            switch (operation.OperationKind)
            {
                case BrokerOrganizationOperation.Kind.Move:
                {
                    var credential = Name(operation.Credential!);
                    var index = CredentialIndex.Hash(Normalized(credential), key);
                    var record = records.Values.FirstOrDefault(candidate =>
                        candidate.DeletedAt is null && candidate.NameIndex.AsSpan().SequenceEqual(index));
                    if (record is null || record.Permission == CredentialPermission.Hidden.WireValue())
                    {
                        RecordHiddenCredentialGuess(request.CallerName, request.CallerPurpose);
                        throw VaultException.Unavailable();
                    }
                    if (record.ExpiresAt is { } storedExpiry)
                    {
                        var expiry = ParseExpiry(storedExpiry);
                        if (expiry <= CurrentDate) throw VaultException.Unavailable();
                        earliestExpiry = earliestExpiry is { } earliest && earliest <= expiry ? earliest : expiry;
                    }
                    var group = operation.Group is null ? null : ExistingGroup(operation.Group);
                    summary.Add(new BrokerOrganizationSummary.Operation
                    {
                        OperationKind = BrokerOrganizationSummary.Kind.Move,
                        Credential = VaultCrypto.Decrypt(record.EncryptedDisplayName, key),
                        From = assignments.GetValueOrDefault(record.Id),
                        To = group,
                    });
                    moved.Add(record.Id);
                    Assign(record.Id, group);
                    break;
                }
                case BrokerOrganizationOperation.Kind.CreateGroup:
                {
                    var display = Name(operation.Name!);
                    namedGroups.Add(Normalized(display));
                    if (knownGroups.Contains(Normalized(display))) throw VaultException.Unavailable();
                    var existing = groupNames.OrderBy(value => value, StringComparer.Ordinal)
                        .FirstOrDefault(value => Normalized(value) == Normalized(display));
                    if (existing is not null)
                    {
                        var members = Members(existing);
                        summary.Add(new BrokerOrganizationSummary.Operation
                        {
                            OperationKind = BrokerOrganizationSummary.Kind.ExistingGroup,
                            Name = existing,
                            Members = members.Count,
                            Nonvisible = Nonvisible(members),
                        });
                    }
                    else
                    {
                        groupNames.Add(display);
                        storedGroupNames.Add(display);
                        summary.Add(new BrokerOrganizationSummary.Operation
                        {
                            OperationKind = BrokerOrganizationSummary.Kind.CreateGroup,
                            Name = display,
                        });
                    }
                    knownGroups.Add(Normalized(display));
                    break;
                }
                case BrokerOrganizationOperation.Kind.RenameGroup:
                {
                    var from = ExistingGroup(operation.From!);
                    var proposed = Name(operation.To!);
                    namedGroups.Add(Normalized(proposed));
                    if (knownGroups.Contains(Normalized(proposed))) throw VaultException.Unavailable();
                    var existing = groupNames.OrderBy(value => value, StringComparer.Ordinal)
                        .FirstOrDefault(value => Normalized(value) == Normalized(proposed));
                    var to = existing ?? proposed;
                    var affectedMembers = Members(from);
                    if (existing is not null)
                    {
                        var targetMembers = Members(existing);
                        summary.Add(new BrokerOrganizationSummary.Operation
                        {
                            OperationKind = BrokerOrganizationSummary.Kind.MergeGroup,
                            From = from,
                            To = to,
                            Members = affectedMembers.Count,
                            Nonvisible = Nonvisible(affectedMembers),
                            TargetMembers = targetMembers.Count,
                            TargetNonvisible = Nonvisible(targetMembers),
                        });
                    }
                    else
                    {
                        summary.Add(new BrokerOrganizationSummary.Operation
                        {
                            OperationKind = BrokerOrganizationSummary.Kind.RenameGroup,
                            From = from,
                            To = to,
                            Members = affectedMembers.Count,
                            Nonvisible = Nonvisible(affectedMembers),
                        });
                        storedGroupNames.Add(to);
                    }
                    foreach (var record in affectedMembers) Assign(record.Id, to);
                    groupNames.RemoveWhere(value => Normalized(value) == Normalized(from));
                    storedGroupNames.RemoveWhere(value => Normalized(value) == Normalized(from));
                    groupNames.Add(to);
                    knownGroups.Remove(Normalized(from));
                    knownGroups.Add(Normalized(to));
                    break;
                }
                case BrokerOrganizationOperation.Kind.DeleteGroup:
                {
                    var group = ExistingGroup(operation.Name!);
                    var members = Members(group);
                    summary.Add(new BrokerOrganizationSummary.Operation
                    {
                        OperationKind = BrokerOrganizationSummary.Kind.DeleteGroup,
                        Name = group,
                        Members = members.Count,
                        Nonvisible = Nonvisible(members),
                    });
                    foreach (var record in members) Assign(record.Id, null);
                    groupNames.RemoveWhere(value => Normalized(value) == Normalized(group));
                    storedGroupNames.RemoveWhere(value => Normalized(value) == Normalized(group));
                    knownGroups.Remove(Normalized(group));
                    break;
                }
            }
        }

        var before = affected.OrderBy(id => id, StringComparer.Ordinal)
            .Select(id => original[id]).ToList();
        var after = affected.OrderBy(id => id, StringComparer.Ordinal)
            .Select(id => records[id]).ToList();
        var groups = snapshot.GroupStates(namedGroups, key);
        var finalNames = storedGroupNames.Where(name => namedGroups.Contains(Normalized(name)))
            .OrderBy(name => name, StringComparer.Ordinal).ToList();
        var presentation = new BrokerOrganizationSummary(summary);
        var approvalDigest = OrganizationApprovalDigest(digest, before, after, groups, finalNames, presentation);
        var approval = new BrokerApprovalOperationRequest(request.OperationId, "", "credential-library",
            BrokerApprovalOperation.Organize, approvalDigest, callerName: request.CallerName,
            callerPurpose: request.CallerPurpose, retransmissionDigest: digest,
            organizationCredentialIds: affected.OrderBy(id => id, StringComparer.Ordinal).ToList());
        return new FrozenAgentOrganization
        {
            Request = request,
            Digest = digest,
            ApprovalRequest = approval,
            CredentialExpiresAt = earliestExpiry,
            Before = before,
            After = after,
            MovedCredentialIds = moved,
            Groups = groups,
            FinalGroupNames = finalNames,
            Summary = presentation,
        };
    }

    private AgentTextWriteResult StoreCommitOrganization(FrozenAgentOrganization frozen, string requestId,
        string capabilityDigest, byte[] key)
    {
        return Store.RunTransaction(() =>
        {
            var committedAt = CurrentDate;
            var snapshot = OrganizationSnapshot(key);
            var names = frozen.Groups.Select(group => group.NormalizedName).ToList();
            var currentGroups = snapshot.GroupStates(names, key);
            if (currentGroups.Count != frozen.Groups.Count
                || currentGroups.Zip(frozen.Groups).Any(pair => !pair.First.SameAs(pair.Second)))
            {
                throw VaultException.Changed();
            }

            var currentRecords = snapshot.Records.ToDictionary(record => record.Id, StringComparer.Ordinal);
            foreach (var before in frozen.Before)
            {
                if (!currentRecords.TryGetValue(before.Id, out var current)
                    || VaultJson.Canonical(VaultJson.RecordNode(current))
                        != VaultJson.Canonical(VaultJson.RecordNode(before)))
                {
                    throw VaultException.Changed();
                }
                if (!frozen.MovedCredentialIds.Contains(before.Id)) continue;
                if (current.DeletedAt is not null
                    || current.Permission == CredentialPermission.Hidden.WireValue())
                {
                    throw VaultException.Unavailable();
                }
                if (current.ExpiresAt is { } storedExpiry
                    && ParseExpiry(storedExpiry) <= committedAt)
                {
                    throw VaultException.Unavailable();
                }
            }

            var groups = new HashSet<string>(snapshot.StoredGroups, StringComparer.Ordinal);
            groups.RemoveWhere(value => names.Contains(CredentialName.Normalized(value), StringComparer.Ordinal));
            foreach (var name in frozen.FinalGroupNames) groups.Add(name);
            if (!groups.SetEquals(snapshot.StoredGroups))
            {
                var json = new JsonArray(groups.OrderBy(value => value, StringComparer.Ordinal)
                    .Select(value => (JsonNode)JsonValue.Create(value)!).ToArray());
                var encrypted = VaultCrypto.Encrypt(VaultJson.Canonical(json), key);
                Store.SetConfigValueInTransaction(VaultGroups.ConfigKey, Convert.ToBase64String(encrypted));
            }
            foreach (var record in frozen.After)
            {
                Store.UpdateCredentialInTransaction(record);
            }
            Store.InsertAgentWriteOperation(new AgentWriteOperationRecord
            {
                OperationId = frozen.Request.OperationId,
                PayloadDigest = frozen.Digest,
                CredentialId = "",
                Operation = BrokerApprovalOperation.Organize.WireOperation(),
                CommittedAt = Timestamp(committedAt),
                RequestId = requestId,
                CapabilityDigest = capabilityDigest,
            });
            return new AgentTextWriteResult(frozen.Request.OperationId, "");
        });
    }

    private AgentOrganizationSnapshot OrganizationSnapshot(byte[] key)
    {
        var records = Store.FetchAllCredentialsIncludingRecycled();
        foreach (var record in records)
        {
            if (!CredentialPermissionExtensions.TryParse(record.Permission, out _))
            {
                throw VaultException.Database("Credential has an unknown permission.");
            }
        }
        return new AgentOrganizationSnapshot
        {
            Records = records,
            StoredGroups = VaultGroups.StoredGroups(Store.ConfigValue(VaultGroups.ConfigKey), key),
        };
    }

    private static string OrganizationApprovalDigest(string requestDigest,
        IReadOnlyList<CredentialRecord> before, IReadOnlyList<CredentialRecord> after,
        IReadOnlyList<AgentOrganizationGroupState> groups, IReadOnlyList<string> finalGroups,
        BrokerOrganizationSummary summary)
    {
        var node = new JsonObject
        {
            ["requestDigest"] = requestDigest,
            ["before"] = new JsonArray(before.Select(record => VaultJson.RecordNode(record)).ToArray()),
            ["after"] = new JsonArray(after.Select(record => VaultJson.RecordNode(record)).ToArray()),
            ["groups"] = new JsonArray(groups.Select(group => (JsonNode)VaultJson.GroupStateNode(group)).ToArray()),
            ["finalGroups"] = new JsonArray(finalGroups.Select(value => (JsonNode)JsonValue.Create(value)!).ToArray()),
            ["summary"] = VaultJson.OrganizationSummaryNode(summary),
        };
        return VaultJson.Digest(node);
    }
}

