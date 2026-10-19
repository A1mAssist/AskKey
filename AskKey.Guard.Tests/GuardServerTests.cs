using System.Security.Cryptography;
using AskKey.Broker.Runtime;

namespace AskKey.Guard.Tests;

public sealed class GuardServerTests
{
    private static byte[] Sign(RSA key, byte[] data)
        => key.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

    private static byte[] Enroll(GuardTestServer context, RSA key, byte[] proof)
    {
        var challenge = context.Client.Challenge();
        Assert.True(challenge.Ok, challenge.Error);
        Assert.Equal(32, challenge.Nonce!.Length);
        var response = context.Client.Enroll(
            key.ExportSubjectPublicKeyInfo(),
            challenge.Nonce,
            Sign(key, challenge.Nonce),
            proof);
        Assert.True(response.Ok, response.Error);
        return response.Kek!;
    }

    private static GuardResponse Unlock(GuardTestServer context, RSA key)
    {
        var challenge = context.Client.Challenge();
        Assert.True(challenge.Ok, challenge.Error);
        return context.Client.Unlock(challenge.Nonce!, Sign(key, challenge.Nonce!));
    }

    [Fact]
    public void FirstEnrollmentPersistsKeyAndProofAndReturnsKek()
    {
        using var context = new GuardTestServer();
        using var key = RSA.Create(2048);
        var proof = GuardTestSupport.NewProof();
        var publicKey = key.ExportSubjectPublicKeyInfo();

        var before = context.Client.Status();
        Assert.True(before.Ok, before.Error);
        Assert.False(before.Enrolled);

        var challenge = context.Client.Challenge();
        Assert.True(challenge.Ok, challenge.Error);
        Assert.Equal(32, challenge.Nonce!.Length);
        var enroll = context.Client.Enroll(
            publicKey, challenge.Nonce, Sign(key, challenge.Nonce), proof);
        Assert.True(enroll.Ok, enroll.Error);
        Assert.Equal(32, enroll.Kek!.Length);

        Assert.True(context.Client.Status().Enrolled);
        var userDirectory = Path.Combine(context.Directory.Path, "users", context.Sid);
        Assert.Equal(publicKey, File.ReadAllBytes(Path.Combine(userDirectory, "hello.pub")));
        Assert.Equal(proof, File.ReadAllBytes(Path.Combine(userDirectory, "hello.proof")));
    }

    [Fact]
    public void UnlockReturnsTheSameKekAndRejectsReplay()
    {
        using var context = new GuardTestServer();
        using var key = RSA.Create(2048);
        var kek = Enroll(context, key, GuardTestSupport.NewProof());

        var challenge = context.Client.Challenge();
        var signature = Sign(key, challenge.Nonce!);
        var unlock = context.Client.Unlock(challenge.Nonce!, signature);
        Assert.True(unlock.Ok, unlock.Error);
        Assert.Equal(kek, unlock.Kek);

        var replay = context.Client.Unlock(challenge.Nonce!, signature);
        Assert.False(replay.Ok);
        Assert.Equal(GuardErrors.BadNonce, replay.Error);
    }

    [Fact]
    public void UnlockBeforeEnrollAndWrongSignatureFailClosed()
    {
        using var context = new GuardTestServer();
        using var key = RSA.Create(2048);
        using var other = RSA.Create(2048);

        var challenge = context.Client.Challenge();
        var notEnrolled = context.Client.Unlock(challenge.Nonce!, Sign(key, challenge.Nonce!));
        Assert.False(notEnrolled.Ok);
        Assert.Equal(GuardErrors.NotEnrolled, notEnrolled.Error);

        var enrollChallenge = context.Client.Challenge();
        var wrongSignature = context.Client.Enroll(
            key.ExportSubjectPublicKeyInfo(),
            enrollChallenge.Nonce!,
            Sign(other, enrollChallenge.Nonce!),
            GuardTestSupport.NewProof());
        Assert.False(wrongSignature.Ok);
        Assert.Equal(GuardErrors.BadSignature, wrongSignature.Error);
        Assert.False(context.Client.Status().Enrolled);
    }

