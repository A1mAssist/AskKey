using AskKey.Vault;

namespace AskKeyApp.Tests;

public sealed class ManagementAuthenticationPolicyTests
{
    [Fact]
    public void SensitiveReasonsRequireConfirmationInsideASession()
    {
        Assert.False(ManagementAuthenticationPolicy.RequiresConfirmation(
            hasActiveSession: true, CredentialManagementCopy.ManageReason));
        Assert.True(ManagementAuthenticationPolicy.RequiresConfirmation(
            hasActiveSession: true, CredentialManagementCopy.RevealReason));
        Assert.True(ManagementAuthenticationPolicy.RequiresConfirmation(
            hasActiveSession: true, CredentialManagementCopy.AllowPermissionReason));
        Assert.True(ManagementAuthenticationPolicy.RequiresConfirmation(
            hasActiveSession: false, CredentialManagementCopy.ManageReason));
    }

    [Fact]
    public void ConfirmationIsReusedOnlyForTheSameReasonAndLiveSession()
    {
        var active = false;
        var prompts = 0;
        var authenticator = ManagementAuthenticationPolicy.From(
            _ => { prompts++; return true; },
            () => active);

        Assert.True(authenticator.Confirm(CredentialManagementCopy.RevealReason));
        Assert.Equal(1, prompts);

        // BeginManagementSession opens the session after this confirmation.
        active = true;
        Assert.True(authenticator.Confirm(CredentialManagementCopy.RevealReason));
        Assert.Equal(1, prompts);

        Assert.True(authenticator.Confirm(CredentialManagementCopy.ManageReason));
        Assert.Equal(1, prompts);

        Assert.True(authenticator.Confirm(CredentialManagementCopy.AllowPermissionReason));
        Assert.Equal(2, prompts);

        // Once the session ends, even a previously confirmed reason must ask again.
        active = false;
        Assert.True(authenticator.Confirm(CredentialManagementCopy.RevealReason));
        Assert.Equal(3, prompts);
    }

    [Fact]
    public void ADeclinedConfirmationIsNotCached()
    {
        var prompts = 0;
        var authenticator = ManagementAuthenticationPolicy.From(
            _ => { prompts++; return prompts > 1; },
            () => false);

        Assert.False(authenticator.Confirm(CredentialManagementCopy.RevealReason));
        Assert.True(authenticator.Confirm(CredentialManagementCopy.RevealReason));
        Assert.Equal(2, prompts);
    }
}