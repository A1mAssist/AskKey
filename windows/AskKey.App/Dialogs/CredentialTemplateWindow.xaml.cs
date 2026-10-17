using System.Windows;

namespace AskKeyApp;

public partial class CredentialTemplateWindow : Window
{
    internal CredentialTemplate? SelectedTemplate { get; private set; }

    public CredentialTemplateWindow()
    {
        InitializeComponent();
        PresetList.ItemsSource = CredentialTemplates.Presets.Select(ToChoice).ToList();
        CustomList.ItemsSource = new[] { ToChoice(CredentialTemplate.Custom) };
    }

    private static TemplateChoice ToChoice(CredentialTemplate template) => new(
        template,
        Loc.T(CredentialTemplates.PrototypeTitle(template)),
        Loc.T(CredentialTemplates.Description(template)),
        CredentialTemplates.Glyph(template));

    private void Template_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not TemplateChoice choice) return;
        SelectedTemplate = choice.Template;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private sealed record TemplateChoice(
        CredentialTemplate Template,
        string Title,
        string Description,
        string Glyph);
}
