using AskKey.Broker.Approval;
using AskKey.Broker.Protocol;
using AskKey.Broker.Runtime;
using AskKey.Broker.Transport;

namespace AskKey.Broker.Tests;

/// <summary>
/// A locked vault must fail the request closed without executing anything, and the
/// same operation ID must still work once the user unlocks Ask Key.
/// </summary>
public sealed class VaultLockedTests
{
    [Fact]
    public void VaultLockedRoundTripsOnTheWire()
    {
        Assert.Equal("vault_locked", BrokerWire.ToWire(BrokerErrorCode.VaultLocked));
        Assert.True(BrokerWire.TryParseErrorCode("vault_locked", out var code));
        Assert.Equal(BrokerErrorCode.VaultLocked, code);

        var encoded = BrokerJson.Encode(BrokerResponse.Failure(BrokerErrorCode.VaultLocked));
        var decoded = BrokerJson.DecodeResponse(encoded!);
        Assert.Equal(BrokerResponse.Failure(BrokerErrorCode.VaultLocked), decoded);
    }

    [Fact]
    public void AProviderErrorMapsToTheVaultLockedWireCode()
    {
        var handler = new BrokerRequestHandler(
            catalog: _ => throw new BrokerProviderException(BrokerProviderError.VaultLocked),
            requestStatus: (_, _) => null);
        var response = handler.Handle(new BrokerRequest
        {
            Version = BrokerProtocolVersion.Current,
            Method = "catalog",
        });
        Assert.Equal(BrokerResponse.Failure(BrokerErrorCode.VaultLocked), response);
    }

    /// <summary>
    /// The receipt is created inside Perform, so a lock that is only detected there
    /// would store the failure and replay it forever. The preflight runs before the
    /// receipt, so the retry after the unlock runs the command.
    /// </summary>
    [Fact]
    public void ALockedPreflightDoesNotPoisonTheOperationReceipt()
    {
        var locked = true;
        var starts = 0;
        var runtime = new BrokerTextRuntime(
            (_, _) => BrokerTextCredentialResolution.Resolved(
                new[] { new BrokerTextCredential("TOKEN", "synthetic") }),
            beforeRequest: () =>
            {
                if (locked) throw new BrokerProviderException(BrokerProviderError.VaultLocked);
            },
            beforeSystemSpawn: () => starts++);
        var request = new BrokerTextRunRequest("locked-op", new[] { "cmd.exe", "/c", "exit 0" }, new[] { "TOKEN" });

        using (var channel = NullRuntimeChannel.Create())
        {
            Assert.Equal(
                BrokerProviderError.VaultLocked,
                Assert.Throws<BrokerProviderException>(() => runtime.Run(request, channel)).Error);
        }
        Assert.Equal(0, starts);

        locked = false;
        using (var channel = NullRuntimeChannel.Create())
        {
            Assert.Equal(BrokerTextRunResult.Exited(0), runtime.Run(request, channel));
        }
        Assert.Equal(1, starts);
    }

    /// <summary>
    /// A lock detected inside the operation, after the receipt was created, must also
    /// leave the operation ID usable. Otherwise the Agent's retry after the unlock
    /// would replay the stored failure instead of running the command.
    /// </summary>
    [Fact]
    public void ALockInsideTheOperationDoesNotPoisonTheOperationReceipt()
    {
        var locked = true;
        var starts = 0;
        var runtime = new BrokerTextRuntime(
            (_, _) =>
            {
                if (locked) throw new BrokerProviderException(BrokerProviderError.VaultLocked);
                return BrokerTextCredentialResolution.Resolved(
                    new[] { new BrokerTextCredential("TOKEN", "synthetic") });
            },
            beforeSystemSpawn: () => starts++);
        var request = new BrokerTextRunRequest("locked-inside", new[] { "cmd.exe", "/c", "exit 0" }, new[] { "TOKEN" });

        using (var channel = NullRuntimeChannel.Create())
        {
            Assert.Equal(
                BrokerProviderError.VaultLocked,
                Assert.Throws<BrokerProviderException>(() => runtime.Run(request, channel)).Error);
        }
        Assert.Equal(0, starts);

        locked = false;
        using (var channel = NullRuntimeChannel.Create())
        {
            Assert.Equal(BrokerTextRunResult.Exited(0), runtime.Run(request, channel));
        }
        Assert.Equal(1, starts);
    }

    [Fact]
    public void ALockedRunReachesTheClientAsAVaultLockedFailure()
    {
        var runtime = new BrokerTextRuntime(
            (_, _) => BrokerTextCredentialResolution.Resolved(
                new[] { new BrokerTextCredential("TOKEN", "synthetic") }),
            beforeRequest: () => throw new BrokerProviderException(BrokerProviderError.VaultLocked));
        var pipe = ProtocolTestSupport.NewPipeName();
        using var server = new BrokerSocketServer(pipe, new BrokerRequestHandler(
            catalog: _ => Array.Empty<BrokerCatalogItem>(),
            requestStatus: (_, _) => null,
            textRun: runtime.Run));
        server.Start();
        var client = ProtocolTestSupport.Client(pipe);

        var error = Assert.Throws<BrokerSocketException>(() => RunWithNullChannel(
            client, new BrokerTextRunRequest("locked-socket", new[] { "cmd.exe" }, new[] { "TOKEN" })));
        Assert.Equal(BrokerSocketError.BrokerFailure, error.Error);
        Assert.Equal(BrokerErrorCode.VaultLocked, error.BrokerFailure);
    }

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