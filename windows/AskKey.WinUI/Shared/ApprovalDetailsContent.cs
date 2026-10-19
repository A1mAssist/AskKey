using AskKey.Broker.Approval;
using AskKey.Broker.FileWrite;
using AskKey.Broker.Protocol;
using AskKey.Vault;

namespace AskKeyApp;

internal sealed record ApprovalDetailRow(string Label, string Text, bool Code = false);

/// <summary>
/// Presentation-only rows for the approval card. The frozen summaries remain
/// the source of truth; this type formats them without loading any values.
/// </summary>
internal static class ApprovalDetailsContent
{
    private enum ChangeKind
    {
        Unchanged,
        Changed,
        New,
        Replaced,
        Removed,
    }

    private sealed record ComponentChange(string Name, ChangeKind Kind);

    public static string Title(BrokerPendingApproval pending, BrokerCredentialWriteSummary? write = null)
    {
        var request = pending.Request;
        var requester = Requester(request);
        var credential = pending.DisplayCredentialName;
        return request.Operation switch
        {
            BrokerApprovalOperation.Read => Loc.F("“{0}” wants to use “{1}”", requester, credential),
            BrokerApprovalOperation.Create => Loc.F("“{0}” wants to create the credential “{1}”", requester, credential),
            BrokerApprovalOperation.Modify => ModifyTitle(requester, credential, write),
            BrokerApprovalOperation.Delete => Loc.F("“{0}” wants to delete “{1}”", requester, credential),
            _ => Loc.F("“{0}” wants to organize your groups", requester),
        };
    }

    private static string ModifyTitle(string requester, string credential,
        BrokerCredentialWriteSummary? write)
    {
        if (write is null || !IsValueOnlyModify(write))
        {
            return Loc.F("“{0}” wants to change “{1}”", requester, credential);
        }

        var replaced = ModifiedComponents(write)
            .Where(change => change.Kind == ChangeKind.Replaced)
            .Select(change => change.Name)
            .ToList();
        if (replaced.Count < write.After.Count)
        {
            return Loc.F("“{0}” wants to replace {1} in “{2}”", requester,
                string.Join(Loc.T("List separator"), replaced), credential);
        }
        return replaced.Count == 1
            ? Loc.F("“{0}” wants to replace the value of “{1}”", requester, credential)
            : Loc.F("“{0}” wants to replace the values of “{1}”", requester, credential);
    }

    public static string? Subtitle(BrokerPendingApproval pending,
        BrokerCredentialWriteSummary? write,
        BrokerOrganizationSummary? organization)
    {
        var request = pending.Request;
        return request.Operation switch
        {
            BrokerApprovalOperation.Read => null,
            BrokerApprovalOperation.Create => write?.AfterGroup is { } group
                ? Loc.F(write.CreatesGroup ? "In the new group “{0}”" : "In the group “{0}”", group)
                : Loc.T("Create a new credential."),
            BrokerApprovalOperation.Modify => IsValueOnlyModify(write)
                ? Loc.T(ModifiedComponents(write!).Count(change => change.Kind == ChangeKind.Replaced) == 1
                    ? "The old value can't be recovered"
                    : "The old values can't be recovered")
                : Loc.T("This change updates the credential."),
            BrokerApprovalOperation.Delete => Loc.T("Moves to the Recycle Bin; restorable for 30 days"),
            _ => OrganizationSubtitle(organization),
        };
    }

    private static string OrganizationSubtitle(BrokerOrganizationSummary? organization)
    {
        if (organization is null) return Loc.T("The frozen group changes are unavailable.");
        var steps = organization.Operations.Count;
        var subtitle = steps == 1
            ? Loc.F("{0} step", steps)
            : Loc.F("{0} steps", steps);
        var merges = organization.Operations.Count(operation =>
            operation.OperationKind == BrokerOrganizationSummary.Kind.MergeGroup);
        if (merges == 1)
        {
            subtitle = Loc.F("{0}, one of which merges groups", subtitle);
        }
        else if (merges > 1)
        {
            subtitle = Loc.F("{0}, {1} of which merge groups", subtitle, merges);
        }

        var hidden = HiddenCredentials(organization);
        if (hidden == 1)
        {
            subtitle = Loc.F("{0}, affecting 1 hidden credential", subtitle);
        }
        else if (hidden > 1)
        {
            subtitle = Loc.F("{0}, affecting {1} hidden credentials", subtitle, hidden);
        }
        return subtitle;
    }

