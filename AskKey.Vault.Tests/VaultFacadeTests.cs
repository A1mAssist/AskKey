using System.Security.Cryptography;
using System.Text;
using AskKey.Broker.Approval;
using AskKey.Broker.Protocol;
using AskKey.Broker.Runtime;
using AskKey.Broker.Transport;
using AskKey.Vault;

namespace AskKey.Vault.Tests;

public sealed class VaultFacadeTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class Harness : IDisposable
    {
        public TestDirectory Root { get; }
        public VaultStore Store { get; }
        public byte[] Key { get; }
        public Vault Vault { get; }

        public Harness(Func<DateTimeOffset>? now = null, VaultConfiguration? configuration = null,
            IAppKeyStore? keyStore = null, TimeSpan? idleLimit = null)
        {
            Root = new TestDirectory();
            Key = TestCredentials.Key(40);
            var clock = now ?? (() => Now);
            if (configuration is not null)
            {
                PrivateFileSystem.CreatePrivateDirectory(configuration.ApplicationSupportDirectory);
            }
            Store = TestCredentials.NewStore(configuration?.VaultFile ?? Root.Combine("library.db"), Key);
            Vault = new Vault(Store, Key, clock,
                new BrokerApprovalStateMachine(clock: clock, authenticate: _ => true),
                Root.Combine("deliveries"), configuration, keyStore);
            Vault.ManagementSessionIdleLimit = idleLimit ?? TimeSpan.FromMinutes(5);
            Vault.BeginManagementSession(ManagementAuthenticator.Allow);
        }

        public void Dispose()
        {
            Vault.Dispose();
            Root.Dispose();
        }
    }

    [Fact]
    public void ManagementSessionHonorsTheConfiguredIdleLimit()
    {
        var now = Now;
        using var harness = new Harness(() => now, idleLimit: TimeSpan.FromSeconds(30));

        now = now.AddSeconds(31);

        Assert.False(harness.Vault.HasActiveManagementSession);
    }

    [Fact]
    public void ActiveManagementSessionReusesWindowsHelloConfirmation()
    {
        using var harness = new Harness();
        var created = harness.Vault.CreateTextCredential(new TextCredentialInput
        {
            Name = "Reusable Token",
            Value = "synthetic-secret",
        }, ManagementAuthenticator.Allow);
        var prompts = 0;

        // Metadata edits stay inside the session; granting Allow outlives it, so it asks again.
        harness.Vault.UpdateTextCredential(created.Id, new TextCredentialInput
        {
            Name = "Reusable Token",
            Value = "synthetic-secret",
            UsageInstructions = "updated",
        }, new ManagementAuthenticator(_ => { prompts++; return true; }));
        Assert.Equal(0, prompts);

        harness.Vault.UpdateCredentialPermission(created.Id, CredentialPermission.Allowed,
            new ManagementAuthenticator(_ => { prompts++; return true; }));
        Assert.Equal(1, prompts);
    }

    [Fact]
    public void AllowedCreationAndPermissionChangesAskForTheGrantReason()
    {
        using var harness = new Harness();
        harness.Vault.CreateTextCredential(new TextCredentialInput
        {
            Name = "Seed Credential",
            Value = "synthetic-seed",
        }, ManagementAuthenticator.Allow);
        var prompts = 0;
        var created = harness.Vault.CreateTextCredential(new TextCredentialInput
        {
            Name = "Allowed Grant",
            Value = "synthetic-secret",
            Permission = CredentialPermission.Allowed,
        }, new ManagementAuthenticator(_ => { prompts++; return true; }));
        Assert.Equal(1, prompts);

        harness.Vault.UpdateCredentialMetadata(created.Id, "Allowed Grant Renamed",
            "", null, CredentialPermission.Allowed, null,
            new ManagementAuthenticator(_ => { prompts++; return true; }));
        Assert.Equal(1, prompts);
        Assert.Equal("Allowed Grant Renamed",
            harness.Vault.ListTextCredentials().Single(item => item.Id == created.Id).Name);

        harness.Vault.UpdateCredentialPermission(created.Id, CredentialPermission.Ask,
            new ManagementAuthenticator(_ => { prompts++; return true; }));
        harness.Vault.UpdateCredentialPermission(created.Id, CredentialPermission.Allowed,
            new ManagementAuthenticator(_ => { prompts++; return true; }));
        Assert.Equal(2, prompts);
    }

    [Fact]
    public void RuntimeApprovalUsesWindowsCommandLineQuoting()
    {
        using var harness = new Harness();
        var vault = harness.Vault;
        vault.CreateTextCredential(new TextCredentialInput
        {
            Name = "Quoted Request",
            Value = "synthetic-secret",
            EnvironmentVariable = "QUOTED_TOKEN",
            Permission = CredentialPermission.Ask,
        }, ManagementAuthenticator.Allow);

        var resolution = vault.BrokerTextCredentials(new BrokerTextRunRequest(
            "quoted-run",
            new[] { "tool.exe", "a b", "say \"hi\"" },
            new[] { "Quoted Request" }), new BrokerCancellation());
        Assert.Equal(BrokerTextCredentialResolution.Kind.ApprovalRequired, resolution.ResolutionKind);
        var pending = Assert.Single(vault.ApprovalRequests.PendingRequests());
        var commandLine = pending.Request.DisplayValue?.CommandLine ?? "";
        Assert.Equal("tool.exe \"a b\" \"say \\\"hi\\\"\"", commandLine);
        Assert.DoesNotContain("'", commandLine);
    }
    [Fact]
    public void RevealingAValueAsksAgainInsideAnActiveManagementSession()
    {
        using var harness = new Harness();
        var created = harness.Vault.CreateTextCredential(new TextCredentialInput
        {
            Name = "Reveal Token",
            Value = "synthetic-secret",
        }, ManagementAuthenticator.Allow);
        var prompts = 0;

        harness.Vault.RevealTextCredential(created.Id,
            new ManagementAuthenticator(_ => { prompts++; return true; }));

        Assert.Equal(1, prompts);
    }

    [Fact]
    public void ExpiredManagementSessionRequiresConfirmationAgain()
    {
        var now = Now;
        using var harness = new Harness(() => now, idleLimit: TimeSpan.FromMinutes(5));
        now = now.AddMinutes(6);
        var prompts = 0;

        var error = Assert.Throws<VaultException>(() => harness.Vault.PauseAgentAccess(
            new ManagementAuthenticator(_ => { prompts++; return false; })));

        Assert.Equal(VaultErrorCode.ManagementAuthenticationRequired, error.Code);
        Assert.Equal(1, prompts);
    }

    [Fact]
    public void ChangingTheIdleLimitRestartsAnActiveSessionFromNow()
    {
        var now = Now;
        using var harness = new Harness(() => now, idleLimit: TimeSpan.FromMinutes(5));
        now = now.AddMinutes(4);

        harness.Vault.SetManagementSessionIdleLimit(TimeSpan.FromMinutes(15));
        now = now.AddMinutes(10);

        Assert.True(harness.Vault.HasActiveManagementSession);
    }

    private static AgentTextWriteSubmission Submit(Vault vault, AgentTextWriteRequest request)
    {
        var outcome = vault.RequestAgentTextWrite(request);
        Assert.Null(outcome.CompletedResult);
        return Assert.IsType<AgentTextWriteSubmission>(outcome.Submission);
    }

    private static AgentTextWriteResult ApproveAndCommit(Vault vault, AgentTextWriteRequest request,
        AgentTextWriteSubmission submission)
    {
        vault.ApprovalRequests.Decide(submission.RequestId, submission.Capability,
            BrokerApprovalDecision.Once);
        return vault.CommitAgentTextWrite(request, submission.RequestId, submission.Capability);
    }

    [Fact]
    public void TextCrudRevealPermissionMetadataAndExpiryRoundTrip()
    {
        using var harness = new Harness();
        var vault = harness.Vault;
        var expires = Now.AddYears(1);
        var created = vault.CreateTextCredential(new TextCredentialInput
        {
            Name = "Synthetic Token",
            Value = "synthetic-secret",
            UsageInstructions = "Use for tests",
            PrivateNotes = "private-note",
            GroupName = "Work",
            EnvironmentVariable = "SYNTHETIC_TOKEN",
            Permission = CredentialPermission.Ask,
            ExpiresAt = expires,
        }, ManagementAuthenticator.Allow);

        var listed = Assert.Single(vault.ListTextCredentials());
        Assert.Equal(created.Id, listed.Id);
        Assert.Null(listed.Value);
        Assert.Null(listed.PrivateNotes);
        Assert.Equal(CredentialPermission.Ask, listed.Permission);
        Assert.Equal(1, vault.StoredCredentialCount());
        Assert.Equal(expires, Assert.Single(vault.ListCredentialExpirySnapshots()).ExpiresAt);

        var revealed = vault.RevealTextCredential(created.Id, ManagementAuthenticator.Allow);
        Assert.Equal("synthetic-secret", revealed.Value);
        Assert.Equal("private-note", revealed.PrivateNotes);

        vault.UpdateCredentialPermission(created.Id, CredentialPermission.Allowed,
            ManagementAuthenticator.Allow);
        vault.UpdateCredentialMetadata(created.Id, "Renamed Token", "Updated guidance", "Renamed Group",
            CredentialPermission.Hidden, Now.AddDays(30), ManagementAuthenticator.Allow);
        var updated = vault.RevealTextCredential(created.Id, ManagementAuthenticator.Allow);
        Assert.Equal("Renamed Token", updated.Name);
        Assert.Equal("Updated guidance", updated.UsageInstructions);
        Assert.Equal("Renamed Group", updated.GroupName);
        Assert.Equal(CredentialPermission.Hidden, updated.Permission);
        Assert.Equal(Now.AddDays(30), updated.ExpiresAt);
        var hidden = Assert.Throws<VaultException>(() =>
            vault.AuthorizeAgentCredential("Renamed Token", AgentCredentialOperation.Read,
                new BrokerCallerClaim()));
        Assert.Equal(VaultErrorCode.CredentialUnavailable, hidden.Code);

        vault.UpdateCredentialPermission(created.Id, CredentialPermission.Allowed,
            ManagementAuthenticator.Allow);
        Assert.Equal(AgentCredentialAuthorization.Allowed,
            vault.AuthorizeAgentCredential("Renamed Token", AgentCredentialOperation.Read,
                new BrokerCallerClaim()));
    }

    [Fact]
    public void GroupsAndTrashLifecyclePreserveTheContract()
    {
        using var harness = new Harness();
        var vault = harness.Vault;
        vault.CreateCredentialGroup("Work", ManagementAuthenticator.Allow);
        var first = vault.CreateTextCredential(new TextCredentialInput
        {
            Name = "First",
            Value = "one",
            GroupName = "Work",
        }, ManagementAuthenticator.Allow);
        var second = vault.CreateTextCredential(new TextCredentialInput
        {
            Name = "Second",
            Value = "two",
            GroupName = "Work",
        }, ManagementAuthenticator.Allow);
        Assert.Equal(new[] { "Work" }, vault.ListCredentialGroups());

        vault.UpdateCredentialGroup(first.Id, "Personal", ManagementAuthenticator.Allow);
        Assert.Equal("Personal", vault.RevealTextCredential(first.Id, ManagementAuthenticator.Allow).GroupName);
        vault.DeleteCredentialGroup("Work", ManagementAuthenticator.Allow);
        Assert.Null(vault.RevealTextCredential(second.Id, ManagementAuthenticator.Allow).GroupName);
        Assert.Contains("Personal", vault.ListCredentialGroups());

        vault.DeleteTextCredential(second.Id, ManagementAuthenticator.Allow);
        Assert.Equal(second.Id, Assert.Single(vault.ListRecycledTextCredentials()).Id);
        vault.RestoreRecycledTextCredential(second.Id, ManagementAuthenticator.Allow);
        Assert.Single(vault.ListTextCredentials(), item => item.Id == second.Id);

        vault.DeleteTextCredential(first.Id, ManagementAuthenticator.Allow);
        vault.PermanentlyDeleteRecycledTextCredential(first.Id, ManagementAuthenticator.Allow);
        Assert.DoesNotContain(vault.ListRecycledTextCredentials(), item => item.Id == first.Id);
    }

    [Fact]
    public void FileAndBundleCreationUpdateAndImportReplacementRoundTrip()
    {
        using var harness = new Harness();
        var vault = harness.Vault;
        var file = vault.CreateFileCredential(new FileCredentialInput
        {
            Name = "Synthetic File",
            Snapshot = new FileImport.FrozenFile("synthetic.pem", Encoding.UTF8.GetBytes("file-one")),
            UsageInstructions = "File guidance",
            PrivateNotes = "file-private",
            GroupName = "Files",
            EnvironmentVariable = "SYNTHETIC_FILE",
            Permission = CredentialPermission.Allowed,
        }, ManagementAuthenticator.Allow);
        var revealedFile = vault.RevealTextCredential(file.Id, ManagementAuthenticator.Allow);
        Assert.Equal("file-one", Encoding.UTF8.GetString(revealedFile.FileBytes!));
        Assert.Equal("synthetic.pem", revealedFile.OriginalFilename);
        Assert.Equal(CredentialPayloadKind.File, revealedFile.PayloadKind);

        var updatedFile = vault.UpdateFileCredential(file.Id, new FileCredentialInput
        {
            Name = "Synthetic File",
            Snapshot = new FileImport.FrozenFile("updated.pem", Encoding.UTF8.GetBytes("file-two")),
            EnvironmentVariable = "SYNTHETIC_FILE",
            Permission = CredentialPermission.Allowed,
        }, ManagementAuthenticator.Allow);
        Assert.Equal("updated.pem", vault.RevealTextCredential(file.Id, ManagementAuthenticator.Allow).OriginalFilename);
        Assert.Equal("file-two", Encoding.UTF8.GetString(
            vault.RevealTextCredential(updatedFile.Id, ManagementAuthenticator.Allow).FileBytes!));

        var bundle = vault.CreateBundleCredential(new BundleCredentialInput
        {
            Name = "Synthetic Bundle",
            Components = new[]
            {
                new CredentialComponentInput("TOKEN", new CredentialComponentValue.Text("bundle-value"),
                    CredentialComponentDelivery.EnvironmentVariable("BUNDLE_TOKEN")),
                new CredentialComponentInput("CERT", new CredentialComponentValue.File("cert.pem",
                    Encoding.UTF8.GetBytes("certificate")), CredentialComponentDelivery.TemporaryFile("BUNDLE_CERT")),
            },
            UsageInstructions = "Bundle guidance",
            GroupName = "Bundles",
            Permission = CredentialPermission.Ask,
        }, ManagementAuthenticator.Allow);
        var revealedBundle = vault.RevealTextCredential(bundle.Id, ManagementAuthenticator.Allow);
        Assert.Equal(CredentialPayloadKind.Bundle, revealedBundle.PayloadKind);
        Assert.Equal(2, revealedBundle.Components.Count);
        Assert.Equal("bundle-value", ((CredentialComponentValue.Text)revealedBundle.Components[0].Value!).Value);

        var replaced = vault.ReplaceImportedBundleCredential(bundle.Id, new[]
        {
            new CredentialComponentInput("ONLY", new CredentialComponentValue.Text("replacement"),
                CredentialComponentDelivery.EnvironmentVariable("ONLY_VALUE")),
        }, ManagementAuthenticator.Allow);
        Assert.Single(vault.RevealTextCredential(replaced.Id, ManagementAuthenticator.Allow).Components);
    }

    [Fact]
    public void BrokerCatalogAndTextResolutionHideSecretsAndHonorApprovals()
    {
        using var harness = new Harness();
        var vault = harness.Vault;
        var allowed = vault.CreateTextCredential(new TextCredentialInput
        {
            Name = "Allowed Token",
            Value = "allowed-value",
            UsageInstructions = "Visible guidance",
            GroupName = "Visible Group",
            EnvironmentVariable = "ALLOWED_TOKEN",
            Permission = CredentialPermission.Allowed,
        }, ManagementAuthenticator.Allow);
        vault.CreateTextCredential(new TextCredentialInput
        {
            Name = "Hidden Token",
            Value = "hidden-value",
            Permission = CredentialPermission.Hidden,
        }, ManagementAuthenticator.Allow);
        vault.CreateTextCredential(new TextCredentialInput
        {
            Name = "Ask Token",
            Value = "ask-value",
            EnvironmentVariable = "ASK_TOKEN",
            Permission = CredentialPermission.Ask,
        }, ManagementAuthenticator.Allow);
        vault.CreateCredentialGroup("Empty Group", ManagementAuthenticator.Allow);
        Assert.Equal(new[] { "Empty Group", "Visible Group" }, vault.BrokerCredentialGroups());

        var catalog = vault.BrokerCredentialCatalog();
        Assert.Equal(new[] { "Allowed Token", "Ask Token" }, catalog.Select(item => item.Name).ToArray());
        Assert.Equal(allowed.Id, catalog[0].CredentialId);
        Assert.DoesNotContain("allowed-value", catalog.Select(item => item.UsageInstructions));
        Assert.Equal(AgentCredentialAuthorization.Allowed,
            vault.AuthorizeAgentCredential("Allowed Token", AgentCredentialOperation.Read,
                new BrokerCallerClaim()));

        var run = new BrokerTextRunRequest("run-1", new[] { "synthetic-command" },
            new[] { "Allowed Token" });
        var resolved = vault.BrokerTextCredentials(run, new BrokerCancellation());
        Assert.Equal(BrokerTextCredentialResolution.Kind.Resolved, resolved.ResolutionKind);
        Assert.Equal("allowed-value", Assert.Single(resolved.Credentials).Value);
        resolved.DeliveryLease?.Dispose();

        var askRun = new BrokerTextRunRequest("run-2", new[] { "synthetic-command" },
            new[] { "Ask Token" });
        var required = vault.BrokerTextCredentials(askRun, new BrokerCancellation());
        Assert.Equal(BrokerTextCredentialResolution.Kind.ApprovalRequired, required.ResolutionKind);
        var ticket = Assert.Single(required.Tickets);
        vault.ApprovalRequests.Decide(ticket.RequestId, ticket.Capability, BrokerApprovalDecision.Once);
        var approved = vault.BrokerTextCredentials(askRun, new BrokerCancellation());
        Assert.Equal(BrokerTextCredentialResolution.Kind.Resolved, approved.ResolutionKind);
        Assert.Equal("ask-value", Assert.Single(approved.Credentials).Value);
        approved.DeliveryLease?.Dispose();
    }

    [Fact]
    public void FileRuntimeDeliveryIsMaterializedAndCleanedUp()
    {
        using var harness = new Harness();
        var vault = harness.Vault;
        vault.CreateFileCredential(new FileCredentialInput
        {
            Name = "Runtime File",
            Snapshot = new FileImport.FrozenFile("runtime.txt", Encoding.UTF8.GetBytes("runtime-bytes")),
            EnvironmentVariable = "RUNTIME_FILE",
            Permission = CredentialPermission.Allowed,
        }, ManagementAuthenticator.Allow);

        var resolution = vault.BrokerTextCredentials(new BrokerTextRunRequest("run-file",
            new[] { "synthetic-command" }, new[] { "Runtime File" }), new BrokerCancellation());
        Assert.Equal(BrokerTextCredentialResolution.Kind.Resolved, resolution.ResolutionKind);
        var credential = Assert.Single(resolution.Credentials);
        Assert.True(File.Exists(credential.Value));
        Assert.Equal("runtime-bytes", File.ReadAllText(credential.Value));
        resolution.DeliveryLease!.Dispose();
        Assert.False(File.Exists(credential.Value));
    }

    [Fact]
    public void PauseResumeAndAccessRecordFailureAreVisible()
    {
        using var harness = new Harness();
        var vault = harness.Vault;
        vault.PauseAgentAccess(ManagementAuthenticator.Allow);
        Assert.True(vault.IsAgentAccessPaused());
        var paused = Assert.Throws<VaultException>(() => vault.BrokerCredentialCatalog());
        Assert.Equal(VaultErrorCode.AgentAccessPaused, paused.Code);
        vault.ResumeAgentAccess(ManagementAuthenticator.Allow);
        Assert.False(vault.IsAgentAccessPaused());

        vault.RecordCredentialAccess(CredentialAccessEvent.Create(Now, "credential-id",
            CredentialAccessOperation.Catalog, CredentialAccessResult.Allowed, "caller", "purpose"));
        Assert.Contains(vault.ListCredentialAccessRecords(), item => item.CredentialId == "credential-id");
        vault.ClearCredentialAccessRecords(ManagementAuthenticator.Allow);
        Assert.Empty(vault.ListCredentialAccessRecords());

        harness.Store.FailCredentialAccessRecordWritesForTesting();
        vault.RecordCredentialAccess(CredentialAccessEvent.Create(Now, "credential-id",
            CredentialAccessOperation.RuntimeRead, CredentialAccessResult.Allowed, "caller", "purpose"));
        Assert.True(vault.HasCredentialAccessRecordWriteFailure());
    }

    [Fact]
    public void AgentTextWriteCreateModifyDeleteAndPayloadMismatchAreTransactional()
    {
        using var harness = new Harness();
        var vault = harness.Vault;
        var create = new AgentTextWriteRequest("agent-create",
            AgentTextWriteAction.Create("Agent Token", "agent-secret"));
        var createTicket = Submit(vault, create);
        var mismatch = new AgentTextWriteRequest("agent-create",
            AgentTextWriteAction.Create("Agent Token", "different-secret"));
        var mismatchError = Assert.Throws<BrokerApprovalException>(() =>
            vault.CommitAgentTextWrite(mismatch, createTicket.RequestId, createTicket.Capability));
        Assert.Equal(BrokerApprovalError.PayloadMismatch, mismatchError.Error);
        var created = ApproveAndCommit(vault, create, createTicket);
        Assert.Equal("agent-secret", vault.RevealTextCredential(created.CredentialId,
            ManagementAuthenticator.Allow).Value);

        var modify = new AgentTextWriteRequest("agent-modify",
            AgentTextWriteAction.Modify("Agent Token", "updated-secret"));
        var modifyTicket = Submit(vault, modify);
        var modified = ApproveAndCommit(vault, modify, modifyTicket);
        Assert.Equal(created.CredentialId, modified.CredentialId);
        Assert.Equal("updated-secret", vault.RevealTextCredential(modified.CredentialId,
            ManagementAuthenticator.Allow).Value);

        var delete = new AgentTextWriteRequest("agent-delete", AgentTextWriteAction.Delete("Agent Token"));
        var deleteTicket = Submit(vault, delete);
        _ = ApproveAndCommit(vault, delete, deleteTicket);
        Assert.Empty(vault.ListTextCredentials());
        Assert.Single(vault.ListRecycledTextCredentials());
    }

    [Theory]
    [InlineData(BrokerRequestState.Denied, false)]
    [InlineData(BrokerRequestState.Cancelled, false)]
    [InlineData(BrokerRequestState.Expired, false)]
    [InlineData(BrokerRequestState.Denied, true)]
    [InlineData(BrokerRequestState.Cancelled, true)]
    [InlineData(BrokerRequestState.Expired, true)]
    public void TerminalAgentWritesStillCleanUpAndRecordAccessAfterUiRegistration(
        BrokerRequestState state, bool organization)
    {
        var now = Now;
        using var harness = new Harness(() => now);
        var vault = harness.Vault;
        var uiStates = new List<BrokerRequestState>();
        vault.ApprovalRequests.AddOperationStateChanged((_, _, terminal) => uiStates.Add(terminal));
        vault.ApprovalRequests.ConfigureOperationStateChanged((_, _, _) => { });
        var action = organization
            ? AgentTextWriteAction.Organize(new[] { BrokerOrganizationOperation.CreateGroup("New") })
            : AgentTextWriteAction.Create("Agent Token", "synthetic-secret");
        var request = new AgentTextWriteRequest("terminal-write", action, "Synthetic client", "Test cleanup");
        var ticket = Submit(vault, request);
        Assert.True(organization
            ? vault.AgentOrganizations.Entry(request.OperationId) is not null
            : vault.AgentTextWrites.Entry(request.OperationId) is not null);

        switch (state)
        {
            case BrokerRequestState.Denied:
                vault.ApprovalRequests.Decide(ticket.RequestId, ticket.Capability, BrokerApprovalDecision.Deny);
                break;
            case BrokerRequestState.Cancelled:
                vault.ApprovalRequests.Cancel(ticket.RequestId, ticket.Capability);
                break;
            case BrokerRequestState.Expired:
                now = now.AddHours(1);
                vault.ApprovalRequests.RefreshExpiration();
                break;
        }

        Assert.Null(vault.AgentTextWrites.Entry(request.OperationId));
        Assert.Null(vault.AgentOrganizations.Entry(request.OperationId));
        Assert.Equal(new[] { state }, uiStates);
        Assert.Equal(state, vault.ApprovalRequests.Status(ticket.RequestId, ticket.Capability));
        if (state == BrokerRequestState.Expired)
        {
            vault.BeginManagementSession(ManagementAuthenticator.Allow);
        }
        var record = Assert.Single(vault.ListCredentialAccessRecords());
        Assert.Equal(state == BrokerRequestState.Expired
            ? CredentialAccessResult.Expired : CredentialAccessResult.Denied, record.Result);
        Assert.Equal(organization ? CredentialAccessOperation.Modify : CredentialAccessOperation.Create,
            record.Operation);
        Assert.Equal("Synthetic client", record.CallerHint);
        Assert.Equal("Test cleanup", record.DeclaredPurpose);
        Assert.Empty(vault.ListTextCredentials());
        Assert.Empty(vault.ListCredentialGroups());
    }

    [Fact]
    public void AgentOrganizationBatchFreezesAndCommitsOneApproval()
    {
        using var harness = new Harness();
        var vault = harness.Vault;
        var first = vault.CreateTextCredential(new TextCredentialInput
        {
            Name = "Org A",
            Value = "a",
            GroupName = "Old",
        }, ManagementAuthenticator.Allow);
        vault.CreateTextCredential(new TextCredentialInput
        {
            Name = "Org B",
            Value = "b",
            GroupName = "Old",
        }, ManagementAuthenticator.Allow);

        var request = new AgentTextWriteRequest("organization-1", AgentTextWriteAction.Organize(new[]
        {
            BrokerOrganizationOperation.CreateGroup("New"),
            BrokerOrganizationOperation.Move("Org A", "New"),
        }));
        var ticket = Submit(vault, request);
        var summary = vault.FrozenAgentOrganizationSummary(request.OperationId, ticket.RequestId,
            ticket.Capability);
        Assert.Equal(2, summary.Operations.Count);
        Assert.Equal("New", summary.Operations[0].Name);
        Assert.Equal("New", summary.Operations[1].To);
        var result = ApproveAndCommit(vault, request, ticket);
        Assert.Equal("", result.CredentialId);
        Assert.Equal("New", vault.RevealTextCredential(first.Id, ManagementAuthenticator.Allow).GroupName);
        Assert.Equal(new[] { "New", "Old" }, vault.BrokerCredentialGroups());
        Assert.Contains(vault.ListCredentialAccessRecords(), item =>
            item.CredentialId == first.Id && item.Operation == CredentialAccessOperation.Modify
            && item.Result == CredentialAccessResult.Allowed);
    }

    [Fact]
    public void FileWriteApprovalCommitsNewBytesAndReceipt()
    {
        using var harness = new Harness();
        var vault = harness.Vault;
        var file = vault.CreateFileCredential(new FileCredentialInput
        {
            Name = "Writable File",
            Snapshot = new FileImport.FrozenFile("before.txt", Encoding.UTF8.GetBytes("before")),
            Permission = CredentialPermission.Allowed,
        }, ManagementAuthenticator.Allow);
        var previousDigest = vault.BrokerFileContentDigest(file.Id);
        var bytes = Encoding.UTF8.GetBytes("after");
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var draft = new BrokerFrozenFile
        {
            OperationId = "file-write-1",
            CredentialId = file.Id,
            TargetId = file.Id,
            Operation = BrokerApprovalOperation.Modify,
            PreviousDigest = previousDigest,
            OriginalFilename = "after.txt",
            Bytes = bytes,
            ByteCount = bytes.Length,
            Digest = digest,
        };
        var approvalRequest = new BrokerApprovalOperationRequest("file-write-1", file.Id, file.Id,
            BrokerApprovalOperation.Modify, draft.ApprovalPayloadDigest);
        var ticket = vault.SubmitFileWriteApprovalIfCurrent(file.Id, previousDigest, approvalRequest);
        vault.ApprovalRequests.Decide(ticket.RequestId, ticket.Capability, BrokerApprovalDecision.Once);
        var frozen = new BrokerFrozenFile
        {
            OperationId = "file-write-1",
            CredentialId = file.Id,
            TargetId = file.Id,
            Operation = BrokerApprovalOperation.Modify,
            PreviousDigest = previousDigest,
            OriginalFilename = "after.txt",
            Bytes = bytes,
            ByteCount = bytes.Length,
            Digest = digest,
            ApprovalRequestId = ticket.RequestId,
            ApprovalCapability = ticket.Capability,
        };
        vault.CommitAgentFileWrite(frozen);
        Assert.True(vault.CompletedAgentFileWrite(ticket.RequestId, ticket.Capability, digest));
        Assert.Equal(BrokerRequestState.Completed,
            vault.CommittedAgentFileWriteStatus(ticket.RequestId, ticket.Capability));
        Assert.Equal(digest, vault.BrokerFileContentDigest(file.Id));
        Assert.Equal("after", Encoding.UTF8.GetString(vault.RevealTextCredential(file.Id,
            ManagementAuthenticator.Allow).FileBytes!));
    }

    [Fact]
    public void LocalEraseRemovesLibraryAndKeysAndRecoveryResumesJournal()
    {
        using var root = new TestDirectory();
        var configuration = new VaultConfiguration(root.Combine("appdata"), root.Combine("localdata"));
        PrivateFileSystem.CreatePrivateDirectory(configuration.ApplicationSupportDirectory);
        var key = TestCredentials.Key(41);
        var keys = new MemoryAppKeyStore { AppKey = key };
        using (var store = TestCredentials.NewStore(configuration.VaultFile, key))
        {
            using var vault = new Vault(store, key, () => Now,
                new BrokerApprovalStateMachine(clock: () => Now, authenticate: _ => true),
                root.Combine("deliveries"), configuration, keys);
            vault.BeginManagementSession(ManagementAuthenticator.Allow);
            vault.CreateTextCredential(new TextCredentialInput { Name = "Erase Me", Value = "value" },
                ManagementAuthenticator.Allow);
            vault.EraseLocalLibrary("ERASE", LocalVaultEraseLanguage.English,
                ManagementAuthenticator.Allow);
            Assert.True(vault.IsLocked);
            Assert.False(File.Exists(configuration.VaultFile));
            Assert.Null(keys.AppKey);
            Assert.False(File.Exists(configuration.LocalEraseJournalFile));
        }

        PrivateFileSystem.CreatePrivateDirectory(configuration.ApplicationSupportDirectory);
        var recoveryKey = TestCredentials.Key(42);
        var recoveryKeys = new MemoryAppKeyStore { AppKey = recoveryKey };
        using var recoveryStore = TestCredentials.NewStore(configuration.VaultFile, recoveryKey);
        using var recoveryVault = new Vault(recoveryStore, recoveryKey, () => Now,
            new BrokerApprovalStateMachine(clock: () => Now, authenticate: _ => true),
            root.Combine("deliveries-recovery"), configuration, recoveryKeys);
        var journal = new FileLocalVaultEraseJournalStore(configuration.LocalEraseJournalFile);
        journal.Save(LocalVaultEraseState.DeliveriesCleared);
        recoveryVault.RecoverInterruptedLocalErase();
        Assert.True(recoveryVault.IsLocked);
        Assert.False(File.Exists(configuration.VaultFile));
        Assert.Null(recoveryKeys.AppKey);
        Assert.False(File.Exists(configuration.LocalEraseJournalFile));
    }

    [Fact]
    public void ResetWithoutTheKeyErasesALockedVault()
    {
        using var root = new TestDirectory();
        var configuration = new VaultConfiguration(root.Combine("appdata"), root.Combine("localdata"));
        PrivateFileSystem.CreatePrivateDirectory(configuration.ApplicationSupportDirectory);
        var key = TestCredentials.Key(44);
        var keys = new MemoryAppKeyStore { AppKey = key };
        using var store = TestCredentials.NewStore(configuration.VaultFile, key);
        using var vault = new Vault(store, key, () => Now,
            new BrokerApprovalStateMachine(clock: () => Now, authenticate: _ => true),
            root.Combine("deliveries-reset"), configuration, keys);
        vault.Lock();
        Assert.True(vault.IsLocked);

        var mismatch = Assert.Throws<LocalVaultEraseException>(() =>
            vault.ResetLocalLibrary("erase", LocalVaultEraseLanguage.English));
        Assert.Equal(LocalVaultEraseError.ConfirmationMismatch, mismatch.Error);

        vault.ResetLocalLibrary("ERASE", LocalVaultEraseLanguage.English);

        Assert.True(vault.IsLocked);
        Assert.False(File.Exists(configuration.VaultFile));
        Assert.Null(keys.AppKey);
        Assert.False(File.Exists(configuration.LocalEraseJournalFile));
    }

    [Fact]
    public void InvalidEraseJournalFailsClosed()
    {
        using var root = new TestDirectory();
        var configuration = new VaultConfiguration(root.Combine("appdata"), root.Combine("localdata"));
        PrivateFileSystem.CreatePrivateDirectory(configuration.ApplicationSupportDirectory);
        var key = TestCredentials.Key(43);
        using var store = TestCredentials.NewStore(configuration.VaultFile, key);
        using var vault = new Vault(store, key, () => Now,
            new BrokerApprovalStateMachine(clock: () => Now, authenticate: _ => true),
            root.Combine("deliveries"), configuration, new MemoryAppKeyStore { AppKey = key });
        Directory.CreateDirectory(configuration.LocalEraseJournalFile);
        var exception = Assert.Throws<LocalVaultEraseException>(() => vault.RecoverInterruptedLocalErase());
        Assert.Equal(LocalVaultEraseError.InvalidJournal, exception.Error);
    }
}
