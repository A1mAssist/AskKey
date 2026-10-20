using AskKey.Broker.Protocol;
using AskKey.Broker.Runtime;
using AskKey.Broker.Transport;

namespace AskKey.Broker.Tests;

public sealed class BrokerServerIdentityTests
{
    /// <summary>
    /// The pipe-time gate is compiled out of development builds, so the policy itself
    /// is exercised directly: these must hold identically in Debug and Release.
    /// </summary>
    public static TheoryData<string, bool> HostImages => new()
    {
        { Path.Combine(OfficialInstallTopology.CanonicalMachineAppPath, "current", "AskKey.exe"), true },
        { Path.Combine(OfficialInstallTopology.LegacyMachineAppPath, "current", "AskKey.exe"), true },
        { Path.Combine(OfficialInstallTopology.CanonicalAppPath, "current", "AskKey.exe"), true },
        { @"D:\relocated\AskKey\current\AskKey.exe", false },
        { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe"), false },
        { @"C:\evil\AskKey.exe", false },
    };

    [Theory]
    [MemberData(nameof(HostImages))]
    public void PolicyAcceptsOnlyAnInstalledHost(string imagePath, bool expected)
    {
        Assert.Equal(expected, BrokerServerIdentity.IsOfficialHostImage(imagePath));
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
        Assert.False(BrokerServerIdentity.IsOfficialHostImage(null));
        Assert.False(BrokerServerIdentity.IsOfficialHostImage(""));
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
