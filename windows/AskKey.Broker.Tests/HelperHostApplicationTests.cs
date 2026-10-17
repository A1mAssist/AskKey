using AskKey.Broker.Approval;
using AskKey.Broker.FileWrite;
using AskKey.Broker.Protocol;
using AskKey.Broker.Runtime;
using AskKey.Broker.Transport;

namespace AskKey.Broker.Tests;

public sealed class HelperHostApplicationTests
{
    [Fact]
    public void ReleaseOpenRejectsMovedHostAndNamesTheOfficialPath()
    {
        using var fixture = new HostFixture("AskKey");
        var opened = new List<string>();
        var error = Assert.Throws<HelperHostApplicationException>(() =>
            HelperHostApplication.OpenHost(fixture.Helper, isDevelopmentBuild: false, opened.Add));
        Assert.Contains(OfficialInstallTopology.CanonicalAppPath, error.Message);
        Assert.Contains("Reinstall", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(opened);
    }

    [Fact]
    public void ReleaseOpenRejectsExternalHelper()
    {
        var loose = Path.Combine(ProtocolTestSupport.NewDirectory(), "askkey.exe");
        try
        {
            File.WriteAllText(loose, "helper");
            var opened = new List<string>();
            Assert.Throws<HelperHostApplicationException>(() =>
                HelperHostApplication.OpenHost(loose, isDevelopmentBuild: false, opened.Add));
            Assert.Empty(opened);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(loose)!, recursive: true);
        }
    }

