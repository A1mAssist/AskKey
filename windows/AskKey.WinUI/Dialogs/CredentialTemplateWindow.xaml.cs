using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AskKeyApp;

public partial class CredentialTemplateWindow : Window
{
    private readonly TaskCompletionSource<CredentialTemplate?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal CredentialTemplate? SelectedTemplate { get; private set; }

    public CredentialTemplateWindow()
    {
        InitializeComponent();
        Title = Loc.T("New credential");
        WindowHelpers.Configure(this, 720, 565, 620, 480, showInSwitchers: false);
        ThemeManager.Apply(this);
        PresetList.ItemsSource = CredentialTemplates.Presets.Select(ToChoice).ToList();
        CustomList.ItemsSource = new[] { ToChoice(CredentialTemplate.Custom) };
        Closed += (_, _) => _completion.TrySetResult(SelectedTemplate);
    }

    public Task<CredentialTemplate?> ShowAsync()
        => WindowHelpers.ShowDialogAsync(this, App.CurrentWindow, _completion.Task);

    private static TemplateChoice ToChoice(CredentialTemplate template) => new(
        template,
        Loc.T(CredentialTemplates.PrototypeTitle(template)),
        Loc.T(CredentialTemplates.Description(template)),
        CredentialTemplates.Glyph(template));

    private void Template_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not TemplateChoice choice) return;
        SelectedTemplate = choice.Template;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    public sealed record TemplateChoice(CredentialTemplate Template, string Title, string Description, string Glyph);
}
