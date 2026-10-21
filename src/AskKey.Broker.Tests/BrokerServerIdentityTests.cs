using AskKey.Broker.Protocol;
using AskKey.Broker.Runtime;
using AskKey.Broker.Transport;

namespace AskKey.Broker.Tests;

public sealed class BrokerServerIdentityTests
{
    private static readonly HelperHostSigningFacts Unsigned = HelperHostSigningFacts.Unsigned;

    private static BrokerServerIdentity ClientFor(
        string installRoot,
        bool allowUnsigned = true,
        string? helperPublisher = null,
        string? hostPublisher = null)
        => BrokerServerIdentity.ForHelper(
            Path.Combine(OfficialInstallTopology.PayloadPath(installRoot),
                OfficialInstallTopology.HelperRelativePath),
            path => IsHelper(path)
                ? Publisher(helperPublisher)
                : Publisher(hostPublisher),
            allowUnsigned);

    /// <summary>AskKey.exe and askkey.exe differ only by case on NTFS, so the helper is
    /// identified by its directory, never by its file name.</summary>
    private static bool IsHelper(string path)
        => string.Equals(Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty),
            OfficialInstallTopology.HelperDirectoryName, StringComparison.OrdinalIgnoreCase);

    private static HelperHostSigningFacts Publisher(string? publisher)
        => publisher is null
            ? Unsigned
            : new HelperHostSigningFacts { Publisher = publisher, IsValid = true };

    private static string HostIn(string installRoot)
        => Path.Combine(OfficialInstallTopology.PayloadPath(installRoot),
            OfficialInstallTopology.HostFileName);

    /// <summary>
    /// The pipe-time gate is compiled out of development builds, so the policy itself
    /// is exercised directly: these must hold identically in Debug and Release.
    ///
    /// Each case names the directory holding the candidate host. Only the directory this
    /// helper's own install produces is accepted; the other official roots used to pass
    /// just as well, because the whitelist judged every root independently.
    /// </summary>
    public static TheoryData<string, bool> HostDirectories => new()
    {
        // This helper's own install, with the host where Velopack puts it.
        { OfficialInstallTopology.PayloadPath(OfficialInstallTopology.CanonicalAppPath), true },
        // Official roots this helper did not come from.
        { OfficialInstallTopology.PayloadPath(OfficialInstallTopology.CanonicalMachineAppPath), false },
        { OfficialInstallTopology.PayloadPath(OfficialInstallTopology.LegacyMachineAppPath), false },
        // Its own root, but the host one level too high instead of inside the payload.
        { OfficialInstallTopology.CanonicalAppPath, false },
        // Neither an official root nor this helper's.
        { @"D:\relocated\AskKey\current", false },
        { @"C:\evil", false },
    };

    [Theory]
    [MemberData(nameof(HostDirectories))]
    public void PolicyAcceptsOnlyTheHostOfThisInstall(string hostDirectory, bool expected)
    {
        var client = ClientFor(OfficialInstallTopology.CanonicalAppPath);

        Assert.Equal(expected, client.IsOfficialHostImage(
            Path.Combine(hostDirectory, OfficialInstallTopology.HostFileName)));
    }

    /// <summary>Roots that are official in their own right but not this client's install.</summary>
    public static TheoryData<string> OtherOfficialRoots => new()
    {
        OfficialInstallTopology.CanonicalAppPath,
        OfficialInstallTopology.LegacyMachineAppPath,
    };

    /// The core regression: on a per-machine install, an AskKey.exe planted in the per-user
    /// root belongs to a different install, not to our host. The whitelist judged each root
    /// independently, so any same-user process could squat the pipe this way.
    /// </summary>
    [Theory]
    [MemberData(nameof(OtherOfficialRoots))]
    public void APerMachineClientRejectsAHostFromAnotherOfficialRoot(string impostorRoot)
    {
        var client = ClientFor(OfficialInstallTopology.CanonicalMachineAppPath);

        Assert.True(client.IsOfficialHostImage(
            HostIn(OfficialInstallTopology.CanonicalMachineAppPath)));
        Assert.False(client.IsOfficialHostImage(HostIn(impostorRoot)));
    }

    /// <summary>The mirror case: a per-user helper must not accept a per-machine host.</summary>
    [Fact]
    public void APerUserClientRejectsAPerMachineHost()
    {
        var client = ClientFor(OfficialInstallTopology.CanonicalAppPath);

        Assert.True(client.IsOfficialHostImage(HostIn(OfficialInstallTopology.CanonicalAppPath)));
        Assert.False(client.IsOfficialHostImage(
            HostIn(OfficialInstallTopology.CanonicalMachineAppPath)));
    }

    /// <summary>
    /// A signed release compares the publisher rather than accepting any valid signature:
    /// "signed by somebody" must not pass as "signed by us". An unsigned release has no
    /// publisher to compare and relies on the same-root path check instead.
    /// </summary>
    [Theory]
    [InlineData("Ask Key", "Ask Key", true)]
    [InlineData("Ask Key", "ask key", true)]
    [InlineData("Ask Key", "Contoso Ltd", false)]
    [InlineData(null, null, false)]
    public void ASignedReleaseComparesThePublisherNotJustValidity(
        string? helperPublisher, string? hostPublisher, bool expected)
    {
        var client = ClientFor(
            OfficialInstallTopology.CanonicalAppPath,
            allowUnsigned: false,
            helperPublisher: helperPublisher,
            hostPublisher: hostPublisher);

        Assert.Equal(expected,
            client.IsOfficialHostImage(HostIn(OfficialInstallTopology.CanonicalAppPath)));
    }

    public static TheoryData<string, bool> HelperPaths => new()
    {
        { Path.Combine(OfficialInstallTopology.CanonicalMachineAppPath, "current",
            OfficialInstallTopology.HelperRelativePath), true },
        { Path.Combine(OfficialInstallTopology.CanonicalAppPath, "current",
            OfficialInstallTopology.HelperRelativePath), true },
        // A helper anywhere else submits no real credentials, so the gate must not block it.
        { Path.Combine(Path.GetTempPath(), "askkey-helper-tests", "askkey.exe"), false },
        { Path.Combine(OfficialInstallTopology.CanonicalMachineAppPath, "current", "AskKey.exe"), false },
    };

    [Theory]
    [MemberData(nameof(HelperPaths))]
    public void OnlyTheInstalledHelperIsSubjectToTheGate(string helperPath, bool official)
    {
        Assert.Equal(official, BrokerServerIdentity.IsOfficialHelperPath(helperPath));
    }

    [Fact]
    public void PolicyRejectsAMissingImagePath()
    {
        var client = ClientFor(OfficialInstallTopology.CanonicalAppPath);

        Assert.False(client.IsOfficialHostImage(null));
        Assert.False(client.IsOfficialHostImage(""));
    }

    [Fact]
    public void AnIdentityWithoutAKnownHelperCannotNameAHost()
    {
        // AlwaysTrusted short-circuits before this, but an identity that never learned
        // where it lives has nothing to compare against, so it must not fall back to the
        // old any-official-root whitelist.
        var anonymous = new BrokerServerIdentity(null, _ => Unsigned, allowUnsigned: true);

        Assert.False(anonymous.IsOfficialHostImage(HostIn(OfficialInstallTopology.CanonicalAppPath)));
    }

    /// <summary>
    /// Only a helper inside an official install is subject to the server-identity gate;
    /// anything else runs with no vault behind it, so it is not blocked.
    /// </summary>
    [Fact]
    public void ANonInstalledClientIsNotBlockedByTheGate()
    {
        var pipe = ProtocolTestSupport.NewPipeName();
        using var server = new BrokerSocketServer(pipe, new BrokerRequestHandler(
            catalog: _ => Array.Empty<BrokerCatalogItem>(), requestStatus: (_, _) => null));
        server.Start();

        Assert.Equal(BrokerResponse.Kind.Success,
            new BrokerSocketClient(pipe, BrokerServerIdentity.Production)
                .Send(ProtocolTestSupport.Health()).ResponseKind);
    }

    [Fact]
    public void ATrustedPeerStillRoundTrips()
    {
        var pipe = ProtocolTestSupport.NewPipeName();
        using var server = new BrokerSocketServer(pipe, new BrokerRequestHandler(
            catalog: _ => Array.Empty<BrokerCatalogItem>(), requestStatus: (_, _) => null));
        server.Start();

        var response = ProtocolTestSupport.Client(pipe).Send(ProtocolTestSupport.Health());
        Assert.Equal(BrokerResponse.Kind.Success, response.ResponseKind);
    }
}