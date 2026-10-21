using System.Diagnostics;
using System.Text;
using AskKey.Broker.Approval;
using AskKey.Broker.FileWrite;
using AskKey.Broker.Protocol;
using AskKey.Broker.Runtime;
using AskKey.Broker.Transport;

namespace AskKey.Broker.Tests;

public sealed class BrokerTextRunDeclarationTests
{
    [Fact]
    public void LegacyEncodedRequestDecodesWithoutCallerFields()
    {
        const string json = """
            {"command":["cmd.exe"],"credentialNames":["TOKEN"],"inheritedEnvironment":{},"operationID":"legacy-op"}
            """;
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var request = BrokerJson.DecodeTextRunRequest(document.RootElement);
        Assert.Equal("legacy-op", request.OperationId);
        Assert.Null(request.CallerName);
        Assert.Null(request.CallerPurpose);
        Assert.True(request.DeclarationsAreValid);
    }

    [Fact]
    public void ExplicitDeclarationsRoundTripAndStayOutOfInheritedEnvironment()
    {
        var request = new BrokerTextRunRequest(
            "declared-op",
            new[] { "cmd.exe" },
            new[] { "TOKEN" },
            inheritedEnvironment: new Dictionary<string, string>
            {
                ["PATH"] = "C:\\Windows",
                ["SECRET"] = "must-not-travel",
            },
            callerName: "  Codex  ",
            callerPurpose: "deploy the nightly");
        Assert.Equal("Codex", request.CallerName);
        Assert.Equal("deploy the nightly", request.CallerPurpose);
        var inherited = BrokerTextRunRequest.FilteredInheritedEnvironment(request.InheritedEnvironment);
        Assert.Equal("C:\\Windows", inherited["PATH"]);
        Assert.False(inherited.ContainsKey("SECRET"));

        var encoded = BrokerJson.EncodeTextRunRequest(request);
        using var document = System.Text.Json.JsonDocument.Parse(encoded.ToJsonString());
        var decoded = BrokerJson.DecodeTextRunRequest(document.RootElement);
        Assert.Equal("Codex", decoded.CallerName);
        Assert.Equal("deploy the nightly", decoded.CallerPurpose);
        Assert.Equal(request, decoded);
    }

    [Fact]
    public void WindowsInheritedEnvironmentKeysArePreserved()
    {
        var inherited = BrokerTextRunRequest.FilteredInheritedEnvironment(new Dictionary<string, string>
        {
            ["USERPROFILE"] = "C:\\Users\\a",
            ["HOMEDRIVE"] = "C:",
            ["HOMEPATH"] = "\\Users\\a",
            ["TEMP"] = "C:\\Temp",
            ["TMP"] = "C:\\Temp",
            ["SystemRoot"] = "C:\\Windows",
            ["ComSpec"] = "C:\\Windows\\System32\\cmd.exe",
            ["PATHEXT"] = ".COM;.EXE",
            ["ProgramData"] = "C:\\ProgramData",
            ["LC_ALL"] = "C",
            ["ASKKEY_SECRET"] = "no",
        });
        Assert.Equal(10, inherited.Count);
        Assert.Equal("C:\\Windows", inherited["SystemRoot"]);
        // Win32-OpenSSH resolves its crypto config under ProgramData and exits 255
        // without this key, so a run that cannot reach ssh looks like a spawn failure.
        Assert.Equal("C:\\ProgramData", inherited["ProgramData"]);
        Assert.False(inherited.ContainsKey("ASKKEY_SECRET"));
    }

    [Fact]
    public void WindowsEnvironmentNameCasingIsPreservedAndAccepted()
    {
        var inherited = BrokerTextRunRequest.FilteredInheritedEnvironment(new Dictionary<string, string>
        {
            ["Path"] = "C:\\Windows\\System32",
            ["systemroot"] = "C:\\Windows",
        });
        Assert.Equal("C:\\Windows\\System32", inherited["Path"]);
        Assert.Equal("C:\\Windows", inherited["systemroot"]);
    }

