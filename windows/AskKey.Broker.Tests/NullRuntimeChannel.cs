using System.IO.Pipes;
using AskKey.Broker.Transport;

namespace AskKey.Broker.Tests;

/// <summary>
/// /dev/null stand-in for direct runtime tests: connects all four channel pipes,
/// gives the child an immediate stdin EOF, and drains target output.
/// </summary>
internal static class NullRuntimeChannel
{
    public static BrokerRuntimeChannel Create()
    {
        var channel = BrokerRuntimeChannel.CreateCaller();
        var accept = Task.Run(async () =>
        {
            var clients = new NamedPipeClientStream[4];
            for (var index = 0; index < 4; index++)
            {
                clients[index] = new NamedPipeClientStream(
                    ".", BrokerRuntimeChannel.PipeName(channel.Token, index),
                    PipeDirection.InOut, PipeOptions.Asynchronous);
                await clients[index].ConnectAsync(5000);
            }
            await channel.ConnectCallerAsync();
            clients[BrokerRuntimeChannel.StdinIndex].Dispose();
            channel.Stdin.Dispose();
            _ = DrainAsync(clients[BrokerRuntimeChannel.StdoutIndex]);
            _ = DrainAsync(clients[BrokerRuntimeChannel.StderrIndex]);
            return clients;
        });
        channel.AcceptTask = accept;
        return channel;
    }

    private static async Task DrainAsync(Stream stream)
    {
        var buffer = new byte[4096];
        try
        {
            while (await stream.ReadAsync(buffer) > 0) { }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }
}
