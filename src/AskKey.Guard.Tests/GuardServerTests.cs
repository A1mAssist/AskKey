using AskKey.Broker.Runtime;

namespace AskKey.Guard.Tests;

public sealed class GuardServerTests
{
    private static byte[] Enroll(GuardTestServer context, byte[] proof)
    {
        var challenge = context.Client.Challenge();
        Assert.True(challenge.Ok, challenge.Error);
        Assert.Equal(32, challenge.Nonce!.Length);
        var response = context.Client.ConsumeEnroll(challenge.Nonce, proof);
        Assert.True(response.Ok, response.Error);
        return response.Kek!;
    }

    [Fact]
    public void FirstEnrollmentStoresTheProofAndReturnsKek()
    {
        using var context = new GuardTestServer();
        var proof = GuardTestSupport.NewProof();

        var before = context.Client.Status();
        Assert.True(before.Ok, before.Error);
        Assert.False(before.Enrolled);

        var challenge = context.Client.Challenge();
        Assert.True(challenge.Ok, challenge.Error);
        Assert.Equal(32, challenge.Nonce!.Length);
        var enroll = context.Client.ConsumeEnroll(challenge.Nonce, proof);
        Assert.True(enroll.Ok, enroll.Error);
        Assert.Equal(32, enroll.Kek!.Length);

        Assert.True(context.Client.Status().Enrolled);
        var userDirectory = Path.Combine(context.Directory.Path, "users", context.Sid);
        Assert.Equal(proof, File.ReadAllBytes(Path.Combine(userDirectory, "hello.proof")));
    }

    [Fact]
    public void ConsumeReturnsTheSameKekAndRejectsReplay()
    {
        using var context = new GuardTestServer();
        var kek = Enroll(context, GuardTestSupport.NewProof());

        var challenge = context.Client.Challenge();
        var unlocked = context.Client.Consume(challenge.Nonce!);
        Assert.True(unlocked.Ok, unlocked.Error);
        Assert.Equal(kek, unlocked.Kek);

        var replay = context.Client.Consume(challenge.Nonce!);
        Assert.False(replay.Ok);
        Assert.Equal(GuardErrors.BadNonce, replay.Error);
    }

    [Fact]
    public void ConsumeBeforeEnrollFailsClosed()
    {
        using var context = new GuardTestServer();

        var challenge = context.Client.Challenge();
        var notEnrolled = context.Client.Consume(challenge.Nonce!);
        Assert.False(notEnrolled.Ok);
        Assert.Equal(GuardErrors.NotEnrolled, notEnrolled.Error);
    }

    [Fact]
    public void ARepeatedEnrollmentNeedsTheSameProof()
    {
        using var context = new GuardTestServer();
        var proof = GuardTestSupport.NewProof();
        var kek = Enroll(context, proof);

        var challenge = context.Client.Challenge();
        var same = context.Client.ConsumeEnroll(challenge.Nonce!, proof);
        Assert.True(same.Ok, same.Error);
        Assert.Equal(kek, same.Kek);

        var wrongProof = GuardTestSupport.NewProof();
        Assert.NotEqual(proof, wrongProof);
        var wrongChallenge = context.Client.Challenge();
        var wrong = context.Client.ConsumeEnroll(wrongChallenge.Nonce!, wrongProof);
        Assert.False(wrong.Ok);
        Assert.Equal(GuardErrors.BadProof, wrong.Error);
        // A wrong proof must not consume the challenge or replace the enrollment.
        var still = context.Client.Consume(wrongChallenge.Nonce!);
        Assert.True(still.Ok, still.Error);
        Assert.Equal(kek, still.Kek);
    }

    [Fact]
    public void AProofOfTheWrongLengthIsRejected()
    {
        using var context = new GuardTestServer();

        var challenge = context.Client.Challenge();
        var shortProof = context.Client.ConsumeEnroll(challenge.Nonce!, new byte[16]);
        Assert.False(shortProof.Ok);
        Assert.Equal(GuardErrors.BadProof, shortProof.Error);
        Assert.False(context.Client.Status().Enrolled);
    }

    [Fact]
    public void UnenrollRequiresTheEnrollmentProof()
    {
        using var context = new GuardTestServer();
        var proof = GuardTestSupport.NewProof();
        Enroll(context, proof);

        var wrongProof = context.Client.Unenroll(GuardTestSupport.NewProof());
        Assert.False(wrongProof.Ok);
        Assert.Equal(GuardErrors.BadProof, wrongProof.Error);
        Assert.True(context.Client.Status().Enrolled);

        var removed = context.Client.Unenroll(proof);
        Assert.True(removed.Ok, removed.Error);
        Assert.False(context.Client.Status().Enrolled);
    }

    [Fact]
    public void ResetDropsTheEnrollmentWithoutTheOldProof()
    {
        using var context = new GuardTestServer();
        var kek = Enroll(context, GuardTestSupport.NewProof());
        Assert.True(context.Client.Status().Enrolled);

        var challenge = context.Client.Challenge();
        var reset = context.Client.Reset(challenge.Nonce!);
        Assert.True(reset.Ok, reset.Error);
        Assert.False(context.Client.Status().Enrolled);

        // Nothing is enrolled until the new vault proves itself; the KEK itself is stable.
        var consume = context.Client.Consume(context.Client.Challenge().Nonce!);
        Assert.False(consume.Ok);
        Assert.Equal(GuardErrors.NotEnrolled, consume.Error);
        Assert.Equal(kek, Enroll(context, GuardTestSupport.NewProof()));
    }

