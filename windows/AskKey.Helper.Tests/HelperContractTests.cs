using System.Text;
using System.Text.Json.Nodes;
using AskKey.Broker.Protocol;
using AskKey.Broker.Runtime;

namespace AskKey.Helper.Tests;

public sealed class HelperContractTests
{
    [Fact]
    public void UsageAndExitCodeAreFixed()
    {
        var result = HelperProcess.Run(Array.Empty<string>());
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Equal(
            "Usage: askkey health | version | status | mcp | open | run --credential <name> [--credential <name> ...] [--wait-for-approval] [--operation-id <id>] [--caller-name <name>] [--caller-purpose <purpose>] -- <command> [args...]\n",
            result.StandardError);
    }

    [Fact]
    public void FixedApprovalAndOutcomeCopyIsExact()
    {
        Assert.Equal(
            "Ask Key approval is required. Retry with the same --operation-id after approval.",
            HelperCopy.ApprovalRequired);
        Assert.Equal(
            "Ask Key could not determine whether the target started; it was not retried.",
            HelperCopy.OutcomeUnknown);
        Assert.Equal(
            "Ask Key is waiting for approval. Keep this process running; it will continue after approval.",
            HelperCopy.Waiting);
        Assert.Equal(
            "Ask Key approval wait expired; the command was not resumed.",
            HelperCopy.WaitExpired);
        Assert.Equal(
            "Ask Key returned an invalid approval response; the command was not resumed.",
            HelperCopy.WaitInvalid);
        Assert.Equal("Ask Key could not open the host application.", HelperCopy.OpenFailed);
    }

    [Fact]
    public void HealthVersionAndStatusUseTheBroker()
    {
        using var broker = new BrokerHarness(TestHandlers.Catalog());
        foreach (var command in new[] { "health", "version", "status" })
        {
            var result = HelperProcess.Run(new[] { command }, broker);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("\"success\"", result.StandardOutput, StringComparison.Ordinal);
            Assert.Equal(string.Empty, result.StandardError);
        }
    }

    [Fact]
    public void OutcomeUnknownUsesFixedErrorAndExitCode()
    {
        using var broker = new BrokerHarness(TestHandlers.OutcomeUnknown());
        var result = HelperProcess.Run(
            new[] { "run", "--credential", "token", "--operation-id", "unknown", "--", "cmd" },
            broker);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Equal(HelperCopy.OutcomeUnknown + "\n", result.StandardError);
    }

    [Fact]
    public void ApprovalRequiredPrintsJsonAndFixedError()
    {
        var handler = new BrokerRequestHandler(
            catalog: _ => Array.Empty<BrokerCatalogItem>(),
            requestStatus: (_, _) => null,
            textRun: (_, _, _) => BrokerTextRunResult.ApprovalRequired(
                "op-1",
                new[] { new BrokerApprovalTicket("req-1", "cap-1", BrokerRequestState.Pending, 0) }));
        using var broker = new BrokerHarness(handler);
        var result = HelperProcess.Run(
            new[] { "run", "--credential", "token", "--operation-id", "op-1", "--", "cmd" },
            broker);
        Assert.Equal(1, result.ExitCode);
        Assert.DoesNotContain("\"success\"", result.StandardOutput, StringComparison.Ordinal);
        var approval = JsonNode.Parse(result.StandardOutput)!.AsObject();
        Assert.Equal("op-1", approval["approvalRequired"]!["operationID"]!.GetValue<string>());
        Assert.Equal(HelperCopy.ApprovalRequired + "\n", result.StandardError);
    }