    [Fact]
    public void ReplacementWithCorrectProofSwitchesKeysAndKeepsKek()
    {
        using var context = new GuardTestServer();
        using var oldKey = RSA.Create(2048);
        using var newKey = RSA.Create(2048);
        var proof = GuardTestSupport.NewProof();
        var kek = Enroll(context, oldKey, proof);

        var challenge = context.Client.Challenge();
        var replacement = context.Client.Enroll(
            newKey.ExportSubjectPublicKeyInfo(),
            challenge.Nonce!,
            Sign(newKey, challenge.Nonce!),
            proof);
        Assert.True(replacement.Ok, replacement.Error);
        Assert.Equal(kek, replacement.Kek);

        var newUnlock = Unlock(context, newKey);
        Assert.True(newUnlock.Ok, newUnlock.Error);
        Assert.Equal(kek, newUnlock.Kek);

        var oldUnlock = Unlock(context, oldKey);
        Assert.False(oldUnlock.Ok);
        Assert.Equal(GuardErrors.BadSignature, oldUnlock.Error);
    }

    [Fact]
    public void ReplacementWithWrongOrMissingProofIsRejectedAndKeepsOldKey()
    {
        using var context = new GuardTestServer();
        using var oldKey = RSA.Create(2048);
        using var newKey = RSA.Create(2048);
        var proof = GuardTestSupport.NewProof();
        Enroll(context, oldKey, proof);
        var newPublicKey = newKey.ExportSubjectPublicKeyInfo();

        var wrongProof = GuardTestSupport.NewProof();
        Assert.NotEqual(proof, wrongProof);
        var challenge = context.Client.Challenge();
        var wrong = context.Client.Enroll(
            newPublicKey,
            challenge.Nonce!,
            Sign(newKey, challenge.Nonce!),
            wrongProof);
        Assert.False(wrong.Ok);
        Assert.Equal(GuardErrors.BadSignature, wrong.Error);
        // A wrong proof must not consume the challenge or replace the key.
        var oldUnlock = context.Client.Unlock(
            challenge.Nonce!, Sign(oldKey, challenge.Nonce!));
        Assert.True(oldUnlock.Ok, oldUnlock.Error);

        var missingChallenge = context.Client.Challenge();
        var missing = context.Client.Send(new GuardRequest
        {
            Operation = GuardOperations.Enroll,
            PublicKey = newPublicKey,
            Nonce = missingChallenge.Nonce,
            Signature = Sign(newKey, missingChallenge.Nonce!),
        });
        Assert.False(missing.Ok);
        Assert.Equal(GuardErrors.Protocol, missing.Error);
        var oldUnlockAgain = context.Client.Unlock(
            missingChallenge.Nonce!, Sign(oldKey, missingChallenge.Nonce!));
        Assert.True(oldUnlockAgain.Ok, oldUnlockAgain.Error);
    }

    [Fact]
    public void UnenrollRequiresTheEnrollmentProof()
    {
        using var context = new GuardTestServer();
        using var key = RSA.Create(2048);
        var proof = GuardTestSupport.NewProof();
        Enroll(context, key, proof);

        var wrongProof = context.Client.Unenroll(GuardTestSupport.NewProof());
        Assert.False(wrongProof.Ok);
        Assert.Equal(GuardErrors.BadSignature, wrongProof.Error);
        Assert.True(context.Client.Status().Enrolled);

        var removed = context.Client.Unenroll(proof);
        Assert.True(removed.Ok, removed.Error);
        Assert.False(context.Client.Status().Enrolled);
    }

    [Fact]
    public void ChallengeExpiresAfterFiveMinutes()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var context = new GuardTestServer(timeProvider: clock);
        using var key = RSA.Create(2048);
        Enroll(context, key, GuardTestSupport.NewProof());

        var challenge = context.Client.Challenge();
        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        var expired = context.Client.Unlock(
            challenge.Nonce!, Sign(key, challenge.Nonce!));
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
        Assert.True(GuardCallerVerifier.IsOfficialMachineInstall(machineRoot));
        Assert.True(GuardCallerVerifier.IsOfficialMachineInstall(
            OfficialInstallTopology.PayloadPath(machineRoot)));

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
