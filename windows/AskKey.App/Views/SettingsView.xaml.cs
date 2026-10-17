using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AskKey.Vault;

namespace AskKeyApp;

public partial class SettingsView : UserControl
{
    private sealed record AllowanceOption(int Minutes, string Label);

    private static string EraseConfirmation => EraseLanguage.ConfirmationText();
    private static LocalVaultEraseLanguage EraseLanguage =>
        Loc.Current == AppLanguage.SimplifiedChinese
            ? LocalVaultEraseLanguage.SimplifiedChinese
            : LocalVaultEraseLanguage.English;
    private readonly AppHost _host;
    private bool _loading;

    public SettingsView(AppHost host)
    {
        _host = host;
        InitializeComponent();
        _loading = true;
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
        _loading = false;
        Loaded += (_, _) => Refresh();
    }

    public void Refresh()
    {
        _loading = true;
        try
        {
            var preferences = _host.Preferences;
            LaunchCheck.IsChecked = preferences.LaunchAtLogin;
            HotkeyCombo.SelectedItem = HotkeyCombo.Items.OfType<HotkeyOption>()
                .FirstOrDefault(option => option.Id == preferences.HotkeyShortcut);
            AllowanceCombo.SelectedItem = AllowanceCombo.Items.OfType<AllowanceOption>()
                .FirstOrDefault(option => option.Minutes ==
                    (preferences.TimedAllowanceEnabled ? preferences.DefaultTimedAllowanceMinutes : 0))
                ?? AllowanceCombo.Items.OfType<AllowanceOption>().First();
            SelectTag(AppearanceCombo, preferences.AppearanceMode);
            SelectTag(LanguageCombo, preferences.LanguageMode);
            ErasePromptText.Text = Loc.F("Type {0} to confirm", EraseConfirmation);
            ReadAuthCheck.IsChecked = preferences.ReadApprovalAuthenticationEnabled;
            DataFolderText.Text = VaultConfiguration.Default.LocalApplicationDataDirectory;
            EraseConfirmPanel.Visibility = Visibility.Collapsed;
            EraseWordBox.Text = "";
            EraseFailureText.Visibility = Visibility.Collapsed;
            EraseConfirmButton.IsEnabled = false;
            EraseButton.IsEnabled = true;

            BrokerFailurePanel.Visibility = string.IsNullOrWhiteSpace(_host.BrokerFailure)
                ? Visibility.Collapsed : Visibility.Visible;
            BrokerFailureText.Text = Loc.T(_host.BrokerFailure ?? "");

            AccessRecordFailureText.Visibility = Visibility.Collapsed;
            if (_host.Vault.HasActiveManagementSession
                && _host.Vault.HasCredentialAccessRecordWriteFailure())
            {
                AccessRecordFailureText.Text =
                    Loc.T("Ask Key could not persist the latest access record. Agent operations continue, but the audit history may be incomplete.");
                AccessRecordFailureText.Visibility = Visibility.Visible;
            }
        }
        catch (Exception)
        {
            // Settings remain usable when the vault is locked.
        }
        finally
        {
            _loading = false;
        }
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var enabled = LaunchCheck.IsChecked == true;
        _host.Preferences.LaunchAtLogin = enabled;
        _host.Preferences.Save();
        try
        {
            StartupRegistration.Apply(enabled);
        }
        catch (Exception error)
        {
            _host.ShowError(error.Message);
        }
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
        _host.Preferences.Save();
        if (enabled) _host.Vault.UpdateDefaultTimedAllowanceMinutes(option.Minutes);
    }

    private void Appearance_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AppearanceCombo.SelectedItem is not ComboBoxItem item) return;
        var mode = item.Tag as string ?? "system";
        _host.Preferences.AppearanceMode = mode;
        _host.Preferences.Save();
        AppearanceManager.Apply(mode);
    }

    private void Language_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageCombo.SelectedItem is not ComboBoxItem item) return;
        _host.ApplyLanguage(item.Tag as string ?? AppLanguage.System);
    }

    private static void SelectTag(ComboBox combo, string tag)
    {
        combo.SelectedItem = combo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
            ?? combo.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private void ReadAuth_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var enabled = ReadAuthCheck.IsChecked == true;
        if (!enabled)
        {
            var warning = MessageBox.Show(
                Loc.T("Turning this off lets one click release Agent read requests without confirming it is you. Credential changes still require authentication."),
                Loc.T("Confirm before reading a credential"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (warning != MessageBoxResult.Yes)
            {
                ReadAuthCheck.IsChecked = true;
                return;
            }
            if (!_host.ConfirmManagement("Disable system authentication for read approvals"))
            {
                ReadAuthCheck.IsChecked = true;
                return;
            }
        }
        _host.Preferences.ReadApprovalAuthenticationEnabled = enabled;
        _host.Preferences.Save();
        _host.Vault.ApprovalRequests.SetReadAuthenticationEnabled(enabled);
    }

    private void ClearAccess_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ConfirmDialog(Loc.T("Clear all Ask Key access records? This cannot be undone."))
        {
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var auth = _host.AuthenticateAndBeginManagement("Clear Ask Key access records");
            _host.Vault.ClearCredentialAccessRecords(auth);
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
        EraseWordBox.Focus();
    }

    private void EraseCancel_Click(object sender, RoutedEventArgs e)
    {
        EraseConfirmPanel.Visibility = Visibility.Collapsed;
        EraseWordBox.Text = "";
        EraseFailureText.Visibility = Visibility.Collapsed;
        EraseButton.IsEnabled = true;
    }

    private void EraseWord_TextChanged(object sender, TextChangedEventArgs e)
    {
        EraseConfirmButton.IsEnabled =
            string.Equals(EraseWordBox.Text, EraseConfirmation, StringComparison.Ordinal);
    }

    private void EraseConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (!string.Equals(EraseWordBox.Text, EraseConfirmation, StringComparison.Ordinal)) return;
        try
        {
            _host.EraseLocalData(EraseConfirmation, EraseLanguage);
        }
        catch (Exception error)
        {
            EraseFailureText.Text = error.Message;
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

    private void RetryBroker_Click(object sender, RoutedEventArgs e)
    {
        _host.RetryBroker();
        Refresh();
    }
}
