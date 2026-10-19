using System.Security.Cryptography.X509Certificates;
using AskKey.Broker.Protocol;
using AskKey.Broker.Runtime;
using AskKey.Broker.Transport;

namespace AskKey.Broker.Tests;

public sealed class RuntimeExecutableBindingTests
{
    [Fact]
    public void ABareCommandNameBindsToTheAbsolutePathBeforeApproval()
    {
        var approved = new List<string>();
        var runtime = new BrokerTextRuntime(
            (request, _) =>
            {
                // The approval the user reviews is built from this command list.
                approved.Add(request.Command[0]);
                return BrokerTextCredentialResolution.Resolved(
                    new[] { new BrokerTextCredential("TOKEN", "synthetic") });
            },
            resolveExecutable: (name, _) => name == "cmd.exe"
                ? Path.Combine(Environment.SystemDirectory, "cmd.exe")
                : null);

        var request = new BrokerTextRunRequest("bind-bare", new[] { "cmd.exe", "/c", "exit 0" }, new[] { "TOKEN" });
        using var channel = NullRuntimeChannel.Create();
        Assert.Equal(BrokerTextRunResult.Exited(0), runtime.Run(request, channel));

        Assert.Equal(Path.Combine(Environment.SystemDirectory, "cmd.exe"), approved.Single());
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "cmd.exe"), request.Command[0]);
        Assert.True(Path.IsPathFullyQualified(request.Command[0]));
        Assert.Equal(new[] { "TOKEN" }, request.CredentialNames.ToArray());
    }

    [Fact]
    public void AnUnresolvableExecutableFailsBeforeAnyApprovalIsPrepared()
    {
        var resolverCalls = 0;
        var runtime = new BrokerTextRuntime(
            (_, _) =>
            {
                resolverCalls++;
                return BrokerTextCredentialResolution.Resolved(
                    new[] { new BrokerTextCredential("TOKEN", "synthetic") });
            },
            resolveExecutable: (_, _) => null);

        var request = new BrokerTextRunRequest(
            "bind-missing", new[] { "askkey-not-installed.exe" }, new[] { "TOKEN" });
        using var channel = NullRuntimeChannel.Create();
        Assert.Equal(
            BrokerTextRuntimeError.UnresolvedExecutable,
            Assert.Throws<BrokerTextRuntimeException>(() => runtime.Run(request, channel)).Error);
        Assert.Equal(0, resolverCalls);
    }

    [Fact]
    public void PathLookupFindsScriptExtensionsAndRejectsAbsentFiles()
    {
        var directory = ProtocolTestSupport.NewDirectory();
        try
        {
            var script = Path.Combine(directory, "askkey-probe.cmd");
            File.WriteAllText(script, "@echo off");
            var environment = new Dictionary<string, string> { ["PATH"] = directory };

            Assert.True(PathsEqual(script, BrokerTextRuntime.ResolveInherited("askkey-probe", environment)));
            Assert.True(PathsEqual(script, BrokerTextRuntime.ResolveInherited("askkey-probe.cmd", environment)));
            Assert.Null(BrokerTextRuntime.ResolveInherited("askkey-absent", environment));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

        // Windows file lookup is case-insensitive, and PATHEXT entries are upper-case.
    private static bool PathsEqual(string left, string? right)
        => right is not null && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void AnExpiredSpawnDeadlineRefusesToLaunch()
    {
        var spawned = false;
        var lease = new BrokerTextDeliveryLease(
            beginSpawnAuthorization: () => { },
            validate: () => { },
            endSpawnAuthorization: () => { },
            spawnDeadline: DateTimeOffset.UtcNow.AddMinutes(-1),
            runtimeAuthorization: null,
            finish: () => { },
            cleanup: () => { });

        Assert.Equal(
            BrokerTextRuntimeError.SpawnDeadlineExpired,
            Assert.Throws<BrokerTextRuntimeException>(
                () => lease.PerformAuthorizedSpawn<bool>(() => { }, () => { spawned = true; return true; })).Error);
        Assert.False(spawned);
    }

    [Fact]
    public void AFutureSpawnDeadlineStillLaunches()
    {
        var spawned = false;
        var lease = new BrokerTextDeliveryLease(
            beginSpawnAuthorization: () => { },
            validate: () => { },
            endSpawnAuthorization: () => { },
            spawnDeadline: DateTimeOffset.UtcNow.AddMinutes(5),
            runtimeAuthorization: null,
            finish: () => { },
            cleanup: () => { });

        lease.PerformAuthorizedSpawn(() => { }, () => { spawned = true; return 0; });
        Assert.True(spawned);
    }

    [Fact]
    public void TheDeclaredInheritedPathWinsOverTheProcessPath()
    {
        var directory = ProtocolTestSupport.NewDirectory();
        try
        {
            var tool = Path.Combine(directory, "askkey-declared.exe");
            File.WriteAllText(tool, "tool");
            var environment = new Dictionary<string, string> { ["PATH"] = directory };
            Assert.True(PathsEqual(tool, BrokerTextRuntime.ResolveInherited("askkey-declared", environment)));

            // A bare name that is not on the declared PATH does not fall back.
            Assert.Null(BrokerTextRuntime.ResolveInherited(
                "cmd.exe", new Dictionary<string, string> { ["PATH"] = directory }));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

public sealed class HelperHostSigningVerificationTests
{
    [Fact]
    public void ASignedBinaryPassesWinVerifyTrust()
    {
        // A catalog-signed Windows binary has no extractable certificate, so it stands
        // in for the release build a developer machine does not have.
        var path = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
        var facts = HelperHostApplication.SigningFacts(path);

        Assert.True(facts.IsValid);
        Assert.False(string.IsNullOrWhiteSpace(facts.Publisher));
        Assert.False(string.IsNullOrWhiteSpace(facts.Thumbprint));
        Assert.Equal(HelperHostSignatureKind.Signed, HelperHostSignatureTrust.Classify(facts));
    }

        [Fact]
    public void AnUnsignedFileHasNoValidSigningFacts()
    {
        var path = Path.Combine(ProtocolTestSupport.NewDirectory(), "unsigned.exe");
        try
        {
            File.WriteAllText(path, "not a PE image at all");
            var facts = HelperHostApplication.SigningFacts(path);

            Assert.False(facts.IsValid);
            Assert.Null(facts.Publisher);
            Assert.Equal(HelperHostSignatureKind.Unsigned, HelperHostSignatureTrust.Classify(facts));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void ATamperedSignedBinaryIsRejectedEvenThoughItsCertificateStillExtracts()
    {
        // This is the P2-5 defect in one assertion: flipping a byte inside the PE header
        // leaves the Authenticode certificate readable, so extracting a certificate would
        // report a trusted publisher for a modified binary. Only a WinVerifyTrust failure
        // catches it.
        var source = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
        var directory = ProtocolTestSupport.NewDirectory();
        var copy = Path.Combine(directory, "tampered.dll");
        try
        {
            var bytes = File.ReadAllBytes(source);
            bytes[0x10] ^= 0xFF;
            File.WriteAllBytes(copy, bytes);

            // The old extraction-only behaviour still finds the publisher here.
#pragma warning disable SYSLIB0057
            var extracted = new X509Certificate2(X509Certificate.CreateFromSignedFile(copy));
#pragma warning restore SYSLIB0057
            Assert.False(string.IsNullOrWhiteSpace(
                extracted.GetNameInfo(X509NameType.SimpleName, forIssuer: false)));

            Assert.False(HelperHostApplication.SigningFacts(copy).IsValid);
            Assert.Null(HelperHostApplication.SigningFacts(copy).Publisher);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AMissingFileHasNoSigningFacts()
    {
        var facts = HelperHostApplication.SigningFacts(
            Path.Combine(Environment.SystemDirectory, "askkey-does-not-exist.exe"));
        Assert.False(facts.IsValid);
        Assert.Null(facts.Publisher);
    }

    [Fact]
    public void ThePublisherGateStillComparesTheSignerIdentity()
    {
        // Same publisher passes, a different one does not: the verification change must
        // not weaken the identity comparison that the gate exists for.
        var trusted = new HelperHostSigningFacts { Publisher = "Ask Key", IsValid = true };
        var other = new HelperHostSigningFacts { Publisher = "Contoso", IsValid = true };

        Assert.True(HelperHostSignatureTrust.Allows(trusted, trusted, isDevelopmentBuild: false, allowUnsigned: false));
        Assert.False(HelperHostSignatureTrust.Allows(trusted, other, isDevelopmentBuild: false, allowUnsigned: false));
    }
}
