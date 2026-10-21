using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using AskKey.Integrations;
using AskKey.Vault;

namespace AskKeyApp;

public partial class SettingsView : UserControl
{
    private const string RepositoryUrl = "https://github.com/A1mAssist/AskKey";
    private sealed record AllowanceOption(int Minutes, string Label);
    private sealed record UnlockDurationOption(int Minutes, string Label);
    private static string EraseConfirmation => EraseLanguage.ConfirmationText();
    private static LocalVaultEraseLanguage EraseLanguage =>
        Loc.Current == AppLanguage.SimplifiedChinese ? LocalVaultEraseLanguage.SimplifiedChinese : LocalVaultEraseLanguage.English;
    private readonly AppHost _host;
    private bool _loading;

    public SettingsView(AppHost host)
    {
        _host = host;
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    public void Refresh()
    {
        _loading = true;
        try
        {
            AboutVersionText.Text = Loc.F("Ask Key for Windows {0}", AskKeyVersion.Current);
            RefreshChoices();
            var preferences = _host.Preferences;
            LaunchToggle.IsChecked = preferences.LaunchAtLogin;
            HotkeyCombo.SelectedItem = HotkeyCombo.Items.OfType<HotkeyOption>()
                .FirstOrDefault(option => option.Id == preferences.HotkeyShortcut);
            AllowanceCombo.SelectedItem = AllowanceCombo.Items.OfType<AllowanceOption>()
                .FirstOrDefault(option => option.Minutes ==
                    (preferences.TimedAllowanceEnabled ? preferences.DefaultTimedAllowanceMinutes : 0))
                ?? AllowanceCombo.Items.OfType<AllowanceOption>().First();
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(AccessRecordsButton, Loc.T("Access records"));
            // The switch shows no text, so its accessible name comes from the row title.
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(LaunchToggle, Loc.T("Launch at login"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ReadAuthToggle,
                Loc.T("Confirm before reading a credential"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(HighSecurityToggle, Loc.T("High security mode"));
            SelectTag(AppearanceCombo, preferences.AppearanceMode);
            SelectTag(LanguageCombo, preferences.LanguageMode);
            ErasePromptText.Text = Loc.F("Type {0} to confirm", EraseConfirmation);
            UnlockDurationCombo.SelectedItem = UnlockDurationCombo.Items.OfType<UnlockDurationOption>()
                .FirstOrDefault(option => option.Minutes * 60 == preferences.SessionTimeoutSeconds)
                ?? UnlockDurationCombo.Items.OfType<UnlockDurationOption>().First();
            ReadAuthToggle.IsChecked = preferences.ReadApprovalAuthenticationEnabled;
            var highSecurity = _host.Vault.IsHighSecurityEnabled;
            HighSecurityToggle.IsChecked = highSecurity;
            HighSecurityText.Text = Loc.T(highSecurity
                ? (_host.Vault.IsGuardUnlockEnabled
                    ? "A passphrase wraps the vault key. Windows Hello unlocks it at startup."
                    : "A passphrase wraps the vault key. Enter it once each launch; sensitive actions use Windows Hello.")
                : "Windows DPAPI wraps the vault key. Any program running as you can unwrap it.");
            ChangePassphraseRow.Visibility = ChangePassphraseDivider.Visibility =
                _host.Vault.IsPassphraseProtected ? Visibility.Visible : Visibility.Collapsed;
            var helloUnlock = _host.Vault.IsGuardUnlockEnabled;
            HelloUnlockToggle.IsChecked = helloUnlock;
            HelloUnlockToggle.IsEnabled = highSecurity;
            HelloUnlockText.Text = Loc.T(highSecurity
                ? (helloUnlock
                    ? "Windows Hello unlocks Ask Key at startup."
                    : "Use Windows Hello at startup. Requires the per-machine Ask Key Guard service.")
                : "Turn on high security mode first.");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                HelloUnlockToggle, Loc.T("Windows Hello unlock"));
            DataFolderText.Text = VaultConfiguration.Default.LocalApplicationDataDirectory;
            EraseConfirmPanel.Visibility = Visibility.Collapsed;
            EraseWordBox.Text = "";
            EraseFailureText.Visibility = Visibility.Collapsed;
            EraseConfirmButton.IsEnabled = false;
            EraseButton.IsEnabled = true;

            BrokerFailurePanel.Visibility = string.IsNullOrWhiteSpace(_host.BrokerFailure) ? Visibility.Collapsed : Visibility.Visible;
            BrokerFailureText.Text = Loc.T(_host.BrokerFailure ?? "");

            AccessRecordFailureText.Visibility = Visibility.Collapsed;
            if (_host.Vault.HasActiveManagementSession && _host.Vault.HasCredentialAccessRecordWriteFailure())
            {
                AccessRecordFailureText.Text = Loc.T("Ask Key could not persist the latest access record. Agent operations continue, but the audit history may be incomplete.");
                AccessRecordFailureText.Visibility = Visibility.Visible;
            }
        }
        catch
        {
        }
        finally
        {
            _loading = false;
        }
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var enabled = LaunchToggle.IsChecked == true;
        _host.Preferences.LaunchAtLogin = enabled;
        _host.Preferences.Save();
        try { StartupRegistration.Apply(enabled); }
        catch (Exception error) { _host.ShowError(error.Message); }
    }

    private void Hotkey_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || HotkeyCombo.SelectedItem is not HotkeyOption option) return;
        _host.ApplyHotkey(option.Id);
    }

    private void Allowance_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AllowanceCombo.SelectedItem is not AllowanceOption option) return;
        var enabled = option.Minutes > 0;
        _host.Preferences.TimedAllowanceEnabled = enabled;
        if (enabled) _host.Preferences.DefaultTimedAllowanceMinutes = option.Minutes;
        _host.Preferences.SaveSecuritySettings(_host, "Manage timed allowances");
    }

    private void UnlockDuration_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || UnlockDurationCombo.SelectedItem is not UnlockDurationOption option) return;
        _host.Preferences.SessionTimeoutSeconds = option.Minutes * 60;
        _host.Preferences.SaveSecuritySettings(_host, "Manage the management session window");
    }

    private void Appearance_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AppearanceCombo.SelectedItem is not ComboBoxItem item) return;
        var mode = item.Tag as string ?? "system";
        _host.ApplyAppearance(mode);
    }

    private void Language_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageCombo.SelectedItem is not ComboBoxItem item) return;
        _host.ApplyLanguage(item.Tag as string ?? AppLanguage.System);
    }

    private void RefreshChoices()
    {
        HotkeyCombo.ItemsSource = HotkeyOption.AllLocalized;
        AllowanceCombo.ItemsSource = new[]
        {
            new AllowanceOption(0, Loc.T("Off")),
            new AllowanceOption(5, Loc.F("{0} minutes", 5)),
            new AllowanceOption(15, Loc.F("{0} minutes", 15)),
            new AllowanceOption(30, Loc.F("{0} minutes", 30)),
            new AllowanceOption(60, Loc.F("{0} minutes", 60)),
            new AllowanceOption(120, Loc.F("{0} minutes", 120)),
            new AllowanceOption(240, Loc.F("{0} minutes", 240)),
        };
        UnlockDurationCombo.ItemsSource = new[]
        {
            new UnlockDurationOption(5, Loc.F("{0} minutes", 5)),
            new UnlockDurationOption(15, Loc.F("{0} minutes", 15)),
            new UnlockDurationOption(30, Loc.F("{0} minutes", 30)),
            new UnlockDurationOption(60, Loc.F("{0} minutes", 60)),
        };
    }

    private static void SelectTag(ComboBox combo, string tag)
    {
        var selected = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
            ?? combo.Items.OfType<ComboBoxItem>().FirstOrDefault();
        combo.SelectedItem = null;
        combo.SelectedItem = selected;
    }

    private async void HighSecurity_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try
        {
            // The toggle already flipped; both flows re-read the real state in Refresh().
            if (_host.Vault.IsHighSecurityEnabled)
            {
                await _host.DisableHighSecurityAsync();
            }
            else
            {
                await _host.EnableHighSecurityAsync();
            }
        }
        catch (Exception error)
        {
            _host.ShowError(error.Message);
        }
        finally
        {
            Refresh();
        }
    }

    private async void ChangePassphrase_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        await _host.ChangePassphraseAsync();
        Refresh();
    }

    private async void HelloUnlock_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try
        {
            if (HelloUnlockToggle.IsChecked == true)
            {
                var result = await _host.EnableGuardUnlockAsync();
                // A canceled Hello prompt is not an error; the toggle simply returns.
                if (result.Error is { } error) _host.ShowError(error);
            }
            else
            {
                await _host.DisableGuardUnlockAsync();
            }
        }
        catch (Exception error)
        {
            _host.ShowError(error.Message);
        }
        finally
        {
            Refresh();
        }
    }

    private async void ReadAuth_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var enabled = ReadAuthToggle.IsChecked == true;
        if (!enabled)
        {
            var warning = Loc.T("Turning this off lets one click release Agent read requests without confirming it is you. Credential changes still require authentication.");
            if (!await Dialogs.ConfirmAsync(App.CurrentWindow, Loc.T("Confirm before reading a credential"),
                    warning, destructive: true))
            {
                _loading = true;
                ReadAuthToggle.IsChecked = true;
                _loading = false;
                return;
            }
            if (!_host.ConfirmSensitiveSetting("Disable system authentication for read approvals"))
            {
                _loading = true;
                ReadAuthToggle.IsChecked = true;
                _loading = false;
                return;
            }
        }
        _host.Preferences.ReadApprovalAuthenticationEnabled = enabled;
        _host.Preferences.SaveSecuritySettings(_host, "Manage read approval authentication");
    }

    private void OpenAccessRecords_Click(object sender, RoutedEventArgs e)
    {
        if (App.CurrentWindow is MainWindow window) window.ShowActivity(ActivitySection.AccessRecords);
    }

    private async void ClearAccess_Click(object sender, RoutedEventArgs e)
    {
        if (!await Dialogs.ConfirmAsync(App.CurrentWindow, Loc.T("Clear access records"),
                Loc.T("Clear all Ask Key access records? This cannot be undone."), destructive: true)) return;
        try
        {
            _host.Vault.ClearCredentialAccessRecords(_host.AuthenticateAndBeginManagement("Clear Ask Key access records"));
            _host.NotifyCredentialsChanged();
        }
        catch (Exception error)
        {
            _host.ShowError(error.Message);
        }
    }

    private void Erase_Click(object sender, RoutedEventArgs e)
    {
        EraseFailureText.Visibility = Visibility.Collapsed;
        EraseConfirmPanel.Visibility = Visibility.Visible;
        EraseWordBox.Text = "";
        EraseButton.IsEnabled = false;
        EraseWordBox.Focus(FocusState.Programmatic);
    }

    private void EraseCancel_Click(object sender, RoutedEventArgs e)
    {
        EraseConfirmPanel.Visibility = Visibility.Collapsed;
        EraseWordBox.Text = "";
        EraseFailureText.Visibility = Visibility.Collapsed;
        EraseButton.IsEnabled = true;
    }

    private void EraseWord_TextChanged(object sender, TextChangedEventArgs e)
        => EraseConfirmButton.IsEnabled = string.Equals(EraseWordBox.Text, EraseConfirmation, StringComparison.Ordinal);

    private async void EraseConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (!string.Equals(EraseWordBox.Text, EraseConfirmation, StringComparison.Ordinal)) return;
        try
        {
            await _host.EraseLocalDataAsync(EraseConfirmation, EraseLanguage);
        }
        catch (Exception error)
        {
            EraseFailureText.Text = Loc.T(error.Message);
            EraseFailureText.Visibility = Visibility.Visible;
        }
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = VaultConfiguration.Default.LocalApplicationDataDirectory;
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception error)
        {
            _host.ShowError(error.Message);
        }
    }

    private void OpenGitHub_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(RepositoryUrl) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            _host.ShowError(error.Message);
        }
    }

    private void RetryBroker_Click(object sender, RoutedEventArgs e)
    {
        _host.RetryBroker();
        Refresh();
    }
}
