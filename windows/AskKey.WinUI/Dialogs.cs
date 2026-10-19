using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AskKeyApp;

internal static class Dialogs
{
    public static async Task<bool> ConfirmAsync(Window? owner, string title, string message,
        string confirmText = "Confirm", bool destructive = false)
    {
        var root = Root(owner);
        if (root is null) return false;
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = Loc.T(title),
            Content = new TextBlock { Text = Loc.T(message), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = Loc.T(confirmText),
            CloseButtonText = Loc.T("Cancel"),
            DefaultButton = ContentDialogButton.None,
            RequestedTheme = ThemeFor(owner),
        };
        if (destructive) dialog.PrimaryButtonStyle = (Style)Application.Current.Resources["DangerSolidButtonStyle"];
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public static async Task<string?> PromptAsync(Window? owner, string title, string prompt)
    {
        var root = Root(owner);
        if (root is null) return null;
        var input = new TextBox { PlaceholderText = Loc.T(prompt) };
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = Loc.T(title),
            Content = input,
            PrimaryButtonText = Loc.T("Save"),
            CloseButtonText = Loc.T("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ThemeFor(owner),
        };
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? input.Text.Trim() : null;
    }

    /// <summary>
    /// Password entry for high security mode. Returns null when the user cancels, so the
    /// caller can leave the vault locked instead of treating cancel as an empty passphrase.
    /// </summary>
    public static async Task<string?> PasswordAsync(Window? owner, string title, string prompt,
        string primaryText = "Unlock")
    {
        var root = Root(owner);
        if (root is null) return null;
        var input = new PasswordBox { PlaceholderText = Loc.T(prompt) };
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = Loc.T(title),
            Content = input,
            PrimaryButtonText = Loc.T(primaryText),
            CloseButtonText = Loc.T("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ThemeFor(owner),
        };
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? input.Password : null;
    }

    public static void ShowError(string message)
        => MessageBoxW(OwnerHandle(), Loc.T(message), Loc.T("Ask Key"), 0x00000000 | 0x00000030 | 0x00040000);

    public static void ShowInfo(string message)
        => MessageBoxW(OwnerHandle(), Loc.T(message), Loc.T("Ask Key"), 0x00000000 | 0x00000040 | 0x00040000);

    private static XamlRoot? Root(Window? owner)
        => (owner ?? App.CurrentWindow)?.Content?.XamlRoot;

    private static ElementTheme ThemeFor(Window? owner)
        => (owner ?? App.CurrentWindow)?.Content is FrameworkElement element
            ? element.RequestedTheme
            : ElementTheme.Default;

    private static nint OwnerHandle()
        => App.CurrentWindow is { } window ? WindowHelpers.Handle(window) : 0;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint owner, string text, string caption, uint type);
}
