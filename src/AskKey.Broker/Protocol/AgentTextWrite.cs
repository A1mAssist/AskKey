using AskKey.Broker.Approval;

namespace AskKey.Broker.Protocol;

public sealed class BrokerOrganizationOperation
{
    public enum Kind
    {
        Move,
        CreateGroup,
        RenameGroup,
        DeleteGroup,
    }

    public Kind OperationKind { get; }
    public string? Credential { get; }
    public string? Group { get; }
    public string? From { get; }
    public string? To { get; }
    public string? Name { get; }

    private BrokerOrganizationOperation(
        Kind kind,
        string? credential = null,
        string? group = null,
        string? from = null,
        string? to = null,
        string? name = null)
    {
        OperationKind = kind;
        Credential = credential;
        Group = group;
        From = from;
        To = to;
        Name = name;
    }

    public static BrokerOrganizationOperation Move(string credential, string? group)
        => new(Kind.Move, credential: credential, group: group);

    public static BrokerOrganizationOperation CreateGroup(string name) => new(Kind.CreateGroup, name: name);

    public static BrokerOrganizationOperation RenameGroup(string from, string to)
        => new(Kind.RenameGroup, from: from, to: to);

    public static BrokerOrganizationOperation DeleteGroup(string name) => new(Kind.DeleteGroup, name: name);

    /// <summary>Every identity or group name is non-empty and within the field cap.</summary>
    public bool IsBounded
    {
        get
        {
            var names = OperationKind switch
            {
                Kind.Move => new[] { Credential, Group }.Where(n => n is not null)!,
                Kind.CreateGroup => new[] { Name },
                Kind.DeleteGroup => new[] { Name },
                Kind.RenameGroup => new[] { From, To },
                _ => Array.Empty<string?>(),
            };
            return names.All(name => name is { Length: > 0 }
                && BrokerWire.Utf8Length(name) <= BrokerLimits.MaximumFieldBytes);
        }
    }
}

public sealed class BrokerOrganizationSummary
{
    public enum Kind
    {
        Move,
        CreateGroup,
        ExistingGroup,
        RenameGroup,
        MergeGroup,
        DeleteGroup,
    }

    public sealed class Operation
    {
        public required Kind OperationKind { get; init; }
        public string? Credential { get; init; }
        public string? From { get; init; }
        public string? To { get; init; }
        public string? Name { get; init; }
        public int Members { get; init; }
        public int Nonvisible { get; init; }
        public int TargetMembers { get; init; }
        public int TargetNonvisible { get; init; }
    }

    public IReadOnlyList<Operation> Operations { get; }

    public BrokerOrganizationSummary(IReadOnlyList<Operation> operations) => Operations = operations;
}

public enum AgentTextWriteActionKind
{
    Organize,
    CreateBundle,
    ModifyBundle,
    Create,
    Modify,
    Delete,
}

public sealed class AgentTextWriteAction
{
    public AgentTextWriteActionKind ActionKind { get; }
    public IReadOnlyList<BrokerOrganizationOperation>? OrganizationOperations { get; }
    public string? Name { get; }
    public string? Value { get; }
    public IReadOnlyList<BrokerCredentialComponentInput>? Components { get; }
    public IReadOnlyList<BrokerCredentialComponentChange>? Changes { get; }
    public string? UsageInstructions { get; }
    /// <summary>Null preserves the group on a bundle action.</summary>
    public string? Group { get; }
    public BrokerCredentialGroupChange? GroupChange { get; }

    private AgentTextWriteAction(
        AgentTextWriteActionKind kind,
        IReadOnlyList<BrokerOrganizationOperation>? organizationOperations = null,
        string? name = null,
        string? value = null,
        IReadOnlyList<BrokerCredentialComponentInput>? components = null,
        IReadOnlyList<BrokerCredentialComponentChange>? changes = null,
        string? usageInstructions = null,
        string? group = null,
        BrokerCredentialGroupChange? groupChange = null)
    {
        ActionKind = kind;
        OrganizationOperations = organizationOperations;
        Name = name;
        Value = value;
        Components = components;
        Changes = changes;
        UsageInstructions = usageInstructions;
        Group = group;
        GroupChange = groupChange;
    }

    public static AgentTextWriteAction Organize(IReadOnlyList<BrokerOrganizationOperation> operations)
        => new(AgentTextWriteActionKind.Organize, organizationOperations: operations);

