using AskKey.Broker.Approval;
using AskKey.Broker.Protocol;

namespace AskKey.Vault;

/// <summary>Swift FrozenAgentTextMutation: the exact business write frozen at approval time.</summary>
internal abstract record FrozenAgentTextMutation
{
    public sealed record Create(CredentialRecord Record) : FrozenAgentTextMutation;
    public sealed record Modify(CredentialRecord Record, string ExpectedUpdatedAt) : FrozenAgentTextMutation;
    public sealed record Delete(string CredentialId, string ExpectedUpdatedAt, string DeletedAt)
        : FrozenAgentTextMutation;
}

internal sealed class FrozenAgentTextWrite
{
    public required string OperationId { get; init; }
    public required string Digest { get; init; }
    public required BrokerApprovalOperationRequest ApprovalRequest { get; init; }
    public DateTimeOffset? CredentialExpiresAt { get; init; }
    public required FrozenAgentTextMutation Mutation { get; init; }
    public CredentialRecord? BeforeRecord { get; init; }
    public BrokerCredentialWriteSummary? Summary { get; init; }
    public string? GroupAssignment { get; init; }
}

internal sealed class FrozenAgentOrganization
{
    public required AgentTextWriteRequest Request { get; init; }
    public required string Digest { get; init; }
    public required BrokerApprovalOperationRequest ApprovalRequest { get; init; }
    public DateTimeOffset? CredentialExpiresAt { get; init; }
    public required IReadOnlyList<CredentialRecord> Before { get; init; }
    public required IReadOnlyList<CredentialRecord> After { get; init; }
    public required IReadOnlyCollection<string> MovedCredentialIds { get; init; }
    public required IReadOnlyList<AgentOrganizationGroupState> Groups { get; init; }
    public required IReadOnlyList<string> FinalGroupNames { get; init; }
    public required BrokerOrganizationSummary Summary { get; init; }
}

internal sealed class AgentOrganizationGroupState
{
    public required string NormalizedName { get; init; }
    public required IReadOnlyList<string> Names { get; init; }
    public required IReadOnlyList<string> StoredNames { get; init; }
    public required IReadOnlyList<string> MemberIds { get; init; }
    public required IReadOnlyList<string> MemberDigests { get; init; }

    public bool SameAs(AgentOrganizationGroupState other)
        => NormalizedName == other.NormalizedName
            && Names.SequenceEqual(other.Names, StringComparer.Ordinal)
            && StoredNames.SequenceEqual(other.StoredNames, StringComparer.Ordinal)
            && MemberIds.SequenceEqual(other.MemberIds, StringComparer.Ordinal)
            && MemberDigests.SequenceEqual(other.MemberDigests, StringComparer.Ordinal);
}

internal sealed class AgentOrganizationSnapshot
{
    public required IReadOnlyList<CredentialRecord> Records { get; init; }
    public required IReadOnlyList<string> StoredGroups { get; init; }

    public Dictionary<string, string?> Assignments(byte[] key)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var record in Records)
        {
            if (record.EncryptedGroupName is { } encrypted)
            {
                result[record.Id] = VaultCrypto.Decrypt(encrypted, key);
            }
        }
        return result;
    }

    public IReadOnlyList<AgentOrganizationGroupState> GroupStates(IEnumerable<string> names, byte[] key)
    {
        var assignments = Assignments(key);
        var allNames = new HashSet<string>(StoredGroups, StringComparer.Ordinal);
        foreach (var value in assignments.Values)
        {
            if (value is not null) allNames.Add(value);
        }
        var result = new List<AgentOrganizationGroupState>();
        foreach (var normalized in names.Select(CredentialName.Normalized).Distinct(StringComparer.Ordinal)
                     .OrderBy(value => value, StringComparer.Ordinal))
        {
            var members = Records
                .Where(record => assignments.TryGetValue(record.Id, out var assigned)
                    && assigned is not null
                    && CredentialName.Normalized(assigned) == normalized)
                .OrderBy(record => record.Id, StringComparer.Ordinal)
                .ToList();
            result.Add(new AgentOrganizationGroupState
            {
                NormalizedName = normalized,
                Names = allNames.Where(name => CredentialName.Normalized(name) == normalized)
                    .OrderBy(name => name, StringComparer.Ordinal).ToList(),
                StoredNames = StoredGroups.Where(name => CredentialName.Normalized(name) == normalized)
                    .OrderBy(name => name, StringComparer.Ordinal).ToList(),
                MemberIds = members.Select(record => record.Id).ToList(),
                MemberDigests = members.Select(record => VaultJson.Digest(VaultJson.RecordNode(record))).ToList(),
            });
        }
        return result;
    }
}

