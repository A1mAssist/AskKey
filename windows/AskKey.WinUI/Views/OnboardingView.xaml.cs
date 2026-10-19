using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AskKeyApp;

public partial class OnboardingView : UserControl
{
    private readonly AppHost _host;

    public OnboardingView(AppHost host)
    {
        _host = host;
        InitializeComponent();
        UpdateLaunchCopy();
        UpdatePassphraseCopy();
        Loc.LanguageChanged += UpdateLaunchCopy;
        Loc.LanguageChanged += UpdatePassphraseCopy;
    }

    private void UpdateLaunchCopy()
    {
        var text = Loc.T("Launch Ask Key at login. Agents receive no credentials while Ask Key is off.");
        LaunchCheck.OnContent = text;
        LaunchCheck.OffContent = text;
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureHighSecurity()) return;
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
        if (!EnsureHighSecurity()) return;
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

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureHighSecurity()) return;
        Finish();
        _host.ShowAgentAccess();
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureHighSecurity()) return;
        Finish();
        _host.ShowCredentials();
    }

    private void Passphrase_Click(object sender, RoutedEventArgs e) => UpdatePassphraseCopy();

    private void UpdatePassphraseCopy()
    {
        if (_host.Vault.IsHighSecurityEnabled)
        {
            PassphraseToggle.IsChecked = true;
            PassphraseToggle.IsEnabled = false;
            PassphraseFields.Visibility = Visibility.Collapsed;
            PassphraseHint.Text = Loc.T("High security mode is already on.");
            return;
        }
        PassphraseBox.PlaceholderText = Loc.T("Passphrase (at least 8 characters)");
        PassphraseConfirmBox.PlaceholderText = Loc.T("Confirm the passphrase");
        var on = PassphraseToggle.IsChecked == true;
        PassphraseFields.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        PassphraseHint.Text = Loc.T(on
            ? "The vault key is wrapped with your passphrase. Ask Key asks for it every time it starts."
            : "Windows DPAPI wraps the key. Any program running as you can decrypt it.");
    }

    /// <summary>Wraps the key before the first credential is saved; failure keeps the
    /// user on this page instead of silently leaving the weaker store in place.</summary>
    private bool EnsureHighSecurity()
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
            _host.Vault.EnableHighSecurity(passphrase);
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
