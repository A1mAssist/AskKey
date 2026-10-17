using AskKey.Broker.Approval;
using AskKey.Broker.Protocol;
using AskKey.Vault;
using Microsoft.Win32;

namespace AskKeyApp.Tests;

public sealed class ApprovalDetailsContentTests
{
    [Fact]
    public void ReadRowsNameTheRequesterAndShowTheDelivery()
    {
        var request = new BrokerApprovalOperationRequest("op", "credential", "credential",
            BrokerApprovalOperation.Read, "digest",
            credentialName: "Staging API", callerName: "Codex", callerPurpose: "Deploy staging",
            display: new BrokerApprovalOperationRequest.Display("deploy.sh", @"C:\repo", "deploy.sh",
                new[] { "STAGING_KEY" }, Array.Empty<string>()));
        var pending = new BrokerPendingApproval("request", "capability", request);

        var rows = ApprovalDetailsContent.Build(pending, null, null, null, false, 30);

        Assert.Contains(rows, row => row.Label == "Requested by" && row.Text.Contains("Codex"));
        Assert.Contains(rows, row => row.Label == "Purpose" && row.Text.Contains("Deploy staging"));
        Assert.Contains(rows, row => row.Label == "Command" && row.Code && row.Text == "deploy.sh");
        Assert.Contains(rows, row => row.Label == "Hands over" && row.Text.Contains("STAGING_KEY"));
    }

    [Fact]
    public void ModifyMarksReplacedValuesAndOnlyRevealsChangedValues()
    {
        var write = new BrokerCredentialWriteSummary
        {
            CredentialName = "Staging API",
            Operation = BrokerApprovalOperation.Modify,
            Before = new[]
            {
                Component("Token", "value-1"),
                Component("Region", "value-2"),
            },
            After = new[]
            {
                Component("Token", "value-3"),
                Component("Region", "value-2"),
            },
            BeforeDigest = "before",
            AfterDigest = "after",
        };
        var request = new BrokerApprovalOperationRequest("op", "credential", "credential",
            BrokerApprovalOperation.Modify, "digest",
            credentialName: "Staging API", callerName: "Codex");
        var pending = new BrokerPendingApproval("request", "capability", request);

        var rows = ApprovalDetailsContent.Build(pending, write, null, null, false, 30);

        Assert.True(ApprovalDetailsContent.HasRevealableValue(write));
        Assert.Contains(rows, row => row.Label == "Changes"
            && row.Text.Contains("Token · Replaced")
            && !row.Text.Contains("Region"));
    }

    [Fact]
    public void OrganizationMergeUsesTheBatchSummary()
    {
        var organization = new BrokerOrganizationSummary(new[]
        {
            new BrokerOrganizationSummary.Operation
            {
                OperationKind = BrokerOrganizationSummary.Kind.MergeGroup,
                From = "Old",
                To = "New",
                Members = 2,
                Nonvisible = 1,
                TargetMembers = 1,
                TargetNonvisible = 0,
            },
        });
        var request = new BrokerApprovalOperationRequest("op", "library", "library",
            BrokerApprovalOperation.Organize, "digest", callerName: "Codex");
        var pending = new BrokerPendingApproval("request", "capability", request);

        var rows = ApprovalDetailsContent.Build(pending, null, organization, null, false, 30);

        Assert.Contains(rows, row => row.Label == "Step 1"
            && row.Text.Contains("Codex asked to rename")
            && row.Text.Contains("Old")
            && row.Text.Contains("New"));
        Assert.Equal("1 step, one of which merges groups, affecting 1 hidden credential",
            ApprovalDetailsContent.Subtitle(pending, null, organization));
    }

    [Fact]
    public void DestructiveModifyUsesTheDestructiveAction()
    {
        var write = new BrokerCredentialWriteSummary
        {
            CredentialName = "Staging API",
            Operation = BrokerApprovalOperation.Modify,
            Before = new[] { Component("Token", "value-1") },
            After = Array.Empty<BrokerCredentialComponentSummary>(),
        };

        Assert.True(ApprovalDetailsContent.IsDestructive(BrokerApprovalOperation.Modify, write, null));
    }

    [Fact]
    public void DeleteIsDestructiveEvenWithoutAFrozenSummary()
    {
        Assert.True(ApprovalDetailsContent.IsDestructive(BrokerApprovalOperation.Delete, null, null));
    }

    [Fact]
    public void ValueOnlyModifyNamesTheReplacedValueAndUsesReplace()
    {
        var write = new BrokerCredentialWriteSummary
        {
            CredentialName = "Staging API",
            Operation = BrokerApprovalOperation.Modify,
            Before = new[] { Component("Token", "value-1") },
            After = new[] { Component("Token", "value-2") },
        };
        var request = new BrokerApprovalOperationRequest("op", "credential", "credential",
            BrokerApprovalOperation.Modify, "digest",
            credentialName: "Staging API", callerName: "Codex");
        var pending = new BrokerPendingApproval("request", "capability", request);

        Assert.True(ApprovalDetailsContent.IsValueOnlyModify(write));
        Assert.Equal("“Codex” wants to replace the value of “Staging API”",
            ApprovalDetailsContent.Title(pending, write));
    }

    private static BrokerCredentialComponentSummary Component(string name, string digest) => new()
    {
        Name = name,
        PayloadKind = BrokerCatalogPayloadKind.Text,
        ByteCount = 4,
        Delivery = CredentialComponentDelivery.EnvironmentVariable("VALUE"),
        Masked = true,
        ValueDigest = digest,
    };
}

public sealed class SessionPrivacyPolicyTests
{
    [Theory]
    [InlineData(SessionSwitchReason.SessionLock, true)]
    [InlineData(SessionSwitchReason.ConsoleDisconnect, true)]
    [InlineData(SessionSwitchReason.RemoteDisconnect, true)]
    [InlineData(SessionSwitchReason.SessionUnlock, false)]
    [InlineData(SessionSwitchReason.ConsoleConnect, false)]
    public void DetailedApprovalsAreHiddenWhileTheSessionIsAway(SessionSwitchReason reason, bool hidden)
    {
        Assert.Equal(hidden, SessionPrivacyPolicy.HidesDetailedApprovals(reason));
    }

    [Theory]
    [InlineData(SessionSwitchReason.SessionUnlock, true)]
    [InlineData(SessionSwitchReason.ConsoleConnect, true)]
    [InlineData(SessionSwitchReason.RemoteConnect, true)]
    [InlineData(SessionSwitchReason.SessionLock, false)]
    public void DetailedApprovalsReturnOnlyAfterTheSessionIsBack(SessionSwitchReason reason, bool restored)
    {
        Assert.Equal(restored, SessionPrivacyPolicy.RestoresDetailedApprovals(reason));
    }
}
