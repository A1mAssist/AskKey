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
        Loc.LanguageChanged += UpdateLaunchCopy;
    }

    private void UpdateLaunchCopy()
    {
        var text = Loc.T("Launch Ask Key at login. Agents receive no credentials while Ask Key is off.");
        LaunchCheck.OnContent = text;
        LaunchCheck.OffContent = text;
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
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
        Finish();
        _host.ShowAgentAccess();
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        Finish();
        _host.ShowCredentials();
    }

    private void Finish()
    {
        _host.CompleteOnboarding(LaunchCheck.IsOn);
        _host.NotifyCredentialsChanged();
    }
}
