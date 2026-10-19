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

    public static BrokerSocketClient Client(BrokerSocketServer server) => new(server.PipeName);
}
