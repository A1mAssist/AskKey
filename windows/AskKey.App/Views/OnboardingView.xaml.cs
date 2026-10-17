using System.Windows;
using System.Windows.Controls;

namespace AskKeyApp;

public partial class OnboardingView : UserControl
{
    private readonly AppHost _host;

    public OnboardingView(AppHost host)
    {
        _host = host;
        InitializeComponent();
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_host.Vault.StoredCredentialCount() == 0)
            {
                _host.Vault.BeginOnboardingCreation();
            }

            var editor = new CredentialEditorWindow(_host, null) { Owner = Window.GetWindow(this) };
            if (editor.ShowDialog() != true) return;
            Finish();
            _host.ShowCredentials();
        }
        catch (Exception error)
        {
            _host.ShowError(error.Message);
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_host.Vault.StoredCredentialCount() == 0)
            {
                _host.Vault.BeginOnboardingCreation();
            }

            if (Window.GetWindow(this) is MainWindow window)
            {
                window.ImportCredentials(onboarding: true);
            }
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
        _host.CompleteOnboarding(LaunchCheck.IsChecked == true);
        _host.NotifyCredentialsChanged();
    }
}