    [Fact]
    public void WaitForApprovalResumesInTheSameProcess()
    {
        var runs = 0;
        var handler = new BrokerRequestHandler(
            catalog: _ => Array.Empty<BrokerCatalogItem>(),
            requestStatus: (_, _) => BrokerRequestState.Approved,
            textRun: (_, channel, _) =>
            {
                if (Interlocked.Increment(ref runs) == 1)
                {
                    return BrokerTextRunResult.ApprovalRequired(
                        "op-wait",
                        new[] { new BrokerApprovalTicket("req-wait", "cap-wait", BrokerRequestState.Pending, 0) });
                }
                channel.Stdout.Write("resumed\n"u8);
                channel.Stdout.Flush();
                return BrokerTextRunResult.Exited(0);
            });
        using var broker = new BrokerHarness(handler);
        var result = HelperProcess.Run(
            new[]
            {
                "run", "--wait-for-approval", "--credential", "token",
                "--operation-id", "op-wait", "--", "cmd",
            },
            broker);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("resumed\n", result.StandardOutput);
        Assert.Contains(HelperCopy.Waiting, result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void LargeStdoutAndStderrAreDrainedBeforeExit()
    {
        var stdout = Enumerable.Repeat((byte)'O', 1_048_576).ToArray();
        var stderr = Enumerable.Repeat((byte)'E', 262_144).ToArray();
        var handler = TestHandlers.Exited(0, channel =>
        {
            channel.Stdout.Write(stdout);
            channel.Stdout.Flush();
            channel.Stderr.Write(stderr);
            channel.Stderr.Flush();
        });
        using var broker = new BrokerHarness(handler);
        var result = HelperProcess.Run(
            new[] { "run", "--credential", "token", "--operation-id", "large", "--", "cmd" },
            broker,
            timeoutMilliseconds: 30000);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(stdout.Length, Encoding.UTF8.GetByteCount(result.StandardOutput));
        Assert.Equal(stderr.Length, Encoding.UTF8.GetByteCount(result.StandardError));
        Assert.Equal(new string('O', stdout.Length), result.StandardOutput);
        Assert.Equal(new string('E', stderr.Length), result.StandardError);
    }

    [Fact]
    public void OpenTrustGateRequiresMatchingPublisher()
    {
        var signed = new HelperHostSigningFacts { Publisher = "Ask Key", IsValid = true };
        var same = new HelperHostSigningFacts { Publisher = "ask key", IsValid = true };
        var other = new HelperHostSigningFacts { Publisher = "Contoso", IsValid = true };
        Assert.True(HelperHostSignatureTrust.Allows(signed, same, isDevelopmentBuild: false));
        Assert.False(HelperHostSignatureTrust.Allows(signed, other, isDevelopmentBuild: false));
        Assert.False(HelperHostSignatureTrust.Allows(HelperHostSigningFacts.Unsigned, signed, isDevelopmentBuild: false));
        Assert.True(HelperHostSignatureTrust.Allows(HelperHostSigningFacts.Unsigned, HelperHostSigningFacts.Unsigned, isDevelopmentBuild: true));
    }

    [Fact]
    public void OpenHostUsesTheTrustGateBeforeOpening()
    {
        var opened = new List<string>();
        var signed = new HelperHostSigningFacts { Publisher = "Ask Key", IsValid = true };
        var other = new HelperHostSigningFacts { Publisher = "Contoso", IsValid = true };
        var helper = OfficialInstallTopology.CanonicalHelperPath;
        HelperHostApplication.OpenHost(
            helper,
            isDevelopmentBuild: false,
            opened.Add,
            _ => signed,
            path => path);
        Assert.Single(opened);

        Assert.Throws<HelperHostApplicationException>(() => HelperHostApplication.OpenHost(
            helper,
            isDevelopmentBuild: false,
            opened.Add,
            path => path == helper ? signed : other,
            path => path));
        Assert.Single(opened);
    }

    [Fact]
    public void OpenWithoutHostReturnsFixedError()
    {
        var result = HelperProcess.Run(new[] { "open" });
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Equal(HelperCopy.OpenFailed + "\n", result.StandardError);
    }

    [Fact]
    public void HookCapabilitiesReportsAllClients()
    {
        var result = HelperProcess.Run(new[] { "hook", "capabilities" });
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        var node = JsonNode.Parse(result.StandardOutput)!.AsObject();
        Assert.Equal(1, node["protocolVersion"]!.GetValue<int>());
        var clients = node["clients"]!.AsArray().Select(value => value!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "cursor", "grok", "claude", "codex" }, clients);
    }
}