    private static int HiddenCredentials(BrokerOrganizationSummary summary)
    {
        var counted = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var total = 0;
        int Leave(string? group, int hidden)
        {
            var key = Key(group);
            if (!counted.Remove(key, out var alreadyCounted)) return hidden;
            return Math.Max(0, hidden - alreadyCounted);
        }

        foreach (var operation in summary.Operations)
        {
            switch (operation.OperationKind)
            {
                case BrokerOrganizationSummary.Kind.RenameGroup:
                    total += Leave(operation.From, operation.Nonvisible);
                    counted[Key(operation.To)] = operation.Nonvisible;
                    break;
                case BrokerOrganizationSummary.Kind.MergeGroup:
                    total += Leave(operation.From, operation.Nonvisible);
                    var key = Key(operation.To);
                    counted[key] = counted.GetValueOrDefault(key) + operation.Nonvisible;
                    break;
                case BrokerOrganizationSummary.Kind.DeleteGroup:
                    total += Leave(operation.Name, operation.Nonvisible);
                    break;
            }
        }
        return total;
    }

    private static string Key(string? group)
        => (group ?? "").Normalize();

    public static IReadOnlyList<ApprovalDetailRow> Build(BrokerPendingApproval pending,
        BrokerCredentialWriteSummary? write,
        BrokerOrganizationSummary? organization,
        BrokerFileWriteSummary? file,
        bool timedAllowanceEnabled,
        int timedAllowanceMinutes)
    {
        var request = pending.Request;
        var rows = new List<ApprovalDetailRow>
        {
            new(Loc.T("Requested by"), Loc.F("{0} (not verified)", Requester(request))),
        };
        if (!string.IsNullOrWhiteSpace(request.CallerPurpose))
        {
            rows.Add(new ApprovalDetailRow(Loc.T("Purpose"),
                Loc.F("{0} (not verified)", request.CallerPurpose)));
        }

        switch (request.Operation)
        {
            case BrokerApprovalOperation.Read:
                if (request.DisplayValue is { } display)
                {
                    rows.Add(new ApprovalDetailRow(Loc.T("Command"), display.CommandLine, Code: true));
                    if (!string.IsNullOrWhiteSpace(display.WorkingDirectory))
                    {
                        rows.Add(new ApprovalDetailRow(Loc.T("Runs in"), display.WorkingDirectory, Code: true));
                    }
                    rows.Add(new ApprovalDetailRow(Loc.T("Hands over"),
                        HandsOver(display, pending.DisplayCredentialName)));
                }
                if (timedAllowanceEnabled)
                {
                    rows.Add(new ApprovalDetailRow(Loc.F("{0} minutes", timedAllowanceMinutes),
                        Loc.F("For {0} minutes, any agent or command in your Windows account can read this credential without asking. Changing or deleting it still requires your approval.",
                            timedAllowanceMinutes)));
                }
                break;
            case BrokerApprovalOperation.Organize:
                if (organization is null)
                {
                    rows.Add(new ApprovalDetailRow(Loc.T("Group changes"),
                        Loc.T("The frozen group changes are unavailable.")));
                }
                else
                {
                    rows.AddRange(OrganizationRows(organization, Requester(request)));
                }
                break;
            case BrokerApprovalOperation.Create:
            case BrokerApprovalOperation.Modify:
            case BrokerApprovalOperation.Delete:
                if (file is not null)
                {
                    rows.AddRange(FileRows(file));
                }
                else if (write is not null)
                {
                    rows.AddRange(WriteRows(write));
                }
                else
                {
                    rows.Add(new ApprovalDetailRow(Loc.T("Contents"),
                        Loc.T("The frozen contents are unavailable.")));
                }
                break;
        }

        return rows;
    }