    [Fact]
    public void ReleaseOpenRejectsMismatchedPublisher()
    {
        using var fixture = new HostFixture("AskKey");
        var opened = new List<string>();
        var error = Assert.Throws<HelperHostApplicationException>(() =>
            HelperHostApplication.OpenHost(
                fixture.Helper,
                isDevelopmentBuild: false,
                opened.Add,
                path => path == fixture.Helper
                    ? new HelperHostSigningFacts { Publisher = "Contoso", IsValid = true }
                    : new HelperHostSigningFacts { Publisher = "Fabrikam", IsValid = true },
                path => path));
        Assert.Contains("Reinstall", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(opened);
    }

    [Fact]
    public void ReleaseOpenRejectsUnsignedBinaries()
    {
        using var fixture = new HostFixture("AskKey");
        var opened = new List<string>();
        Assert.Throws<HelperHostApplicationException>(() =>
            HelperHostApplication.OpenHost(
                fixture.Helper, isDevelopmentBuild: false, opened.Add,
                _ => HelperHostSigningFacts.Unsigned, path => path));
        Assert.Empty(opened);
    }

    [Fact]
    public void ReleaseOpenAllowsCanonicalPathOnlyWithMatchingPublisher()
    {
        var app = OfficialInstallTopology.CanonicalAppPath;
        var helper = OfficialInstallTopology.CanonicalHelperPath;
        var opened = new List<string>();
        HelperHostApplication.OpenHost(
            helper,
            isDevelopmentBuild: false,
            opened.Add,
            _ => new HelperHostSigningFacts { Publisher = "Ask Key", IsValid = true },
            path => path);
        Assert.Equal(new[] { Path.Combine(app, OfficialInstallTopology.HostFileName) }, opened.ToArray());
    }

    [Fact]
    public void DebugOpenAllowsIsolatedHost()
    {
        using var fixture = new HostFixture("AskKeyDev");
        var opened = new List<string>();
        HelperHostApplication.OpenHost(
            fixture.Helper,
            isDevelopmentBuild: true,
            opened.Add,
            _ => HelperHostSigningFacts.Unsigned);
        Assert.Equal(new[] { fixture.Host }, opened.ToArray());
    }

    [Fact]
    public void UnsignedExecutableHasNoSigningFacts()
    {
        var path = Path.Combine(ProtocolTestSupport.NewDirectory(), "unsigned.exe");
        try
        {
            File.WriteAllText(path, "plain text");
            var facts = HelperHostApplication.SigningFacts(path);
            Assert.False(facts.IsValid);
            Assert.Null(facts.Publisher);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void TopologyRejectsHelperOutsideTheHostDirectory()
    {
        var decision = OfficialInstallTopology.Decide(
            OfficialInstallTopology.CanonicalAppPath,
            helperPath: Path.Combine(Path.GetTempPath(), "askkey.exe"),
            isDevelopmentBuild: true);
        Assert.Equal(OfficialInstallDecisionKind.HelperMismatch, decision.Kind);
        Assert.False(OfficialInstallTopology.AllowsOfficialRuntime(decision));
        Assert.Throws<OfficialInstallTopologyException>(() => OfficialInstallTopology.ResolvedHelperPath(
            OfficialInstallTopology.CanonicalAppPath,
            Path.Combine(Path.GetTempPath(), "askkey.exe"),
            isDevelopmentBuild: true));
    }

    private sealed class HostFixture : IDisposable
    {
        public string Root { get; }
        public string Host { get; }
        public string Helper { get; }

        public HostFixture(string _)
        {
            Root = ProtocolTestSupport.NewDirectory();
            Host = Path.Combine(Root, OfficialInstallTopology.HostFileName);
            Helper = Path.Combine(Root, OfficialInstallTopology.HelperDirectoryName,
                OfficialInstallTopology.HelperFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(Helper)!);
            File.WriteAllText(Host, "host");
            File.WriteAllText(Helper, "helper");
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }
}

public sealed class BrokerHandlerCompositionTests
{
    [Fact]
    public void RealRuntimeAndFileWriteAreWiredIntoTheHandler()
    {
        var directory = ProtocolTestSupport.NewDirectory();
        using var approvals = new BrokerApprovalStateMachine(authenticate: _ => true);
        using var coordinator = new BrokerFileWriteCoordinator(
            directory,
            approvals,
            authenticateReveal: () => true,
            commitFrozenFile: _ => { },
            submitFrozenApproval: (_, _, request) => approvals.Submit(request, BrokerCredentialDeadline.None),
            normalizeCreateTarget: target => target);
        var runtime = new BrokerTextRuntime(
            (_, _) => BrokerTextCredentialResolution.Resolved(
                new[] { new BrokerTextCredential("TOKEN", "synthetic") }));
        var handler = BrokerHandlerComposition.Create(approvals, runtime, coordinator);
        var pipe = ProtocolTestSupport.NewPipeName();
        try
        {
            using var server = new BrokerSocketServer(pipe, handler);
            server.Start();
            var client = new BrokerSocketClient(pipe);

            var upload = Assert.IsType<BrokerFileUploadTicket>(client.Send(new BrokerRequest
            {
                Version = BrokerProtocolVersion.Current,
                Method = "credential.file.write",
                FileWrite = BrokerFileWriteRequest.Begin(new BrokerFileWriteBeginRequest(
                    "wired-write", "credential-id", "credential-id",
                    BrokerApprovalOperation.Modify, "AuthKey.p8", 3)),
            }).Payload?.FileWrite?.UploadTicket);
            Assert.Equal(
                BrokerResponse.Success(BrokerPayload.FromFileWrite(
                    BrokerFileWritePayload.ChunkAccepted(3))),
                client.Send(new BrokerRequest
                {
                    Version = BrokerProtocolVersion.Current,
                    Method = "credential.file.write",
                    FileWrite = BrokerFileWriteRequest.Append(new BrokerFileWriteChunkRequest(
                        upload.UploadId, upload.Capability, 0, "abc"u8.ToArray())),
                }));
            var approval = client.Send(new BrokerRequest
            {
                Version = BrokerProtocolVersion.Current,
                Method = "credential.file.write",
                FileWrite = BrokerFileWriteRequest.Freeze(new BrokerFileWriteFreezeRequest(
                    upload.UploadId, upload.Capability)),
            }).Payload?.FileWrite?.ApprovalTicket;
            Assert.NotNull(approval);
            Assert.Equal(
                BrokerResponse.Success(BrokerPayload.FromRequestStatus(BrokerRequestState.Pending)),
                client.Send(new BrokerRequest
                {
                    Version = BrokerProtocolVersion.Current,
                    Method = "request.status",
                    RequestId = approval.Value.RequestId,
                    Capability = approval.Value.Capability,
                }));

            using var channel = BrokerRuntimeChannel.CreateCaller();
            var drain = Task.Run(async () =>
            {
                await channel.CloseStdinAfterConnectAsync();
                var buffer = new byte[1024];
                while (await channel.Stdout.ReadAsync(buffer) > 0) { }
            });
            Assert.Equal(
                BrokerTextRunResult.Exited(0),
                client.Run(new BrokerTextRunRequest(
                    "wired-runtime", new[] { "cmd.exe", "/c", "exit 0" }, new[] { "TOKEN" }), channel));
            drain.Wait(TimeSpan.FromSeconds(5));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
