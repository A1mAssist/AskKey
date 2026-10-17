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
