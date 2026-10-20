using AskKey.Guard;
using AskKey.Vault;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace AskKeyApp;

public partial class OnboardingView : UserControl
{
    private readonly AppHost _host;
    private bool _guardCheckStarted;
    private bool _guardAvailable = true;

    public OnboardingView(AppHost host)
    {
        _host = host;
        InitializeComponent();
        Loaded += OnLoaded;
        UpdateLaunchCopy();
        UpdatePassphraseCopy();
        Loc.LanguageChanged += UpdateLaunchCopy;
        Loc.LanguageChanged += UpdatePassphraseCopy;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_guardCheckStarted) return;
        _guardCheckStarted = true;
        try
        {
            if (!await HelloKeyManager.IsSupportedAsync())
            {
                _guardAvailable = false;
                HelloUnlockCheck.IsChecked = false;
                HelloUnlockCheck.IsEnabled = false;
                return;
            }
            var status = await Task.Run(() => new GuardClient().Status());
            if (!status.Ok)
            {
                _guardAvailable = false;
                HelloUnlockCheck.IsChecked = false;
                HelloUnlockCheck.IsEnabled = false;
                return;
            }
            _guardAvailable = true;
            HelloUnlockCheck.IsChecked = true;
            HelloUnlockCheck.IsEnabled = true;
        }
        catch (Exception)
        {
            _guardAvailable = false;
            HelloUnlockCheck.IsChecked = false;
            HelloUnlockCheck.IsEnabled = false;
        }
    }

    private void UpdateLaunchCopy()
    {
        var text = Loc.T("Launch Ask Key at login. Agents receive no credentials while Ask Key is off.");
        LaunchCheck.OnContent = text;
        LaunchCheck.OffContent = text;
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureHighSecurityAsync()) return;
        try
        {
            if (_host.Vault.StoredCredentialCount() == 0) _host.Vault.BeginOnboardingCreation();
            var editor = new CredentialEditorWindow(_host, null);
            if (!await editor.ShowAsync()) return;
            Finish();
            _host.ShowCredentials();
        }
        catch (Exception error)
        {
            _host.ShowError(error.Message);
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureHighSecurityAsync()) return;
        try
        {
            if (_host.Vault.StoredCredentialCount() == 0) _host.Vault.BeginOnboardingCreation();
            if (App.CurrentWindow is MainWindow window) await window.ImportCredentialsAsync(onboarding: true);
            if (_host.Vault.StoredCredentialCount() > 0)
            {
                Finish();
                _host.ShowCredentials();
            }
        }
        catch (Exception error)
        {
            _host.ShowError(error.Message);
        }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureHighSecurityAsync()) return;
        Finish();
        _host.ShowAgentAccess();
    }

    private async void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureHighSecurityAsync()) return;
        Finish();
        _host.ShowCredentials();
    }

    private void Passphrase_Click(object sender, RoutedEventArgs e) => UpdatePassphraseCopy();

    /// <summary>The text column toggles too; the switch handles its own click.</summary>
    private void PassphraseRow_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (!PassphraseToggle.IsEnabled) return;
        PassphraseToggle.IsChecked = PassphraseToggle.IsChecked != true;
        UpdatePassphraseCopy();
    }

    private void UpdatePassphraseCopy()
    {
        if (_host.Vault.IsHighSecurityEnabled)
        {
            PassphraseToggle.IsChecked = true;
            PassphraseToggle.IsEnabled = false;
            PassphraseFields.Visibility = Visibility.Collapsed;
            HelloUnlockCheck.Visibility = Visibility.Collapsed;
            HelloUnlockHint.Visibility = Visibility.Collapsed;
            HelloUnlockStatus.Visibility = Visibility.Collapsed;
            PassphraseHint.Text = Loc.T("High security mode is already on.");
            return;
        }
        PassphraseBox.PlaceholderText = Loc.T("Passphrase (at least 8 characters)");
        PassphraseConfirmBox.PlaceholderText = Loc.T("Confirm the passphrase");
        var on = PassphraseToggle.IsChecked == true;
        PassphraseFields.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        HelloUnlockCheck.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        HelloUnlockCheck.IsEnabled = on && _guardAvailable;
        HelloUnlockHint.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on) HelloUnlockStatus.Visibility = Visibility.Collapsed;
        PassphraseHint.Text = Loc.T(on
            ? "The passphrase is your recovery. After Windows Hello is on, startup uses Hello instead of the passphrase."
            : "Windows DPAPI wraps the key. Any program running as you can decrypt it.");
    }

    /// <summary>Wraps the key before the first credential is saved; failure keeps the
    /// user on this page instead of silently leaving the weaker store in place.</summary>
    private async Task<bool> EnsureHighSecurityAsync()
    {
        if (PassphraseToggle.IsChecked != true || _host.Vault.IsHighSecurityEnabled) return true;
        var passphrase = PassphraseBox.Password;
        if (passphrase.Length < 8)
        {
            _host.ShowError("Use at least 8 characters.");
            return false;
        }
        if (!string.Equals(passphrase, PassphraseConfirmBox.Password, StringComparison.Ordinal))
        {
            _host.ShowError("The passphrases do not match.");
            return false;
        }
        try
        {
            await Task.Run(() => _host.Vault.EnableHighSecurity(passphrase));
            // The passphrase was just set; do not make the user pass a second
            // Hello gate before the first management window opens.
            _host.Vault.BeginManagementSession(ManagementAuthenticator.Allow);
            UpdatePassphraseCopy();
            if (HelloUnlockCheck.IsChecked == true)
            {
                var hello = await _host.EnableGuardUnlockAsync();
                HelloUnlockStatus.Text = Loc.T(hello.Ok
                    ? "Windows Hello unlock is on."
                    : "Windows Hello unlock could not be enabled. You can retry in Settings.");
                HelloUnlockStatus.Visibility = Visibility.Visible;
            }
            return true;
        }
        catch (Exception error)
        {
            _host.ShowError(error.Message);
            return false;
        }
    }
    private void Finish()
    {
        _host.CompleteOnboarding(LaunchCheck.IsOn);
        _host.NotifyCredentialsChanged();
    }
}