    [Fact]
    public void WhitespaceOnlyDeclarationChangesShareTheNormalizedDigest()
    {
        var ledger = new BrokerRuntimeOperations();
        var first = new BrokerTextRunRequest(
            "normalized-op",
            new[] { "cmd.exe" },
            new[] { "TOKEN" },
            callerName: "  Codex  ",
            callerPurpose: " deploy ");
        Assert.Equal(BrokerTextRunResult.Exited(0), ledger.Perform(first, new BrokerCancellation(), () => BrokerTextRunResult.Exited(0)));
        var second = new BrokerTextRunRequest(
            "normalized-op",
            new[] { "cmd.exe" },
            new[] { "TOKEN" },
            callerName: "Codex",
            callerPurpose: "deploy");
        Assert.Equal(
            BrokerTextRunResult.Exited(0),
            ledger.Perform(second, new BrokerCancellation(), () =>
            {
                Assert.Fail("normalized retransmission must reuse the receipt");
                return BrokerTextRunResult.Exited(1);
            }));
    }

    [Fact]
    public void OverlongAndControlDeclarationsAreRejected()
    {
        Assert.False(new BrokerTextRunRequest(
            "op", new[] { "cmd.exe" }, new[] { "TOKEN" },
            callerName: new string('x', BrokerLimits.MaximumFieldBytes + 1)).DeclarationsAreValid);
        Assert.False(new BrokerTextRunRequest(
            "op", new[] { "cmd.exe" }, new[] { "TOKEN" },
            callerPurpose: "line\u0007bell").DeclarationsAreValid);
        Assert.True(new BrokerTextRunRequest(
            "op", new[] { "cmd.exe" }, new[] { "TOKEN" },
            callerName: "   ").DeclarationsAreValid);
    }

    [Fact]
    public void ChangingDeclarationOnTheSameOperationIsAPayloadMismatch()
    {
        var ledger = new BrokerRuntimeOperations();
        var first = new BrokerTextRunRequest(
            "same-op", new[] { "cmd.exe" }, new[] { "TOKEN" },
            callerName: "Codex", callerPurpose: "first purpose");
        _ = ledger.Perform(first, new BrokerCancellation(), () => BrokerTextRunResult.Exited(0));
        var error = Assert.Throws<BrokerApprovalException>(() => ledger.Perform(
            new BrokerTextRunRequest(
                "same-op", new[] { "cmd.exe" }, new[] { "TOKEN" },
                callerName: "Codex", callerPurpose: "replaced after freeze"),
            new BrokerCancellation(),
            () => BrokerTextRunResult.Exited(0)));
        Assert.Equal(BrokerApprovalError.PayloadMismatch, error.Error);
    }
}

public sealed class RuntimeReceiptBoundsTests
{
    [Fact]
    public void InvalidRuntimeRequestsDoNotConsumeReceiptCapacity()
    {
        Assert.Equal(65_536, BrokerLimits.MaximumRuntimeReceiptCount);
        var resolverCalls = 0;
        var runtime = new BrokerTextRuntime(
            (_, _) =>
            {
                resolverCalls++;
                return BrokerTextCredentialResolution.Resolved(
                    new[] { new BrokerTextCredential("TOKEN", "synthetic") });
            },
            receiptCapacity: 2);

        var invalid = new (BrokerTextRunRequest Request, BrokerTextRuntimeError Error)[]
        {
            (new BrokerTextRunRequest("missing-command", Array.Empty<string>(), new[] { "TOKEN" }),
                BrokerTextRuntimeError.MissingCommand),
            (new BrokerTextRunRequest("missing-credentials", new[] { "cmd.exe" }, Array.Empty<string>()),
                BrokerTextRuntimeError.MissingCredentials),
            (new BrokerTextRunRequest("duplicate-credentials", new[] { "cmd.exe" }, new[] { "TOKEN", "TOKEN" }),
                BrokerTextRuntimeError.InvalidRequest),
            (new BrokerTextRunRequest(
                "invalid-declaration", new[] { "cmd.exe" }, new[] { "TOKEN" },
                callerName: new string('x', BrokerLimits.MaximumFieldBytes + 1)),
                BrokerTextRuntimeError.InvalidRequest),
            (new BrokerTextRunRequest("invalid-command-field", new[] { "cmd.exe\0" }, new[] { "TOKEN" }),
                BrokerTextRuntimeError.InvalidRequest),
        };
        foreach (var (request, expected) in invalid)
        {
            using var channel = NullRuntimeChannel.Create();
            Assert.Equal(expected, Assert.Throws<BrokerTextRuntimeException>(() => runtime.Run(request, channel)).Error);
        }

        foreach (var operationId in new[] { "valid-one", "valid-two" })
        {
            using var channel = NullRuntimeChannel.Create();
            Assert.Equal(
                BrokerTextRunResult.Exited(0),
                runtime.Run(TrueRequest(operationId), channel));
        }
        using (var overflow = NullRuntimeChannel.Create())
        {
            Assert.Equal(
                BrokerProviderError.ResourceExhausted,
                Assert.Throws<BrokerProviderException>(() => runtime.Run(TrueRequest("valid-three"), overflow)).Error);
        }
        Assert.Equal(2, resolverCalls);
    }

