using AskKey.Broker.Protocol;
using AskKey.Broker.Transport;

namespace AskKey.Broker.Tests;

internal static class ProtocolTestSupport
{
    public static string NewPipeName() => BrokerPipeName.Create();

    public static string NewDirectory()
    {
        var path = global::System.IO.Path.Combine(
            global::System.IO.Path.GetTempPath(), "askkey-broker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static BrokerRequest Health(int version = BrokerProtocolVersion.Current)
        => new() { Version = version, Method = "health" };

    /// <summary>The testhost is not AskKey.exe, so the production server-identity gate is bypassed.</summary>
    public static BrokerSocketClient Client(BrokerSocketServer server)
        => Client(server.PipeName);

    /// <summary>The testhost is not AskKey.exe, so the production server-identity gate is bypassed.</summary>
    public static BrokerSocketClient Client(string pipeName)
        => new(pipeName, BrokerServerIdentity.AlwaysTrusted);
}
