using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AskKey.Vault;

namespace AskKeyApp;

public partial class SettingsView : UserControl
{
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
        AllowanceCombo.ItemsSource = new[] { 5, 15, 30, 60, 120, 240 };
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
            AllowanceCombo.SelectedItem = preferences.DefaultTimedAllowanceMinutes;
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
        if (_loading || AllowanceCombo.SelectedItem is not int minutes) return;
        _host.Preferences.DefaultTimedAllowanceMinutes = minutes;
        _host.Preferences.Save();
        _host.Vault.UpdateDefaultTimedAllowanceMinutes(minutes);
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
            if (!_host.EnsureManagementSession("Clear Ask Key access records")) return;
            _host.Vault.ClearCredentialAccessRecords(
                _host.ManagementAuthenticator("Clear Ask Key access records"));
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