internal sealed class FrozenAgentTextWriteRegistry
{
    private readonly object _operationLock = new();
    private readonly object _lock = new();
    private readonly Dictionary<string, FrozenAgentTextWrite> _entries = new(StringComparer.Ordinal);

    public T Synchronized<T>(Func<T> body)
    {
        lock (_operationLock) return body();
    }

    public (FrozenAgentTextWrite Write, bool Inserted) Store(FrozenAgentTextWrite candidate)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(candidate.OperationId, out var existing))
            {
                if (existing.Digest != candidate.Digest)
                {
                    throw new BrokerApprovalException(BrokerApprovalError.PayloadMismatch);
                }
                return (existing, false);
            }
            if (_entries.Count >= BrokerLimits.MaximumPendingApprovalRequests)
            {
                throw new BrokerApprovalException(BrokerApprovalError.CapacityReached);
            }
            _entries[candidate.OperationId] = candidate;
            return (candidate, true);
        }
    }

    public FrozenAgentTextWrite? Entry(string operationId)
    {
        lock (_lock) return _entries.GetValueOrDefault(operationId);
    }

    public void Remove(string operationId)
    {
        lock (_operationLock)
        {
            lock (_lock) _entries.Remove(operationId);
        }
    }
}

internal sealed class FrozenAgentOrganizationRegistry
{
    private readonly object _operationLock = new();
    private readonly object _lock = new();
    private readonly Dictionary<string, FrozenAgentOrganization> _entries = new(StringComparer.Ordinal);

    public T Synchronized<T>(Func<T> body)
    {
        lock (_operationLock) return body();
    }

    public void Store(FrozenAgentOrganization candidate)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(candidate.Request.OperationId, out var existing))
            {
                if (existing.Digest != candidate.Digest)
                {
                    throw new BrokerApprovalException(BrokerApprovalError.PayloadMismatch);
                }
                return;
            }
            if (_entries.Count >= BrokerLimits.MaximumPendingApprovalRequests)
            {
                throw new BrokerApprovalException(BrokerApprovalError.CapacityReached);
            }
            _entries[candidate.Request.OperationId] = candidate;
        }
    }

    public FrozenAgentOrganization? Entry(string operationId)
    {
        lock (_lock) return _entries.GetValueOrDefault(operationId);
    }

    public void Remove(string operationId)
    {
        lock (_operationLock)
        {
            lock (_lock) _entries.Remove(operationId);
        }
    }
}

public sealed class BrokerCredentialComponentSummary
{
    public required string Name { get; init; }
    public required BrokerCatalogPayloadKind PayloadKind { get; init; }
    public required int ByteCount { get; init; }
    public required CredentialComponentDelivery Delivery { get; init; }
    public required bool Masked { get; init; }
    public required string ValueDigest { get; init; }
}

/// <summary>
/// Presentation-only projection of a frozen write. AskKey.Broker has no
/// counterpart yet, so the Vault owns the type the facade returns.
/// </summary>
public sealed class BrokerCredentialWriteSummary
{
    public required string CredentialName { get; init; }
    public required BrokerApprovalOperation Operation { get; init; }
    public required IReadOnlyList<BrokerCredentialComponentSummary> Before { get; init; }
    public required IReadOnlyList<BrokerCredentialComponentSummary> After { get; init; }
    public string? BeforeDigest { get; init; }
    public string? AfterDigest { get; init; }
    public string? BeforeUsageInstructions { get; init; }
    public string? AfterUsageInstructions { get; init; }
    public string? BeforeGroup { get; init; }
    public string? AfterGroup { get; init; }
    public bool CreatesGroup { get; init; }
}

public sealed class FrozenCredentialWrite
{
    public required string CredentialName { get; init; }
    public required BrokerApprovalOperation Operation { get; init; }
    public required IReadOnlyList<CredentialComponentInput> Before { get; init; }
    public required IReadOnlyList<CredentialComponentInput> After { get; init; }
}