    [Fact]
    public void SocketCapacityRejectsOnlyNewOperationIDsAndRetainsCompletedReceipts()
    {
        var starts = 0;
        var runtime = new BrokerTextRuntime(
            (_, _) => BrokerTextCredentialResolution.Resolved(
                new[] { new BrokerTextCredential("TOKEN", "synthetic") }),
            beforeSystemSpawn: () => starts++,
            receiptCapacity: 2);
        var pipe = ProtocolTestSupport.NewPipeName();
        using var server = new BrokerSocketServer(pipe, new BrokerRequestHandler(
            catalog: _ => Array.Empty<BrokerCatalogItem>(),
            requestStatus: (_, _) => null,
            textRun: runtime.Run));
        server.Start();
        var client = ProtocolTestSupport.Client(pipe);

        Assert.Equal(
            BrokerErrorCode.InvalidRequest,
            Assert.Throws<BrokerSocketException>(() => RunWithNullChannel(
                client, new BrokerTextRunRequest("socket-invalid", Array.Empty<string>(), new[] { "TOKEN" })))
                .BrokerFailure);
        Assert.Equal(BrokerTextRunResult.Exited(0), RunWithNullChannel(client, TrueRequest("socket-first")));
        Assert.Equal(BrokerTextRunResult.Exited(0), RunWithNullChannel(client, TrueRequest("socket-second")));
        Assert.Equal(
            BrokerErrorCode.ResourceExhausted,
            Assert.Throws<BrokerSocketException>(() => RunWithNullChannel(client, TrueRequest("socket-third")))
                .BrokerFailure);
        Assert.Equal(BrokerTextRunResult.Exited(0), RunWithNullChannel(client, TrueRequest("socket-first")));
        Assert.Equal(2, starts);
    }

    [Fact]
    public void PendingApprovalCanResumeAtCapacityAndConcurrentRetransmissionSpawnsOnce()
    {
        const string approvalOperationId = "approval-operation";
        var resolverCalls = 0;
        var starts = 0;
        using var resumeEntered = new ManualResetEventSlim(false);
        using var resumeRelease = new ManualResetEventSlim(false);
        var ticket = new BrokerApprovalTicket("approval-request", "approval-capability", BrokerRequestState.Pending, 0);
        var runtime = new BrokerTextRuntime(
            (request, cancellation) =>
            {
                _ = cancellation;
                if (request.OperationId != approvalOperationId)
                {
                    return BrokerTextCredentialResolution.Resolved(
                        new[] { new BrokerTextCredential("TOKEN", "synthetic") });
                }
                if (Interlocked.Increment(ref resolverCalls) == 1)
                {
                    return BrokerTextCredentialResolution.ApprovalRequired(new[] { ticket });
                }
                resumeEntered.Set();
                _ = resumeRelease.Wait(TimeSpan.FromSeconds(5));
                return BrokerTextCredentialResolution.Resolved(
                    new[] { new BrokerTextCredential("TOKEN", "synthetic") });
            },
            beforeSystemSpawn: () => starts++,
            receiptCapacity: 2);
        var approval = TrueRequest(approvalOperationId);

        using (var channel = NullRuntimeChannel.Create())
        {
            Assert.Equal(
                BrokerTextRunResult.ApprovalRequired(approvalOperationId, new[] { ticket }),
                runtime.Run(approval, channel));
        }
        using (var filler = NullRuntimeChannel.Create())
        {
            Assert.Equal(BrokerTextRunResult.Exited(0), runtime.Run(TrueRequest("approval-filler"), filler));
        }
        using (var overflow = NullRuntimeChannel.Create())
        {
            Assert.Equal(
                BrokerProviderError.ResourceExhausted,
                Assert.Throws<BrokerProviderException>(() => runtime.Run(TrueRequest("approval-overflow"), overflow)).Error);
        }

        var results = new List<BrokerTextRunResult>();
        var errors = new List<Exception>();
        var first = Task.Run(() =>
        {
            using var channel = NullRuntimeChannel.Create();
            try { lock (results) results.Add(runtime.Run(approval, channel)); }
            catch (Exception error) { lock (errors) errors.Add(error); }
        });
        var second = Task.Run(() =>
        {
            using var channel = NullRuntimeChannel.Create();
            try { lock (results) results.Add(runtime.Run(approval, channel)); }
            catch (Exception error) { lock (errors) errors.Add(error); }
        });
        Assert.True(resumeEntered.Wait(TimeSpan.FromSeconds(5)));
        resumeRelease.Set();
        Task.WaitAll(new[] { first, second }, TimeSpan.FromSeconds(10));
        Assert.Empty(errors);
        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.Equal(BrokerTextRunResult.Exited(0), result));
        Assert.Equal(2, resolverCalls);
        Assert.Equal(2, starts);

