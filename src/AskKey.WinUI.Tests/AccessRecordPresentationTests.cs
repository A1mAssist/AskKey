using System.Globalization;
using AskKey.Vault;

namespace AskKeyApp.Tests;

public sealed class AccessRecordPresentationTests
{
    private static readonly Dictionary<string, string> Names = new()
    {
        ["credential"] = "Staging API",
    };

    [Fact]
    public void DeniedRuntimeReadUsesRequestWording()
    {
        var row = Build(Event(CredentialAccessOperation.RuntimeRead, CredentialAccessResult.Denied));

        Assert.Equal("Codex asked to use Staging API to run deploy.cmd", row.Description);
        Assert.Equal("Denied", row.Result);
    }

    [Fact]
    public void AllowedRuntimeReadUsesCompletedWording()
    {
        var row = Build(Event(CredentialAccessOperation.RuntimeRead, CredentialAccessResult.Allowed));

        Assert.Equal("Codex used Staging API to run deploy.cmd", row.Description);
        Assert.Equal("Allowed", row.Result);
    }

    [Fact]
    public void AllowedWriteIsPresentedAsApproved()
    {
        var row = Build(Event(CredentialAccessOperation.Create, CredentialAccessResult.Allowed));

        Assert.Equal("Codex created credential Staging API", row.Description);
        Assert.Equal("Approved", row.Result);
    }

    [Fact]
    public void HiddenNameRejectionIsNotPresentedAsACompletedCatalogRead()
    {
        var row = Build(CredentialAccessEvent.Create(
            DateTimeOffset.Parse("2026-10-18T01:02:00Z"), null, CredentialAccessOperation.Catalog,
            CredentialAccessResult.HiddenNameRejected, "Codex", "Try a hidden name"));

        Assert.Equal("Codex browsed the credential list", row.Description);
        Assert.Equal("Hidden credential request", row.Credential);
        Assert.Equal("Hidden", row.Result);
    }

    private static AccessRecordRow Build(CredentialAccessEvent record)
        => Assert.Single(AccessRecordPresentation.Build(new[] { record }, Names, CultureInfo.InvariantCulture));

    private static CredentialAccessEvent Event(
        CredentialAccessOperation operation, CredentialAccessResult result)
        => CredentialAccessEvent.Create(
            DateTimeOffset.Parse("2026-10-18T01:02:00Z"), "credential", operation, result,
            "Codex", "Deploy", "deploy.cmd");
}
