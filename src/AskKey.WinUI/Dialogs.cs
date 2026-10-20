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

    public static async Task<PassphraseChange?> ChangePassphraseAsync(Window? owner)
    {
        var root = Root(owner);
        if (root is null) return null;
        var current = new PasswordBox { PlaceholderText = Loc.T("Current passphrase") };
        var replacement = new PasswordBox { PlaceholderText = Loc.T("New passphrase") };
        var confirmation = new PasswordBox { PlaceholderText = Loc.T("Confirm new passphrase") };
        var fields = new StackPanel { Spacing = 8 };
        fields.Children.Add(current);
        fields.Children.Add(replacement);
        fields.Children.Add(confirmation);
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = Loc.T("Change passphrase"),
            Content = fields,
            PrimaryButtonText = Loc.T("Save"),
            CloseButtonText = Loc.T("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ThemeFor(owner),
        };
        dialog.Opened += (_, _) => current.Focus(FocusState.Programmatic);
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary
            ? new PassphraseChange(current.Password, replacement.Password, confirmation.Password)
            : null;
    }

    public sealed record PassphraseChange(string Current, string New, string Confirmation);

    private static readonly object NotificationGate = new();
    private static readonly List<(string Text, bool Error)> Notifications = new();
    private static bool _pumping;

    // WinRT and COM failures often carry no Message, which would show an empty box.
    public static void ShowError(string message)
        => Enqueue(string.IsNullOrWhiteSpace(message) ? "Something went wrong." : message, error: true);

    public static void ShowInfo(string message) => Enqueue(message, error: false);

    /// <summary>
    /// Queues a WinUI dialog. The queue keeps one dialog per XamlRoot and lets the many
    /// synchronous call sites keep their signature.
    /// </summary>
    private static void Enqueue(string message, bool error)
    {
        var text = Loc.T(message);
        lock (NotificationGate)
        {
            Notifications.Add((text, error));
            if (_pumping) return;
            _pumping = true;
        }
        if (App.CurrentWindow?.DispatcherQueue.TryEnqueue(PumpNotifications) != true)
        {
            lock (NotificationGate) _pumping = false;
        }
    }

    private static async void PumpNotifications()
    {
        var attempts = 0;
        while (true)
        {
            (string Text, bool Error) item;
            lock (NotificationGate)
            {
                if (Notifications.Count == 0)
                {
                    _pumping = false;
                    return;
                }
                item = Notifications[0];
            }

            var window = App.CurrentWindow;
            var root = window?.Content?.XamlRoot;
            if (window is null || root is null)
            {
                DropFirst();
                ShowNative(item.Text, item.Error);
                continue;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = Loc.T(item.Error ? "Something went wrong" : "Ask Key"),
                Content = new TextBlock { Text = item.Text, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = Loc.T("OK"),
                DefaultButton = ContentDialogButton.Close,
                RequestedTheme = ThemeFor(window),
            };
            try
            {
                await dialog.ShowAsync();
                DropFirst();
                attempts = 0;
            }
            catch (Exception)
            {
                // Another dialog owns this XamlRoot. Retry after it closes; a broken
                // root falls back to the native box so no message is lost.
                if (++attempts < 40)
                {
                    await Task.Delay(250);
                    continue;
                }
                DropFirst();
                ShowNative(item.Text, item.Error);
                attempts = 0;
            }
        }
    }

    private static void DropFirst()
    {
        lock (NotificationGate)
        {
            if (Notifications.Count > 0) Notifications.RemoveAt(0);
        }
    }

    private static void ShowNative(string text, bool error)
        => MessageBoxW(OwnerHandle(), text, Loc.T("Ask Key"),
            0x00000000u | (error ? 0x00000030u : 0x00000040u) | 0x00040000u);

    /// <summary>Waits for the user, so the caller can safely quit afterwards.</summary>
    public static async Task InfoAsync(Window? owner, string title, string message)
    {
        var root = Root(owner);
        if (root is null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = Loc.T(title),
            Content = new TextBlock { Text = Loc.T(message), TextWrapping = TextWrapping.Wrap },
            CloseButtonText = Loc.T("OK"),
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = ThemeFor(owner),
        };
        await dialog.ShowAsync();
    }

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