    public static AgentTextWriteAction CreateBundle(
        string name,
        IReadOnlyList<BrokerCredentialComponentInput> components,
        string? usageInstructions = null,
        string? group = null)
        => new(AgentTextWriteActionKind.CreateBundle, name: name, components: components,
            usageInstructions: usageInstructions, group: group);

    public static AgentTextWriteAction ModifyBundle(
        string name,
        IReadOnlyList<BrokerCredentialComponentChange>? changes = null,
        string? usageInstructions = null,
        BrokerCredentialGroupChange? group = null)
        => new(AgentTextWriteActionKind.ModifyBundle, name: name,
            changes: changes ?? Array.Empty<BrokerCredentialComponentChange>(),
            usageInstructions: usageInstructions, groupChange: group);

    public static AgentTextWriteAction Create(string name, string value)
        => new(AgentTextWriteActionKind.Create, name: name, value: value);

    public static AgentTextWriteAction Modify(string name, string value)
        => new(AgentTextWriteActionKind.Modify, name: name, value: value);

    public static AgentTextWriteAction Delete(string name)
        => new(AgentTextWriteActionKind.Delete, name: name);

    public BrokerApprovalOperation ApprovalOperation => ActionKind switch
    {
        AgentTextWriteActionKind.Create or AgentTextWriteActionKind.CreateBundle => BrokerApprovalOperation.Create,
        AgentTextWriteActionKind.Modify or AgentTextWriteActionKind.ModifyBundle => BrokerApprovalOperation.Modify,
        AgentTextWriteActionKind.Delete => BrokerApprovalOperation.Delete,
        _ => BrokerApprovalOperation.Organize,
    };

    public string CredentialName => ActionKind == AgentTextWriteActionKind.Organize ? string.Empty : Name ?? string.Empty;
}

public sealed class AgentTextWriteRequest
{
    public string OperationId { get; }
    public AgentTextWriteAction Action { get; }
    public string? CallerName { get; }
    public string? CallerPurpose { get; }

    public AgentTextWriteRequest(
        string operationId,
        AgentTextWriteAction action,
        string? callerName = null,
        string? callerPurpose = null)
    {
        OperationId = operationId;
        Action = action;
        CallerName = callerName;
        CallerPurpose = callerPurpose;
    }

    public IReadOnlyList<BrokerComponentFileReference> ComponentFileReferences
    {
        get
        {
            IEnumerable<BrokerCredentialComponentInput> components = Action.ActionKind switch
            {
                AgentTextWriteActionKind.CreateBundle => Action.Components ?? Array.Empty<BrokerCredentialComponentInput>(),
                AgentTextWriteActionKind.ModifyBundle => (Action.Changes ?? Array.Empty<BrokerCredentialComponentChange>())
                    .Where(change => change.Upsert is not null).Select(change => change.Upsert!),
                _ => Array.Empty<BrokerCredentialComponentInput>(),
            };
            return components.Where(component => component.Value.File is not null)
                .Select(component => component.Value.File!).ToArray();
        }
    }

