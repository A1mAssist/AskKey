using System.Security.Cryptography;
using System.Text.Json;

namespace AskKey.Integrations.Tests;

public class HelperIntegrityTests
{
    private static string Hash(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string Manifest(string root, params string[] relativePaths)
    {
        var files = relativePaths.ToDictionary(
            path => path.Replace('\\', '/'),
            path => Hash(Path.Combine(root, path)),
            StringComparer.OrdinalIgnoreCase);
        return JsonSerializer.Serialize(new { version = 1, files });
    }

    [Fact]
    public void AcceptsMatchingPayload()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine("Helpers");
        var helper = TestFiles.WriteExecutable(Path.Combine(root, "askkey.exe"));
        temp.WriteBytes("Helpers/AskKey.Broker.dll", new byte[] { 1, 2, 3 });
        var integrity = new HelperIntegrity(Manifest(root, "askkey.exe", "AskKey.Broker.dll"));

        Assert.True(integrity.IsTrusted(helper));
    }

    [Fact]
    public void RejectsChangedFile()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine("Helpers");
        var helper = TestFiles.WriteExecutable(Path.Combine(root, "askkey.exe"));
        var dll = temp.WriteBytes("Helpers/AskKey.Broker.dll", new byte[] { 1, 2, 3 });
        var integrity = new HelperIntegrity(Manifest(root, "askkey.exe", "AskKey.Broker.dll"));
        File.WriteAllBytes(dll, new byte[] { 9, 9, 9 });

        Assert.False(integrity.IsTrusted(helper));
    }

    [Fact]
    public void RejectsExtraFile()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine("Helpers");
        var helper = TestFiles.WriteExecutable(Path.Combine(root, "askkey.exe"));
        var integrity = new HelperIntegrity(Manifest(root, "askkey.exe"));
        temp.WriteBytes("Helpers/AskKey.Broker.dll", new byte[] { 1, 2, 3 });

        Assert.False(integrity.IsTrusted(helper));
    }

    [Fact]
    public void RejectsMissingFile()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine("Helpers");
        var helper = TestFiles.WriteExecutable(Path.Combine(root, "askkey.exe"));
        var dll = temp.WriteBytes("Helpers/AskKey.Broker.dll", new byte[] { 1, 2, 3 });
        var integrity = new HelperIntegrity(Manifest(root, "askkey.exe", "AskKey.Broker.dll"));
        File.Delete(dll);

        Assert.False(integrity.IsTrusted(helper));
    }

    [Fact]
    public void RejectsMalformedManifest()
    {
        using var temp = new TempDirectory();
        var helper = TestFiles.WriteExecutable(temp.Combine("Helpers", "askkey.exe"));
        var integrity = new HelperIntegrity("not json");

        Assert.False(integrity.IsTrusted(helper));
    }

    [Fact]
    public void RejectsTraversalPath()
    {
        using var temp = new TempDirectory();
        var helper = TestFiles.WriteExecutable(temp.Combine("Helpers", "askkey.exe"));
        var hash = new string('a', 64);
        var integrity = new HelperIntegrity(
            "{\"version\":1,\"files\":{\"../evil.dll\":\"" + hash + "\"}}");

        Assert.False(integrity.IsTrusted(helper));
    }

    [Fact]
    public void UnsignedBuildUsesPinnedManifestWhenAllowed()
    {
        using var temp = new TempDirectory();
        var host = TestFiles.WriteExecutable(temp.Combine("app", "AskKey.exe"));
        var root = temp.Combine("Helpers");
        var helper = TestFiles.WriteExecutable(Path.Combine(root, "askkey.exe"));
        var integrity = new HelperIntegrity(Manifest(root, "askkey.exe"));
        var trust = new HelperTrust(
            new MapAuthenticodeVerifier(), _ => null, integrity, allowUnsignedIntegrity: true);

        Assert.True(trust.IsTrusted(helper, host));

        File.WriteAllBytes(helper, new byte[] { (byte)'M', (byte)'Z', 9, 9, 9 });
        Assert.False(trust.IsTrusted(helper, host));
    }

    [Fact]
    public void UnsignedBuildDoesNotUsePinnedManifestByDefault()
    {
        using var temp = new TempDirectory();
        var host = TestFiles.WriteExecutable(temp.Combine("app", "AskKey.exe"));
        var root = temp.Combine("Helpers");
        var helper = TestFiles.WriteExecutable(Path.Combine(root, "askkey.exe"));
        var integrity = new HelperIntegrity(Manifest(root, "askkey.exe"));
        var trust = new HelperTrust(
            new MapAuthenticodeVerifier(), _ => null, integrity, allowUnsignedIntegrity: false);

        Assert.False(trust.IsTrusted(helper, host));
    }
}
