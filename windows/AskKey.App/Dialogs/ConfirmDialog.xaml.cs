using System.Windows;

namespace AskKeyApp;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string reason)
    {
        InitializeComponent();
        ReasonText.Text = reason;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

public sealed class ManagementPrompt
{
    public bool Confirm(string reason)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return false;
        if (dispatcher.CheckAccess())
        {
            return Show(reason);
        }
        return dispatcher.Invoke(() => Show(reason));
    }

    private static bool Show(string reason)
    {
        var dialog = new ConfirmDialog(reason)
        {
            Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive),
        };
        return dialog.ShowDialog() == true;
    }
}
