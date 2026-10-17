using Windows.Security.Credentials.UI;

namespace AskKeyApp.Tests;

public sealed class ManagementPromptTests
{
    [Theory]
    [InlineData(UserConsentVerificationResult.DeviceNotPresent)]
    [InlineData(UserConsentVerificationResult.NotConfiguredForUser)]
    public void MissingOrUnconfiguredHelloExplainsWhereToSetItUp(UserConsentVerificationResult result)
    {
        Assert.Equal(
            "Windows Hello or a PIN is required to confirm this action. Set it up in Windows Settings > Accounts > Sign-in options.",
            ManagementPrompt.MessageFor(result));
    }

    [Fact]
    public void PolicyFailureIsReportedAsPolicyFailure()
    {
        Assert.Equal(
            "Windows Hello has been disabled by policy.",
            ManagementPrompt.MessageFor(UserConsentVerificationResult.DisabledByPolicy));
    }

    [Fact]
    public void BusyDeviceIsReportedAsRetryable()
    {
        Assert.Equal(
            "Windows Hello is busy. Wait a moment and try again.",
            ManagementPrompt.MessageFor(UserConsentVerificationResult.DeviceBusy));
    }

    [Fact]
    public void ExhaustedRetriesIsReportedAsRetryable()
    {
        Assert.Equal(
            "Too many Windows Hello attempts failed. Wait and try again.",
            ManagementPrompt.MessageFor(UserConsentVerificationResult.RetriesExhausted));
    }
}
