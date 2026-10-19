using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AskKey.Broker.Approval;
using AskKey.Broker.Protocol;

namespace AskKey.Vault;

public sealed partial class Vault
{
    /// <summary>
    /// Port of VaultStore+AgentWrites.commitAgentTextWrite. The receipt and the
    /// credential mutation are committed in one SQLite transaction so a crash
    /// can never leave a consumed approval without its business write.
    /// </summary>
    private AgentTextWriteResult StoreCommit(
        FrozenAgentTextWrite frozen,
        string requestId,
        string capabilityDigest,
        string? resultDigest = null,
        byte[]? expectedFileDigest = null,
        byte[]? credentialGroupsKey = null)
    {
        return Store.RunTransaction(() =>
        {
            var committedAt = CurrentDate;
            if (Store.FetchAgentWriteOperation(frozen.OperationId) is { } existing)
            {
                if (existing.PayloadDigest != frozen.Digest || existing.ResultDigest != resultDigest)
                {
                    throw new BrokerApprovalException(BrokerApprovalError.PayloadMismatch);
                }
                if (existing.RequestId != requestId || existing.CapabilityDigest != capabilityDigest)
                {
                    throw new BrokerApprovalException(BrokerApprovalError.RequestNotFound);
                }
                return new AgentTextWriteResult(existing.OperationId, existing.CredentialId);
            }

            if (frozen.GroupAssignment is { } group)
            {
                var key = credentialGroupsKey ?? throw VaultException.Locked();
                CommitAgentGroupInTransaction(group, frozen.Summary?.CreatesGroup == true, key);
            }

            string credentialId;
            BrokerApprovalOperation operation;
            switch (frozen.Mutation)
            {
                case FrozenAgentTextMutation.Create create:
                    Store.InsertCredentialInTransaction(create.Record);
                    credentialId = create.Record.Id;
                    operation = BrokerApprovalOperation.Create;
                    break;
                case FrozenAgentTextMutation.Modify modify:
                {
                    var current = Store.FetchCredentialRawInTransaction(modify.Record.Id);
                    if (current is null || current.DeletedAt is not null
                        || current.UpdatedAt != modify.ExpectedUpdatedAt)
                    {
                        throw VaultException.Changed();
                    }
                    _ = Store.Authenticate(current);
                    if (expectedFileDigest is not null
                        && (current.ContentDigest is null
                            || !CryptographicOperations.FixedTimeEquals(current.ContentDigest, expectedFileDigest)))
                    {
                        throw VaultException.Changed();
                    }
                    RequireAgentWriteCredentialUnexpired(current, committedAt);
                    Store.UpdateCredentialInTransaction(modify.Record);
                    credentialId = modify.Record.Id;
                    operation = BrokerApprovalOperation.Modify;
                    break;
                }
                case FrozenAgentTextMutation.Delete delete:
                {
                    var current = Store.FetchCredentialRawInTransaction(delete.CredentialId);
                    if (current is null || current.DeletedAt is not null
                        || current.UpdatedAt != delete.ExpectedUpdatedAt)
                    {
                        throw VaultException.Changed();
                    }
                    _ = Store.Authenticate(current);
                    RequireAgentWriteCredentialUnexpired(current, committedAt);
                    var recycled = current.Clone();
                    recycled.DeletedAt = delete.DeletedAt;
                    Store.UpdateCredentialInTransaction(recycled);
                    credentialId = delete.CredentialId;
                    operation = BrokerApprovalOperation.Delete;
                    break;
                }
                default:
                    throw VaultException.Database("Unknown frozen Agent write.");
            }

            Store.InsertAgentWriteOperation(new AgentWriteOperationRecord
            {
                OperationId = frozen.OperationId,
                PayloadDigest = frozen.Digest,
                CredentialId = credentialId,
                Operation = operation.WireOperation(),
                CommittedAt = Timestamp(committedAt),
                RequestId = requestId,
                CapabilityDigest = capabilityDigest,
                ResultDigest = resultDigest,
            });
            return new AgentTextWriteResult(frozen.OperationId, credentialId);
        });
    }

    /// <summary>
    /// Merges one approved group creation against transaction-current state.
    /// Unrelated App group edits are preserved and the approved spelling wins.
    /// </summary>
    private void CommitAgentGroupInTransaction(string name, bool creationApproved, byte[] key)
    {
        var stored = VaultGroups.StoredGroups(Store.ConfigValueInTransaction(VaultGroups.ConfigKey), key)
            .ToList();
        var groups = new HashSet<string>(stored, StringComparer.Ordinal);
        foreach (var raw in Store.FetchAllCredentialsIncludingRecycledRaw())
        {
            var record = Store.Authenticate(raw);
            if (record.EncryptedGroupName is { } encrypted)
            {
                groups.Add(VaultCrypto.Decrypt(encrypted, key));
            }
        }

        var normalized = CredentialName.Normalized(name);
        string? matching = null;
        foreach (var candidate in groups.OrderBy(value => value, StringComparer.Ordinal))
        {
            if (CredentialName.Normalized(candidate) != normalized) continue;
            matching = candidate;
            break;
        }
        if (matching is not null)
        {
            if (matching != name) throw VaultException.Changed();
        }
        else if (!creationApproved)
        {
            throw VaultException.Changed();
        }

        if (stored.Contains(name, StringComparer.Ordinal)) return;
        stored.Add(name);
        stored.Sort(StringComparer.Ordinal);
        var json = new JsonArray(stored.Select(value => (JsonNode)JsonValue.Create(value)!).ToArray());
        var encryptedGroups = VaultCrypto.Encrypt(VaultJson.Canonical(json), key);
        Store.SetConfigValueInTransaction(VaultGroups.ConfigKey, Convert.ToBase64String(encryptedGroups));
    }

    private void RequireAgentWriteCredentialUnexpired(CredentialRecord record, DateTimeOffset now)
    {
        if (record.ExpiresAt is not { } storedExpiry) return;
        if (ParseExpiry(storedExpiry) <= now) throw VaultException.Unavailable();
    }
}

