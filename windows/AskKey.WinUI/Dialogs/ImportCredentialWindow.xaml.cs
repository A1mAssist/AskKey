using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using AskKey.Vault;

namespace AskKeyApp;

public partial class ImportCredentialWindow : Window
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly AppHost _host;
    private readonly string _sourcePath;
    private readonly string? _group;
    private readonly IReadOnlyList<ManagedTextCredential> _existing;
    private readonly ObservableCollection<ImportRow> _rows = [];
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ManagedTextCredential? _conflict;
    private CredentialPermission _desiredPermission = CredentialPermission.Ask;
    private bool _loading = true;
    private bool _sourceLoaded;
    private bool _saved;

    public ImportCredentialWindow(AppHost host, string sourcePath, string? group, IReadOnlyList<ManagedTextCredential> existing)
    {
        _host = host;
        _sourcePath = sourcePath;
        _group = group;
        _existing = existing;
        InitializeComponent();
        WindowHelpers.Configure(this, 780, 800, 680, 560, showInSwitchers: false);
        ThemeManager.Apply(this);
        RowList.ItemsSource = _rows;
        try
        {
            LoadSource();
            _sourceLoaded = true;
        }
        catch (Exception error)
        {
            ShowError(DescribeImportError(error));
        }
        finally { _loading = false; }
        UpdateConflict();
        if (Content is FrameworkElement root) root.Loaded += (_, _) => NameBox.Focus(FocusState.Programmatic);
        Closed += (_, _) => _completion.TrySetResult(_saved);
    }

    public Task<bool> ShowAsync()
        => WindowHelpers.ShowDialogAsync(this, App.CurrentWindow, _completion.Task);

    private void LoadSource()
    {
        var extension = Path.GetExtension(_sourcePath);
        if (string.Equals(extension, ".env", StringComparison.OrdinalIgnoreCase))
        {
            var file = FileImport.Freeze(_sourcePath);
            string text;
            try { text = StrictUtf8.GetString(file.Bytes); }
            catch (DecoderFallbackException) { throw new InvalidOperationException("The .env file must contain valid UTF-8 text."); }
            var pairs = EnvFileFormat.ParseValidated(text);
            if (pairs.Count == 0) throw new InvalidOperationException("The .env file contains no key-value pairs.");
            foreach (var pair in pairs) _rows.Add(ImportRow.Text(pair.Name, pair.Value, pair.Name));
            SummaryText.Text = Loc.F("{0} · items read: {1}. Blank lines skipped: {2}. The original file is not modified.",
                file.OriginalFilename, pairs.Count, BlankLineCount(text));
            ContentsHeader.Text = Loc.T("Contents · delivered as environment variables by key name");
            SetDefaultName(DefaultEnvironmentName(_sourcePath));
            return;
        }

        var frozen = FileImport.Freeze(_sourcePath);
        _rows.Add(ImportRow.File(frozen));
        SummaryText.Text = Loc.F("{0} · {1} · 1 file. The original remains unchanged.", frozen.OriginalFilename, FormatBytes(frozen.ByteSize));
        ContentsHeader.Text = Loc.T("Contents");
        SetDefaultName(frozen.OriginalFilename);
    }

    private void SetDefaultName(string name)
    {
        NameBox.Text = name;
        PermissionCombo.SelectedIndex = 0;
    }

    private static string DefaultEnvironmentName(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        if (!string.IsNullOrEmpty(stem) && !stem.StartsWith('.')) return stem;
        var folder = Directory.GetParent(path)?.Name;
        return string.IsNullOrEmpty(folder) ? Path.GetFileName(path) : folder;
    }

    private static int BlankLineCount(string source)
    {
        var lines = source.Replace("\r\n", "\n").Split('\n');
        if (lines.Length > 0 && lines[^1].Length == 0) lines = lines[..^1];
        return lines.Count(string.IsNullOrWhiteSpace);
    }

    private void Name_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        UpdateConflict();
    }

    private void Permission_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _desiredPermission = SelectedPermission;
    }

    private CredentialPermission SelectedPermission => ((PermissionCombo.SelectedItem as ComboBoxItem)?.Tag as string) switch
    {
        "allowed" => CredentialPermission.Allowed,
        "hidden" => CredentialPermission.Hidden,
        _ => CredentialPermission.Ask,
    };

    private void UpdateConflict()
    {
        var normalized = NormalizeName(NameBox.Text);
        var previousId = _conflict?.Id;
        _conflict = normalized is null ? null : _existing.FirstOrDefault(credential => NormalizeName(credential.Name) == normalized);
        var hasConflict = _conflict is not null;
        ConflictPanel.Visibility = hasConflict ? Visibility.Visible : Visibility.Collapsed;
        PermissionPanel.IsHitTestVisible = !hasConflict;
        PermissionPanel.Opacity = hasConflict ? 0.5 : 1;
        PermissionNote.Visibility = hasConflict ? Visibility.Visible : Visibility.Collapsed;
        if (hasConflict)
        {
            ConflictText.Text = Loc.F("A credential named “{0}” already exists. Choose for the whole credential: skip it, or authenticate to replace it with the imported content.", _conflict!.Name);
            SkipChoice.Content = Loc.T("Skip");
            ReplaceChoice.Content = Loc.T("Authenticate and Replace");
            PermissionNote.Text = Loc.T("Replacing keeps this credential's current permission. You can change it on the credential page.");
            if (previousId != _conflict.Id) SkipChoice.IsChecked = true;
            SelectPermission(_conflict.Permission);
        }
        else
        {
            SelectPermission(_desiredPermission);
        }
        UpdateSaveLabel();
    }

    private void SelectPermission(CredentialPermission permission)
    {
        _loading = true;
        try
        {
            PermissionCombo.SelectedIndex = permission switch
            {
                CredentialPermission.Allowed => 1,
                CredentialPermission.Hidden => 2,
                _ => 0,
            };
        }
        finally { _loading = false; }
    }

    private static string? NormalizeName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try { return CredentialName.Normalized(CredentialName.DisplayName(raw)); }
        catch (VaultException) { return null; }
    }

    private void ConflictChoice_Changed(object sender, RoutedEventArgs e)
    {
        if (SaveButton is null) return;
        UpdateSaveLabel();
    }

    private void UpdateSaveLabel()
    {
        if (SaveButton is null) return;
        SaveButton.IsEnabled = _sourceLoaded && _rows.Count > 0 && !string.IsNullOrWhiteSpace(NameBox.Text);
        SaveButton.Content = Loc.T(_conflict is null ? "Import as 1 credential"
            : ReplaceChoice.IsChecked == true ? "Confirm Import" : "Skip and Finish");
    }

    private void RevealRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ImportRow row) row.Revealed = !row.Revealed;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_sourceLoaded || _rows.Count == 0) return;
            if (_conflict is not null && ReplaceChoice.IsChecked != true)
            {
                _saved = false;
                Close();
                return;
            }
            var name = NameBox.Text.Trim();
            if (name.Length == 0) throw new InvalidOperationException(Loc.T("Enter a credential name."));
            var components = _rows.Select(row => row.ToInput()).ToList();
            var auth = _host.Vault.StoredCredentialCount() == 0
                ? ManagementAuthenticator.Allow
                : _host.AuthenticateAndBeginManagement("Import a credential");
            if (_conflict is not null) _host.Vault.ReplaceImportedBundleCredential(_conflict.Id, components, auth);
            else
            {
                _host.Vault.CreateBundleCredential(new BundleCredentialInput
                {
                    Name = name,
                    Components = components,
                    GroupName = _group,
                    UsageInstructions = Loc.F("Imported from {0}.", Path.GetFileName(_sourcePath)),
                    Permission = SelectedPermission,
                }, auth);
            }
            _saved = true;
            Close();
        }
        catch (Exception error)
        {
            ShowError(DescribeImportError(error));
        }
    }

    private static string DescribeImportError(Exception error)
    {
        if (error is EnvFileFormatException env) return Loc.F("Line {0}: {1}", env.Line, Loc.T(env.LocalizationKey));
        return Loc.T(error.Message);
    }

    private void ShowError(string message)
    {
        ErrorText.Text = Loc.T(message);
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private static string FormatBytes(int bytes) => bytes < 1024 ? $"{bytes} B" :
        bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes / (1024.0 * 1024.0):0.#} MB";

    public sealed class ImportRow : INotifyPropertyChanged
    {
        private string _name;
        private string _mapping;
        private readonly string _textValue;
        private readonly FileImport.FrozenFile? _file;
        private bool _revealed;

        private ImportRow(string name, string mapping, string textValue, FileImport.FrozenFile? file)
        {
            _name = name;
            _mapping = mapping;
            _textValue = textValue;
            _file = file;
        }

        public static ImportRow Text(string name, string value, string mapping) => new(name, mapping, value, null);
        public static ImportRow File(FileImport.FrozenFile file) => new("FILE", "FILE", "", file);

        public string Name { get => _name; set { _name = value; OnPropertyChanged(); } }
        public string Mapping { get => _mapping; set { _mapping = value; OnPropertyChanged(); } }
        public bool Revealed
        {
            get => _revealed;
            set
            {
                _revealed = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ValueDisplay));
                OnPropertyChanged(nameof(RevealLabel));
            }
        }
        public bool CanReveal => _file is null;
        public Visibility RevealVisibility => CanReveal ? Visibility.Visible : Visibility.Collapsed;
        public string RevealLabel => Loc.T(_revealed ? "Hide" : "Show");
        public string MappingLabel => Loc.T(_file is null ? "Environment variable" : "Temporary file variable");
        public string ValueDisplay => _file is null
            ? _revealed ? _textValue : "••••••••••••"
            : Loc.F("{0} · {1}", _file.OriginalFilename, FormatBytes(_file.ByteSize));

        public CredentialComponentInput ToInput()
        {
            if (string.IsNullOrWhiteSpace(Name)) throw new InvalidOperationException(Loc.T("Every component needs a name."));
            var mapping = string.IsNullOrWhiteSpace(Mapping) ? null : Mapping.Trim();
            CredentialComponentValue value = _file is null
                ? new CredentialComponentValue.Text(_textValue)
                : new CredentialComponentValue.File(_file.OriginalFilename, _file.Bytes);
            var delivery = mapping is null ? CredentialComponentDelivery.None
                : _file is null ? CredentialComponentDelivery.EnvironmentVariable(mapping)
                : CredentialComponentDelivery.TemporaryFile(mapping);
            return new CredentialComponentInput(Name.Trim(), value, delivery);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