    public static bool HasRevealableValue(BrokerCredentialWriteSummary? summary)
        => summary is not null && summary.Operation switch
        {
            BrokerApprovalOperation.Create => summary.After.Count > 0,
            BrokerApprovalOperation.Modify => ModifiedComponents(summary)
                .Any(change => change.Kind is ChangeKind.New or ChangeKind.Replaced),
            _ => false,
        };

    public static bool IsValueOnlyModify(BrokerCredentialWriteSummary? summary)
    {
        if (summary is not { Operation: BrokerApprovalOperation.Modify }
            || !string.Equals(summary.BeforeUsageInstructions ?? "",
                summary.AfterUsageInstructions ?? "", StringComparison.Ordinal)
            || !string.Equals(summary.BeforeGroup, summary.AfterGroup, StringComparison.Ordinal))
        {
            return false;
        }
        var changes = ModifiedComponents(summary);
        return changes.Count > 0 && changes.All(change => change.Kind == ChangeKind.Replaced);
    }

    public static bool IsDestructive(BrokerApprovalOperation operation,
        BrokerCredentialWriteSummary? write,
        BrokerOrganizationSummary? organization)
    {
        if (operation == BrokerApprovalOperation.Delete
            || write?.Operation == BrokerApprovalOperation.Delete)
        {
            return true;
        }
        if (write?.Operation == BrokerApprovalOperation.Modify
            && ModifiedComponents(write).Any(change => change.Kind is ChangeKind.Replaced or ChangeKind.Removed))
        {
            return true;
        }
        return organization?.Operations.Any(change =>
            change.OperationKind == BrokerOrganizationSummary.Kind.MergeGroup) == true;
    }

    private static IEnumerable<ApprovalDetailRow> FileRows(BrokerFileWriteSummary file)
    {
        yield return new ApprovalDetailRow(Loc.T("Operation"), OperationLabel(file.Operation));
        yield return new ApprovalDetailRow(Loc.T("File"),
            Loc.F("{0} · {1}", file.OriginalFilename, FormatBytes(file.ByteCount)));
        yield return new ApprovalDetailRow(Loc.T("Digest"), Short(file.Digest), Code: true);
        if (file.PreviousDigest is { } previous)
        {
            yield return new ApprovalDetailRow(Loc.T("Previous digest"), Short(previous), Code: true);
        }
    }

    private static IEnumerable<ApprovalDetailRow> WriteRows(BrokerCredentialWriteSummary write)
    {
        yield return new ApprovalDetailRow(Loc.T("Operation"), OperationLabel(write.Operation));
        if (write.Operation == BrokerApprovalOperation.Delete)
        {
            yield return new ApprovalDetailRow(Loc.T("Contents"), ComponentList(write.Before));
            yield break;
        }

        if (write.Operation == BrokerApprovalOperation.Create)
        {
            yield return new ApprovalDetailRow(Loc.T("Contents"), ComponentList(write.After));
            if (!string.IsNullOrWhiteSpace(write.AfterUsageInstructions))
            {
                yield return new ApprovalDetailRow(Loc.T("Instructions"), write.AfterUsageInstructions);
            }
            yield return new ApprovalDetailRow(Loc.T("Group"), GroupValue(write.AfterGroup, write.CreatesGroup));
            yield break;
        }

        var changes = ModifiedComponents(write);
        if (changes.Count > 0)
        {
            yield return new ApprovalDetailRow(Loc.T("Changes"), string.Join("\n",
                changes.Select(change => Loc.F("{0} · {1}", change.Name, ChangeText(change.Kind)))));
        }

        var beforeInstructions = write.BeforeUsageInstructions ?? "";
        var afterInstructions = write.AfterUsageInstructions ?? "";
        if (!string.Equals(beforeInstructions, afterInstructions, StringComparison.Ordinal))
        {
            yield return new ApprovalDetailRow(Loc.T("Instructions"),
                Loc.F("Before:\n{0}\n\nAfter:\n{1}", beforeInstructions, afterInstructions));
        }

        if (!string.Equals(write.BeforeGroup, write.AfterGroup, StringComparison.Ordinal))
        {
            yield return new ApprovalDetailRow(Loc.T("Group"),
                Loc.F("{0} → {1}", GroupValue(write.BeforeGroup, false),
                    GroupValue(write.AfterGroup, write.CreatesGroup)));
        }
    }

