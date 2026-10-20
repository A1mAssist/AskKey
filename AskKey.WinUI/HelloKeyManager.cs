using Windows.Security.Credentials.UI;

namespace AskKeyApp;

/// <summary>
/// Windows Hello gate for the guard service. KeyCredentialManager cannot be used from a
/// packaged desktop app: its Request* methods need a UWP CoreWindow, so the prompt is
/// owned by a separate process, never reaches the foreground, and fails after the PIN.
/// UserConsentVerifier accepts an owner window, so the prompt is modal to Ask Key.
/// </summary>
internal static class HelloKeyManager
{
    public static async Task<bool> IsSupportedAsync()
    {
        try
        {
            return await UserConsentVerifier.CheckAvailabilityAsync()
                == UserConsentVerifierAvailability.Available;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Shows the Hello prompt owned by the Ask Key window.</summary>
    public static async Task<(bool Ok, bool Canceled, string? Error)> VerifyAsync(string reason)
    {
        var owner = OwnerHandle();
        if (owner == IntPtr.Zero)
        {
            return (false, false, "Bring the Ask Key window to the foreground and try again.");
        }

        var message = Loc.T(reason);
        try
        {
            // The interop call runs off the UI thread, the same way ManagementPrompt
            // does it: the WinUI thread is an STA that must keep pumping while the
            // system prompt is up.
            var result = await Task.Run(() => RequestAsync(owner, message));
            return result switch
            {
                UserConsentVerificationResult.Verified => (true, false, (string?)null),
                UserConsentVerificationResult.Canceled => (false, true, (string?)null),
                _ => (false, false, ManagementPrompt.MessageFor(result)),
            };
        }
        catch (Exception error)
        {
            return (false, false, Describe(error));
        }
    }

    private static async Task<UserConsentVerificationResult> RequestAsync(IntPtr owner, string message)
        => await UserConsentVerifierInterop.RequestVerificationForWindowAsync(owner, message);

    private static IntPtr OwnerHandle()
    {
        var window = App.CurrentWindow;
        return window is null ? IntPtr.Zero : WindowHelpers.Handle(window);
    }

    // WinRT failures carry no Message, which would surface as an empty dialog.
    private static string Describe(Exception error)
        => string.IsNullOrWhiteSpace(error.Message)
            ? Loc.F("Windows Hello failed (0x{0:X8}). Try again.", error.HResult)
            : error.Message;
}
