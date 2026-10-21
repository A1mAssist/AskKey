using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AskKey.Broker.Approval;
using AskKey.Broker.Protocol;

namespace AskKey.Helper.Tests;

public sealed class VaultLockedHelperTests
{
    [Fact]
    public void ACatalogFailureWhileLockedReportsVaultLockedWithItsNextStep()
    {
        var handler = new BrokerRequestHandler(
            catalog: _ => throw new BrokerProviderException(BrokerProviderError.VaultLocked),
            requestStatus: (_, _) => null);
        using var broker = new BrokerHarness(handler);
        var output = RunMcp(broker, ToolCall(1, "list_credentials", new JsonObject()));
        var response = Lines(output).Single();
        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        var payload = JsonNode.Parse(response["result"]!["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
        Assert.Equal("vault_locked", payload["status"]!.GetValue<string>());
        Assert.Equal("vault_locked", payload["brokerCode"]!.GetValue<string>());
        Assert.Equal(AgentUsageGuide.VaultLocked, payload["next_step"]!.GetValue<string>());
    }

    /// <summary>
    /// The CLI keeps the command, its cwd and its environment in memory, so a locked
    /// vault only delays the run: once the user unlocks, the same process continues.
    /// </summary>
    [Fact]
    public void TheWaitingCliResumesAfterTheVaultIsUnlocked()
    {
        var runs = 0;
        var handler = new BrokerRequestHandler(
            catalog: _ => Array.Empty<BrokerCatalogItem>(),
            requestStatus: (_, _) => null,
            textRun: (_, channel, _) =>
            {
                // The first attempt finds the vault locked; the user then unlocks it,
                // so the retry from the same process runs the command.
                if (Interlocked.Increment(ref runs) == 1)
                {
                    throw new BrokerProviderException(BrokerProviderError.VaultLocked);
                }
                channel.Stdout.Write("resumed\n"u8);
                channel.Stdout.Flush();
                return BrokerTextRunResult.Exited(0);
            });
        using var broker = new BrokerHarness(handler);
        var process = StartHelper(broker, new[]
        {
            "run", "--wait-for-approval", "--credential", "token",
            "--operation-id", "op-locked", "--", "cmd",
        });
        try
        {
            Assert.True(process.WaitForExit(15_000), "the helper did not resume");
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("resumed\n", process.StandardOutput.ReadToEnd());
            Assert.Contains(HelperCopy.WaitingForUnlock, process.StandardError.ReadToEnd(), StringComparison.Ordinal);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        Assert.Equal(2, Volatile.Read(ref runs));
    }

    private static Process StartHelper(BrokerHarness broker, string[] arguments)
    {
        var runtime = HelperProcess.SharedRuntime();
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(runtime, "askkey.exe"),
            WorkingDirectory = runtime,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        startInfo.Environment["ASKKEY_BROKER_PIPE"] = broker.PipeFile;
        var process = new Process { StartInfo = startInfo };
        process.Start();
        process.StandardInput.Close();
        return process;
    }

    private static string RunMcp(BrokerHarness broker, string input)
    {
        using var inputStream = new MemoryStream(global::System.Text.Encoding.UTF8.GetBytes(input));
        using var outputStream = new MemoryStream();
        McpServer.Run(inputStream, outputStream, broker.Client);
        return global::System.Text.Encoding.UTF8.GetString(outputStream.ToArray());
    }

    private static string ToolCall(int id, string name, JsonObject arguments)
        => JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method = "tools/call",
            @params = new { name, arguments },
        }) + "\n";

    private static List<JsonObject> Lines(string output)
        => output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)!.AsObject())
            .ToList();
}