using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using Windows.Security.Credentials.UI;

namespace AskKeyApp;

/// <summary>
/// Uses the Windows-owned consent prompt for every management action. The
/// decision is made by Windows Hello or the account PIN; Ask Key only receives
/// the verified/cancelled result and never sees the credential.
/// </summary>
public sealed class ManagementPrompt
{
    private readonly Func<string, bool> _confirm;

    public ManagementPrompt() : this(ConfirmWithWindows)
    {
    }

    internal ManagementPrompt(Func<string, bool> confirm) => _confirm = confirm;

    public bool Confirm(string reason) => _confirm(reason);

    private static bool ConfirmWithWindows(string reason)
    {
        var owner = GetOwnerHandle();
        if (owner == IntPtr.Zero)
        {
            ShowMessage("Ask Key could not open the Windows identity prompt. Bring the Ask Key window to the foreground and try again.");
            return false;
        }

        try
        {
            var result = Task.Run(() => RequestAsync(owner, reason)).GetAwaiter().GetResult();
            if (result == UserConsentVerificationResult.Verified) return true;
            if (result == UserConsentVerificationResult.Canceled) return false;
            ShowMessage(MessageFor(result));
            return false;
        }
        catch (Exception error)
        {
            Debug.WriteLine(error);
            ShowMessage("Ask Key could not start Windows Hello. Try again.");
            return false;
        }
    }

    private static async Task<UserConsentVerificationResult> RequestAsync(IntPtr owner, string reason)
        => await UserConsentVerifierInterop.RequestVerificationForWindowAsync(owner, reason);

    private static IntPtr GetOwnerHandle()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return IntPtr.Zero;
        return dispatcher.Invoke(() =>
        {
            var window = Application.Current!.Windows.OfType<Window>()
                             .FirstOrDefault(candidate => candidate.IsActive && candidate.IsVisible)
                         ?? Application.Current.MainWindow
                         ?? Application.Current.Windows.OfType<Window>().FirstOrDefault();
            return window is null ? IntPtr.Zero : new WindowInteropHelper(window).EnsureHandle();
        });
    }

    internal static string MessageFor(UserConsentVerificationResult result) => result switch
    {
        UserConsentVerificationResult.DeviceNotPresent or UserConsentVerificationResult.NotConfiguredForUser =>
            "Windows Hello or a PIN is required to confirm this action. Set it up in Windows Settings > Accounts > Sign-in options.",
        UserConsentVerificationResult.DisabledByPolicy => "Windows Hello has been disabled by policy.",
        UserConsentVerificationResult.DeviceBusy => "Windows Hello is busy. Wait a moment and try again.",
        UserConsentVerificationResult.RetriesExhausted => "Too many Windows Hello attempts failed. Wait and try again.",
        _ => "Windows Hello could not verify your identity. Try again.",
    };

    private static void ShowMessage(string message)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        void Show() => MessageBox.Show(
            Loc.T(message), Loc.T("Ask Key"), MessageBoxButton.OK, MessageBoxImage.Warning);
        if (dispatcher.CheckAccess()) Show();
        else dispatcher.Invoke(Show);
    }
}