    [Fact]
    public void ResetNeedsAFreshNonce()
    {
        using var context = new GuardTestServer();
        Enroll(context, GuardTestSupport.NewProof());

        var challenge = context.Client.Challenge();
        Assert.True(context.Client.Reset(challenge.Nonce!).Ok);
        var replay = context.Client.Reset(challenge.Nonce!);
        Assert.False(replay.Ok);
        Assert.Equal(GuardErrors.BadNonce, replay.Error);

        var missing = context.Client.Send(new GuardRequest { Operation = GuardOperations.Reset });
        Assert.False(missing.Ok);
        Assert.Equal(GuardErrors.Protocol, missing.Error);
    }

    [Fact]
    public void ChallengeExpiresAfterFiveMinutes()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var context = new GuardTestServer(timeProvider: clock);
        Enroll(context, GuardTestSupport.NewProof());

        var challenge = context.Client.Challenge();
        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        var expired = context.Client.Consume(challenge.Nonce!);
        Assert.False(expired.Ok);
        Assert.Equal(GuardErrors.BadNonce, expired.Error);
    }

    [Fact]
    public void RejectedCallerCannotQueryStatus()
    {
        using var context = new GuardTestServer(new FixedCallerVerifier(null));
        var response = context.Client.Status();
        Assert.False(response.Ok);
        Assert.Equal(GuardErrors.CallerRejected, response.Error);
    }

    [Fact]
    public void ServerResolvesCallerWhileImpersonatingTheClient()
    {
        var verifier = new ImpersonationRecordingVerifier();
        using var context = new GuardTestServer(verifier);

        Assert.True(context.Client.Status().Ok);

        // A service account cannot read a process owned by another account, so the
        // resolver only succeeds while the server borrows the client token.
        Assert.True(verifier.SawImpersonatedThread);
    }

    [Fact]
    public void DefaultVerifierRejectsNonAskKeyProcess()
    {
        var verifier = new GuardCallerVerifier(development: false);
        Assert.Null(verifier.Verify((uint)Environment.ProcessId));
    }

    [Fact]
    public void ProductionTopologyAcceptsOnlyProgramFiles()
    {
        var machineRoot = OfficialInstallTopology.CanonicalMachineAppPath;
        Assert.Equal("AskKey", Path.GetFileName(machineRoot));
        Assert.Equal("Ask Key", Path.GetFileName(OfficialInstallTopology.LegacyMachineAppPath));
        foreach (var root in OfficialInstallTopology.MachineAppPaths)
        {
            Assert.True(GuardCallerVerifier.IsOfficialMachineInstall(root));
            Assert.True(GuardCallerVerifier.IsOfficialMachineInstall(
                OfficialInstallTopology.PayloadPath(root)));
        }

        var perUser = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AskKey",
            "current");
        Assert.False(GuardCallerVerifier.IsOfficialMachineInstall(perUser));
    }

    [Fact]
    public void ScmQueryFindsRunningServiceProcess()
    {
        Assert.True(GuardProcess.TryGetServiceProcessId("RpcSs", out var processId));
        Assert.NotEqual(0u, processId);
    }

    [Fact]
    public void ServicePipeCanBeCreated()
    {
        using var pipe = GuardPipe.Create(
            GuardTestSupport.NewPipeName(), 1, GuardAcl.CreateServicePipeSecurity());
        Assert.False(pipe.IsConnected);
    }

    [Theory]
    [InlineData(@"C:\Program Files\AskKey", @"C:\Program Files\AskKey\Guard\AskKey.Guard.Service.exe")]
    [InlineData(@"C:\Program Files\AskKey\current", @"C:\Program Files\AskKey\Guard\AskKey.Guard.Service.exe")]
    public void ServicePathStaysOutsideTheVelopackPayload(string application, string expected)
        => Assert.Equal(expected, GuardClient.ExpectedServicePath(application));

    [Fact]
    public void ClientRejectsAServerThatIsNotTheMachineGuardService()
    {
        using var context = new GuardTestServer();
        var client = new GuardClient(
            context.Client.PipeName,
            development: false,
            expectedServerPath: Environment.ProcessPath!);
        var error = Assert.Throws<GuardClientException>(() => client.Status());
        Assert.Contains("identity", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PipeNamesSeparateDevelopmentFromProduction()
    {
        Assert.Equal("askkey-guard", GuardProtocol.SelectPipeName(development: false));
        Assert.Equal("askkey-guard-dev", GuardProtocol.SelectPipeName(development: true));
    }

    [Fact]
    public void ClientUsesDevPipeOnlyWhenExplicitlyRequested()
    {
        Assert.Equal(
            GuardProtocol.DevelopmentPipeName,
            new GuardClient(GuardProtocol.DevelopmentPipeName, development: true).PipeName);

        var original = Environment.GetEnvironmentVariable("ASKKEY_DEVELOPMENT");
        try
        {
            Environment.SetEnvironmentVariable("ASKKEY_DEVELOPMENT", null);
            Assert.Equal(GuardProtocol.PipeName, new GuardClient().PipeName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASKKEY_DEVELOPMENT", original);
        }
    }
}
