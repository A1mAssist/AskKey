using System.Windows;

namespace AskKeyApp;

public partial class PromptDialog : Window
{
    public PromptDialog(string title, string prompt)
    {
        InitializeComponent();
        TitleText.Text = title;
        PromptText.Text = prompt;
        Loaded += (_, _) => InputBox.Focus();
    }

    public string Value => InputBox.Text.Trim();

    public static string? Ask(Window owner, string title, string prompt)
    {
        var dialog = new PromptDialog(title, prompt) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
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