    /// <summary>Swift's pre-receipt field bound: no value may exceed the 4 KiB field cap.</summary>
    public bool IsBounded
    {
        get
        {
            if (Action.ActionKind == AgentTextWriteActionKind.Organize)
            {
                var operations = Action.OrganizationOperations ?? Array.Empty<BrokerOrganizationOperation>();
                return OperationId.Length > 0 && operations.Count is > 0 and <= 64
                    && operations.All(operation => operation.IsBounded)
                    && new[] { OperationId, CallerName, CallerPurpose }
                        .Where(text => text is not null)
                        .All(text => BrokerWire.Utf8Length(text!) <= BrokerLimits.MaximumFieldBytes);
            }

            var fields = new List<string>();
            void AddField(string? text)
            {
                if (text is not null) fields.Add(text);
            }

            AddField(OperationId);
            AddField(Action.CredentialName);
            AddField(CallerName);
            AddField(CallerPurpose);
            if (OperationId.Length == 0 || Action.CredentialName.Length == 0) return false;

            bool AddComponent(BrokerCredentialComponentInput component)
            {
                if (component.Name.Length == 0) return false;
                fields.Add(component.Name);
                if (component.Delivery.EnvironmentVariableName is { } variable) fields.Add(variable);
                if (component.Value.Text is { } text)
                {
                    fields.Add(text);
                }
                else if (component.Value.File is { } reference)
                {
                    if (reference.UploadId.Length == 0 || reference.Capability.Length == 0
                        || reference.Digest.Length != 64
                        || !reference.Digest.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
                    {
                        return false;
                    }
                    fields.Add(reference.UploadId);
                    fields.Add(reference.Capability);
                    fields.Add(reference.Digest);
                }
                return true;
            }

            switch (Action.ActionKind)
            {
                case AgentTextWriteActionKind.Create:
                case AgentTextWriteActionKind.Modify:
                    AddField(Action.Value);
                    break;
                case AgentTextWriteActionKind.Delete:
                    break;
                case AgentTextWriteActionKind.CreateBundle:
                    var components = Action.Components ?? Array.Empty<BrokerCredentialComponentInput>();
                    if (components.Count == 0 || components.Count > 64 || !components.All(AddComponent)) return false;
                    AddField(Action.UsageInstructions);
                    AddField(Action.Group);
                    break;
                case AgentTextWriteActionKind.ModifyBundle:
                    var changes = Action.Changes ?? Array.Empty<BrokerCredentialComponentChange>();
                    if ((changes.Count == 0 && Action.UsageInstructions is null && Action.GroupChange is null)
                        || changes.Count > 64)
                    {
                        return false;
                    }
                    AddField(Action.UsageInstructions);
                    if (Action.GroupChange is { Name: { } groupName }) AddField(groupName);
                    foreach (var change in changes)
                    {
                        if (change.Upsert is { } upsert)
                        {
                            if (!AddComponent(upsert)) return false;
                        }
                        else if (change.RemoveName is { } removeName)
                        {
                            if (removeName.Length == 0) return false;
                            fields.Add(removeName);
                        }
                    }
                    break;
            }
            return fields.All(text => BrokerWire.Utf8Length(text) <= BrokerLimits.MaximumFieldBytes);
        }
    }
}

public sealed class AgentTextWriteSubmission : IEquatable<AgentTextWriteSubmission>
{
    public string OperationId { get; }
    public string RequestId { get; }
    public string Capability { get; }
    public BrokerRequestState State { get; }
    public int RetryCount { get; }

    public AgentTextWriteSubmission(
        string operationId,
        string requestId,
        string capability,
        BrokerRequestState state,
        int retryCount)
    {
        OperationId = operationId;
        RequestId = requestId;
        Capability = capability;
        State = state;
        RetryCount = retryCount;
    }

    public bool Equals(AgentTextWriteSubmission? other)
        => other is not null && OperationId == other.OperationId && RequestId == other.RequestId
            && Capability == other.Capability && State == other.State && RetryCount == other.RetryCount;

    public override bool Equals(object? obj) => Equals(obj as AgentTextWriteSubmission);

    public override int GetHashCode() => HashCode.Combine(OperationId, RequestId, State, RetryCount);
}

public sealed class AgentTextWriteResult : IEquatable<AgentTextWriteResult>
{
    public string OperationId { get; }
    public string CredentialId { get; }
    public BrokerRequestState State { get; }

    public AgentTextWriteResult(string operationId, string credentialId, BrokerRequestState state = BrokerRequestState.Completed)
    {
        OperationId = operationId;
        CredentialId = credentialId;
        State = state;
    }

    public bool Equals(AgentTextWriteResult? other)
        => other is not null && OperationId == other.OperationId
            && CredentialId == other.CredentialId && State == other.State;

    public override bool Equals(object? obj) => Equals(obj as AgentTextWriteResult);

    public override int GetHashCode() => HashCode.Combine(OperationId, CredentialId, State);
}

public sealed class AgentTextWriteRequestOutcome : IEquatable<AgentTextWriteRequestOutcome>
{
    public AgentTextWriteSubmission? Submission { get; }
    public AgentTextWriteResult? CompletedResult { get; }

    private AgentTextWriteRequestOutcome(AgentTextWriteSubmission? submission, AgentTextWriteResult? completed)
    {
        Submission = submission;
        CompletedResult = completed;
    }

    public static AgentTextWriteRequestOutcome Submitted(AgentTextWriteSubmission submission)
        => new(submission, null);

    public static AgentTextWriteRequestOutcome Completed(AgentTextWriteResult result) => new(null, result);

    public bool Equals(AgentTextWriteRequestOutcome? other)
        => other is not null && Equals(Submission, other.Submission) && Equals(CompletedResult, other.CompletedResult);

    public override bool Equals(object? obj) => Equals(obj as AgentTextWriteRequestOutcome);

    public override int GetHashCode() => HashCode.Combine(Submission?.RequestId, CompletedResult?.CredentialId);
}