    private static List<ComponentChange> ModifiedComponents(BrokerCredentialWriteSummary write)
    {
        var before = write.Before
            .GroupBy(component => component.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var afterNames = new HashSet<string>(write.After.Select(component => component.Name),
            StringComparer.OrdinalIgnoreCase);
        var changes = new List<ComponentChange>();

        foreach (var current in write.After)
        {
            if (!before.TryGetValue(current.Name, out var previous))
            {
                changes.Add(new ComponentChange(current.Name, ChangeKind.New));
                continue;
            }

            var kind = !string.Equals(previous.ValueDigest, current.ValueDigest, StringComparison.Ordinal)
                ? ChangeKind.Replaced
                : previous.Delivery != current.Delivery
                  || previous.Masked != current.Masked
                  || !string.Equals(previous.Name, current.Name, StringComparison.Ordinal)
                    ? ChangeKind.Changed
                    : ChangeKind.Unchanged;
            if (kind != ChangeKind.Unchanged)
            {
                changes.Add(new ComponentChange(current.Name, kind));
            }
        }

        foreach (var previous in write.Before)
        {
            if (!afterNames.Contains(previous.Name))
            {
                changes.Add(new ComponentChange(previous.Name, ChangeKind.Removed));
            }
        }
        return changes;
    }

    private static IEnumerable<ApprovalDetailRow> OrganizationRows(BrokerOrganizationSummary summary,
        string requester)
    {
        if (summary.Operations.Count == 0)
        {
            yield return new ApprovalDetailRow(Loc.T("Group changes"), Loc.T("No group changes."));
            yield break;
        }

        for (var index = 0; index < summary.Operations.Count; index++)
        {
            var operation = summary.Operations[index];
            var (title, detail) = operation.OperationKind switch
            {
                BrokerOrganizationSummary.Kind.Move => (
                    Loc.F("Move the credential {0} from {1} to {2}",
                        Quote(operation.Credential ?? ""), GroupText(operation.From), GroupText(operation.To)),
                    (string?)null),
                BrokerOrganizationSummary.Kind.CreateGroup => (
                    Loc.F("Create the group {0}", GroupText(operation.Name)), (string?)null),
                BrokerOrganizationSummary.Kind.ExistingGroup => (
                    Loc.F("Create the group {0}", GroupText(operation.Name)),
                    Loc.T("This group already exists, so nothing is created or changed.") + " "
                    + Loc.F("It has {0}.", Members(operation.Members, operation.Nonvisible))),
                BrokerOrganizationSummary.Kind.RenameGroup => (
                    Loc.F("Rename the group {0} to {1}", GroupText(operation.From), GroupText(operation.To)),
                    operation.Members == 0
                        ? Loc.T("When it's renamed, the group is empty.")
                        : Loc.F("When it's renamed, the group has {0}.",
                            Members(operation.Members, operation.Nonvisible))),
                BrokerOrganizationSummary.Kind.MergeGroup => (
                    Loc.F("{0} asked to rename {1} to {2}; {3} already exists and is hidden from it, so the groups merge.",
                        requester, GroupText(operation.From), GroupText(operation.To), GroupText(operation.To)),
                    Loc.F("{0} disappears and {1} will have {2}.", GroupText(operation.From),
                        GroupText(operation.To),
                        Members(operation.Members + operation.TargetMembers,
                            operation.Nonvisible + operation.TargetNonvisible))
                    + " " + Loc.T("A merge can't be undone automatically.")),
                BrokerOrganizationSummary.Kind.DeleteGroup => (
                    Loc.F("Delete the group {0}", GroupText(operation.Name)),
                    operation.Members == 0
                        ? Loc.T("The group is empty.")
                        : Loc.F("Its {0} won't be deleted and will become {1}.",
                            Members(operation.Members, operation.Nonvisible), GroupText(null))),
                _ => (Loc.T("Change a group"), (string?)null),
            };
            var text = detail is null ? title : title + "\n" + detail;
            yield return new ApprovalDetailRow(Loc.F("Step {0}", index + 1), text);
        }
    }

    private static string HandsOver(BrokerApprovalOperationRequest.Display display, string credentialName)
    {
        if (display.EnvironmentVariables is null || display.TemporaryFileVariables is null)
        {
            return Loc.F("The items of {0} that are set to be given to programs (names are shown after you approve)",
                GroupText(credentialName));
        }

        var lines = display.EnvironmentVariables
            .Select(variable => Loc.F("{0} → environment variable {1}", GroupText(credentialName), variable))
            .Concat(display.TemporaryFileVariables
                .Select(variable => Loc.F("{0} → temporary file {1}", GroupText(credentialName), variable)))
            .ToList();
        return lines.Count == 0
            ? Loc.T("Nothing from this credential is given to the command")
            : string.Join("\n", lines);
    }

    private static string ComponentList(IReadOnlyList<BrokerCredentialComponentSummary> components)
        => components.Count == 0
            ? Loc.T("No contents.")
            : string.Join("\n", components.Select(component =>
                Loc.F("{0} · {1}", component.Name, DeliveryText(component.Delivery))));

    private static string DeliveryText(CredentialComponentDelivery delivery) => delivery.Kind switch
    {
        CredentialDeliveryKind.EnvironmentVariable =>
            Loc.F("given to programs as environment variable {0}", delivery.Variable ?? ""),
        CredentialDeliveryKind.TemporaryFile =>
            Loc.F("given to programs as a temporary file (path in {0})", delivery.Variable ?? ""),
        _ => Loc.T("kept in Ask Key only, never given to agents"),
    };

    private static string GroupValue(string? group, bool createsGroup)
        => GroupText(group) + (createsGroup ? " " + Loc.T("(new group)") : "");

    private static string GroupText(string? group)
        => "“" + (string.IsNullOrWhiteSpace(group) ? Loc.T("Ungrouped") : group) + "”";

    private static string Quote(string value) => "“" + value + "”";

    private static string Members(int count, int hidden)
    {
        var members = Loc.F(count == 1 ? "{0} credential" : "{0} credentials", count);
        return hidden > 0 ? Loc.F("{0} ({1} hidden from agents)", members, hidden) : members;
    }

    private static string Requester(BrokerApprovalOperationRequest request)
        => string.IsNullOrWhiteSpace(request.CallerName) ? Loc.T("Local Agent") : request.CallerName;

    private static string OperationLabel(BrokerApprovalOperation operation) => operation switch
    {
        BrokerApprovalOperation.Read => Loc.T("Read"),
        BrokerApprovalOperation.Create => Loc.T("Create"),
        BrokerApprovalOperation.Modify => Loc.T("Modify"),
        BrokerApprovalOperation.Delete => Loc.T("Delete"),
        _ => Loc.T("Organize"),
    };

    private static string ChangeText(ChangeKind kind) => kind switch
    {
        ChangeKind.Changed => Loc.T("Changed"),
        ChangeKind.New => Loc.T("New"),
        ChangeKind.Replaced => Loc.T("Replaced"),
        ChangeKind.Removed => Loc.T("Removed"),
        _ => Loc.T("Unchanged"),
    };

    private static string FormatBytes(int bytes) => bytes < 1024
        ? $"{bytes} B"
        : bytes < 1024 * 1024
            ? $"{bytes / 1024.0:0.#} KB"
            : $"{bytes / (1024.0 * 1024.0):0.#} MB";

    private static string Short(string digest)
        => digest.Length <= 24 ? digest : digest[..24] + "…";
}
