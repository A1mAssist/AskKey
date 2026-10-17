namespace AskKey.Integrations.Tests;

public class HelperTrustTests
{
    private static string WriteHelper(string directory, string name = "askkey.exe")
    {
        Directory.CreateDirectory(directory);
        return TestFiles.WriteExecutable(Path.Combine(directory, name));
    }

    [Fact]
    public void UnsignedReleaseHelperIsRejected()
    {
        using var temp = new TempDirectory();
        var host = WriteHelper(temp.Combine("app"), "AskKey.exe");
        var helper = WriteHelper(temp.Combine("app"), "askkey.exe");
        var trust = new HelperTrust(new MapAuthenticodeVerifier(), _ => null);

        Assert.False(trust.IsDevelopment);
        Assert.False(trust.IsTrusted(helper, host));
    }

    [Fact]
    public void SignedHelperWithMatchingSubjectIsAccepted()
    {
        using var temp = new TempDirectory();
        var host = WriteHelper(temp.Combine("app"), "AskKey.exe");
        var helper = WriteHelper(temp.Combine("helper"), "askkey.exe");
        var verifier = new MapAuthenticodeVerifier()
            .Sign(host, "CN=Ask Key, O=Ask Key")
            .Sign(helper, "CN=Ask Key, O=Ask Key");
        var trust = new HelperTrust(verifier, _ => null);

        Assert.True(trust.IsTrusted(helper, host));
    }

    [Fact]
    public void SignerMismatchIsRejected()
    {
        using var temp = new TempDirectory();
        var host = WriteHelper(temp.Combine("app"), "AskKey.exe");
        var helper = WriteHelper(temp.Combine("helper"), "askkey.exe");
        var verifier = new MapAuthenticodeVerifier()
            .Sign(host, "CN=Ask Key")
            .Sign(helper, "CN=Someone Else");
        var trust = new HelperTrust(verifier, _ => null);

        Assert.False(trust.IsTrusted(helper, host));
    }

    [Fact]
    public void SignedHelperOutsideTheHostDirectoryNeedsTheSameSubject()
    {
        using var temp = new TempDirectory();
        var host = WriteHelper(temp.Combine("app"), "AskKey.exe");
        var helper = WriteHelper(temp.Combine("other"), "askkey.exe");
        var verifier = new MapAuthenticodeVerifier().Sign(host, "CN=Ask Key");
        var trust = new HelperTrust(verifier, _ => null);

        Assert.False(trust.IsTrusted(helper, host));
    }

    [Fact]
    public void DevelopmentBuildAllowsAnUnsignedHelperBesideTheHost()
    {
        using var temp = new TempDirectory();
        var host = WriteHelper(temp.Combine("app"), "AskKey.exe");
        var helper = WriteHelper(temp.Combine("app"), "askkey.exe");
        var trust = new HelperTrust(new MapAuthenticodeVerifier(), name =>
            name == HelperTrust.DevelopmentVariable ? "1" : null);

        Assert.True(trust.IsDevelopment);
        Assert.True(trust.IsTrusted(helper, host));
    }

    [Fact]
    public void DevelopmentBuildStillRejectsAnUnsignedHelperElsewhere()
    {
        using var temp = new TempDirectory();
        var host = WriteHelper(temp.Combine("app"), "AskKey.exe");
        var helper = WriteHelper(temp.Combine("downloads"), "askkey.exe");
        var trust = new HelperTrust(new MapAuthenticodeVerifier(), name =>
            name == HelperTrust.DevelopmentVariable ? "1" : null);

        Assert.False(trust.IsTrusted(helper, host));
    }

    [Fact]
    public void NonPeFileIsNeverAHelper()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("askkey.exe", "#!/bin/sh\necho hi\n");

        Assert.False(HelperTrust.IsExecutableRegularFile(path));
    }

    [Fact]
    public void SignedCodexHelperSigningRejectsAnUnsignedReleaseHelper()
    {
        using var temp = new TempDirectory();
        var host = WriteHelper(temp.Combine("app"), "AskKey.exe");
        var helper = WriteHelper(temp.Combine("app"), "askkey.exe");
        var signing = new CodexHelperSigning(path =>
            new HelperTrust(new MapAuthenticodeVerifier(), _ => null).IsTrusted(path, host));

        Assert.False(signing.IsTrusted(helper));
    }
}