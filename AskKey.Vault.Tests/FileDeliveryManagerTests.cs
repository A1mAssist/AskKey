using System.Text;

namespace AskKey.Vault.Tests;

public sealed class FileDeliveryManagerTests
{
    [Fact]
    public void StartupRemovesStaleNamespacesUnderTheDeliveryRoot()
    {
        using var root = new TestDirectory();
        var deliveryRoot = root.Combine("file-deliveries");
        PrivateFileSystem.CreatePrivateDirectory(deliveryRoot);
        var staleNamespace = CreateNamespace(deliveryRoot);
        _ = CreateStaleInstance(staleNamespace);
        var currentNamespace = Path.Combine(deliveryRoot, Guid.NewGuid().ToString("N"));

        using var manager = new FileDeliveryManager(currentNamespace);

        Assert.False(Directory.Exists(staleNamespace));
        Assert.True(Directory.Exists(currentNamespace));
    }

    [Fact]
    public void StartupKeepsAnotherLiveNamespace()
    {
        using var root = new TestDirectory();
        var deliveryRoot = root.Combine("file-deliveries");
        PrivateFileSystem.CreatePrivateDirectory(deliveryRoot);
        var firstNamespace = Path.Combine(deliveryRoot, Guid.NewGuid().ToString("N"));
        using var first = new FileDeliveryManager(firstNamespace);
        using var delivery = first.Materialize("credential", Encoding.UTF8.GetBytes("secret"));
        var secondNamespace = Path.Combine(deliveryRoot, Guid.NewGuid().ToString("N"));

        using var second = new FileDeliveryManager(secondNamespace);

        Assert.Equal("secret", File.ReadAllText(delivery.Path));
    }

    [Fact]
    public void RetryDoesNotHoldTheOwnerLeaseWhileRemovingTheDirectory()
    {
        using var root = new TestDirectory();
        var deliveryRoot = root.Combine("file-deliveries");
        PrivateFileSystem.CreatePrivateDirectory(deliveryRoot);
        var staleNamespace = CreateNamespace(deliveryRoot);
        var staleInstance = CreateStaleInstance(staleNamespace);
        var retry = default(Action);
        var attempts = 0;

        using var manager = new FileDeliveryManager(
            Path.Combine(deliveryRoot, Guid.NewGuid().ToString("N")),
            retryDelay: TimeSpan.FromMilliseconds(1),
            schedule: (_, action) => retry = action,
            removeItem: path =>
            {
                attempts++;
                if (attempts == 1) throw new IOException("synthetic first failure");
                Directory.Delete(path, recursive: true);
            });

        Assert.NotNull(retry);
        retry!();
        Assert.False(Directory.Exists(staleInstance));
        Assert.Empty(manager.CleanupFailures);
    }
    private static string CreateNamespace(string deliveryRoot)
    {
        var path = Path.Combine(deliveryRoot, Guid.NewGuid().ToString("N"));
        PrivateFileSystem.CreatePrivateDirectory(path);
        return path;
    }

    private static string CreateStaleInstance(string namespaceRoot)
    {
        var instance = Path.Combine(namespaceRoot, "instance-" + Guid.NewGuid());
        PrivateFileSystem.CreatePrivateDirectory(instance);
        File.WriteAllText(Path.Combine(instance, Guid.NewGuid().ToString()), "stale secret");
        using (PrivateFileSystem.CreatePrivateFile(Path.Combine(instance, ".owner-lock"),
                   FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
        }
        return instance;
    }
}