        using var replay = NullRuntimeChannel.Create();
        Assert.Equal(BrokerTextRunResult.Exited(0), runtime.Run(approval, replay));
        Assert.Equal(2, resolverCalls);
        Assert.Equal(2, starts);
    }

    [Fact]
    public void CompletedReceiptIsReturnedAfterWorkingDirectoryDisappears()
    {
        var directory = ProtocolTestSupport.NewDirectory();
        var starts = 0;
        var runtime = new BrokerTextRuntime(
            (_, _) => BrokerTextCredentialResolution.Resolved(
                new[] { new BrokerTextCredential("TOKEN", "synthetic") }),
            beforeSystemSpawn: () => starts++,
            receiptCapacity: 2);
        var request = new BrokerTextRunRequest(
            "mutable-working-directory",
            new[] { "cmd.exe" },
            new[] { "TOKEN" },
            workingDirectory: directory);
        try
        {
            using (var channel = NullRuntimeChannel.Create())
            {
                Assert.Equal(BrokerTextRunResult.Exited(0), runtime.Run(request, channel));
            }
            Directory.Delete(directory, recursive: true);
            using (var replay = NullRuntimeChannel.Create())
            {
                Assert.Equal(BrokerTextRunResult.Exited(0), runtime.Run(request, replay));
            }
            Assert.Equal(1, starts);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static BrokerTextRunRequest TrueRequest(string operationId)
        => new(operationId, new[] { "cmd.exe", "/c", "exit 0" }, new[] { "TOKEN" });

    /// <summary>
    /// Socket path: the Broker connects the caller-owned pipes itself, so the
    /// caller only closes stdin and drains output once the channel is connected.
    /// </summary>
    private static BrokerTextRunResult RunWithNullChannel(
        BrokerSocketClient client, BrokerTextRunRequest request)
    {
        using var channel = BrokerRuntimeChannel.CreateCaller();
        var drain = Task.Run(async () =>
        {
            await channel.CloseStdinAfterConnectAsync();
            await Task.WhenAll(DrainAsync(channel.Stdout), DrainAsync(channel.Stderr));
        });
        var result = client.Run(request, channel);
        drain.Wait(TimeSpan.FromSeconds(5));
        return result;
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

public sealed class DebugRunDirectoryTests
{
    [Fact]
    public void PrivateDirectoryIsAcceptedAndInvalidPathsFailClosed()
    {
        var root = ProtocolTestSupport.NewDirectory();
        BrokerAcl.SecureDirectory(root);
        try
        {
            var environment = new System.Collections.Hashtable
            {
                [DebugRunDirectory.EnvironmentVariable] = root,
            };
            var resolved = DebugRunDirectory.Resolve(environment, Array.Empty<string>());
            Assert.Equal(Path.GetFullPath(root), resolved);

            var missing = Path.Combine(root, "missing");
            Assert.Throws<DebugRunDirectoryException>(() => DebugRunDirectory.Resolve(
                new System.Collections.Hashtable { [DebugRunDirectory.EnvironmentVariable] = missing },
                Array.Empty<string>()));
            Assert.Throws<DebugRunDirectoryException>(() => DebugRunDirectory.Resolve(
                new System.Collections.Hashtable { [DebugRunDirectory.EnvironmentVariable] = "relative\\dir" },
                Array.Empty<string>()));

            var real = Path.Combine(root, "AskKey");
            Directory.CreateDirectory(real);
            BrokerAcl.SecureDirectory(real);
            Assert.Throws<DebugRunDirectoryException>(() => DebugRunDirectory.Resolve(
                new System.Collections.Hashtable { [DebugRunDirectory.EnvironmentVariable] = real },
                new[] { real }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
