using System.Diagnostics;
using System.Text;
using AskKey.Broker.Protocol;
using AskKey.Broker.Runtime;
using AskKey.Broker.Transport;

namespace AskKey.Broker.Tests;

public sealed class BrokerProtocolRuntimeTests
{
    [Fact]
    public void RuntimeStdioFlowsThroughThePipeChannelWithoutReturningOutput()
    {
        var pipe = ProtocolTestSupport.NewPipeName();
        var handler = new BrokerRequestHandler(
            catalog: _ => Array.Empty<BrokerCatalogItem>(),
            requestStatus: (_, _) => null,
            textRun: (request, channel, cancellation) =>
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    },
                };
                process.StartInfo.ArgumentList.Add("/c");
                process.StartInfo.ArgumentList.Add("more");
                process.Start();
                // stdin -> child, child stdout -> caller stdout pipe.
                // The caller keeps its stdin pipe open; stop at the first line so
                // cmd's `more` can finish and the child can exit.
                var pump = Task.Run(async () =>
                {
                    var buffer = new byte[4096];
                    var count = await channel.Stdin.ReadAsync(buffer);
                    if (count > 0)
                    {
                        await process.StandardInput.BaseStream.WriteAsync(buffer.AsMemory(0, count));
                        await process.StandardInput.BaseStream.FlushAsync();
                    }
                    process.StandardInput.Close();
                });
                var output = Task.Run(async () =>
                {
                    var buffer = new byte[4096];
                    int count;
                    while ((count = await process.StandardOutput.BaseStream.ReadAsync(buffer)) > 0)
                    {
                        await channel.Stdout.WriteAsync(buffer.AsMemory(0, count));
                        await channel.Stdout.FlushAsync();
                    }
                });
                pump.GetAwaiter().GetResult();
                process.WaitForExit();
                output.GetAwaiter().GetResult();
                return BrokerTextRunResult.Exited(process.ExitCode);
            });
        using var server = new BrokerSocketServer(pipe, handler);
        server.Start();

        using var channel = BrokerRuntimeChannel.CreateCaller();
        var runTask = Task.Run(() => new BrokerSocketClient(pipe).Run(
            new BrokerTextRunRequest("fd-runtime", new[] { "cmd" }, new[] { "TOKEN" }), channel));
        Assert.True(channel.Connected.Wait(TimeSpan.FromSeconds(5)), "broker did not connect the channel");
        var readTask = Task.Run(() =>
        {
            using var reader = new StreamReader(channel.Stdout, Encoding.UTF8);
            return reader.ReadToEnd();
        });
        var writeTask = Task.Run(async () =>
        {
            await channel.Stdin.WriteAsync("fd-secret\r\n"u8.ToArray());
            await channel.Stdin.FlushAsync();
            channel.Stdin.Dispose();
        });

        var result = runTask.GetAwaiter().GetResult();
        writeTask.Wait(TimeSpan.FromSeconds(5));
        Assert.True(readTask.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(BrokerTextRunResult.Exited(0), result);
        Assert.Contains("fd-secret", readTask.Result);
    }

    [Fact]
    public void RuntimeNoResponseIsOutcomeUnknownWithoutRetry()
    {
        var pipe = ProtocolTestSupport.NewPipeName();
        using var server = new BrokerSocketServer(pipe, new BrokerRequestHandler(
            catalog: _ => Array.Empty<BrokerCatalogItem>(),
            requestStatus: (_, _) => null,
            textRun: (_, _, _) => throw new BrokerTextRuntimeException(BrokerTextRuntimeError.SpawnFailed)));
        server.Start();

        using var channel = BrokerRuntimeChannel.CreateCaller();
        var exception = Assert.Throws<BrokerSocketException>(() => new BrokerSocketClient(pipe).Run(
            new BrokerTextRunRequest("spawn-failed", new[] { "nope" }, new[] { "TOKEN" }), channel));
        Assert.Equal(BrokerSocketError.BrokerFailure, exception.Error);
        Assert.Equal(BrokerErrorCode.InternalError, exception.BrokerFailure);
    }
}

