using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using AskKey.Vault;

namespace AskKeyApp;

public partial class CredentialEditorWindow : Window
{
    private readonly AppHost _host;
    private readonly ManagedTextCredential? _existing;
    private readonly ObservableCollection<ComponentDraft> _components = [];
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private FileImport.FrozenFile? _snapshot;
    private ManagedTextCredential? _revealed;
    private bool _secretsLoaded;
    private bool _loading;
    private bool _saved;

    public CredentialEditorWindow(AppHost host, ManagedTextCredential? existing)
    {
        _host = host;
        _existing = existing;
        InitializeComponent();
        WindowHelpers.Configure(this, 700, 780, 620, 560, showInSwitchers: false);
        ThemeManager.Apply(this);
        ComponentList.ItemsSource = _components;
        GroupCombo.PlaceholderText = Loc.T("Ungrouped");
        LoadGroups();
        if (existing is null)
        {
            Title = Loc.T("New credential");
            TypeCombo.SelectedIndex = 0;
            PermissionCombo.SelectedIndex = 0;
            if (Content is FrameworkElement root) root.Loaded += (_, _) => NameBox.Focus(FocusState.Programmatic);
        }
        else
        {
            Title = Loc.T("Edit credential");
            TitleText.Text = Loc.T("Edit credential");
            SubtitleText.Text = Loc.T("Metadata can be changed without revealing the stored contents.");
            SaveButton.Content = Loc.T("Save changes");
            TypeCombo.IsEnabled = false;
            NameBox.Text = existing.Name;
            InstructionsBox.Text = existing.UsageInstructions;
            EnvironmentBox.Text = existing.EnvironmentVariable ?? "";
            FileEnvironmentBox.Text = existing.EnvironmentVariable ?? "";
            ExpiryBox.Text = existing.ExpiresAt?.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
            SelectTag(PermissionCombo, existing.Permission.WireValue());
            SelectTag(TypeCombo, existing.PayloadKind.WireValue());
            GroupCombo.Text = existing.GroupName ?? "";
            ExistingSecretPanel.Visibility = Visibility.Visible;
            ShowContentPanels(false);
        }
        UpdatePanelVisibility();
        Closed += (_, _) => _completion.TrySetResult(_saved);
    }

    internal CredentialEditorWindow(AppHost host, CredentialTemplate template) : this(host, null)
    {
        ApplyTemplate(template);
    }

    public Task<bool> ShowAsync()
        => WindowHelpers.ShowDialogAsync(this, App.CurrentWindow, _completion.Task);

    private void ApplyTemplate(CredentialTemplate template)
    {
        Title = Loc.T(CredentialTemplates.EditorTitle(template));
        TitleText.Text = Title;
        NameBox.Text = Loc.T(CredentialTemplates.PrototypeTitle(template));
        var components = CredentialTemplates.Components(template);
        if (components.Count > 1) SelectTag(TypeCombo, "bundle");
        foreach (var component in components)
            _components.Add(new ComponentDraft(component.Name, component.Kind == CredentialTemplateComponentKind.File, component.IsOptional));
        UpdatePanelVisibility();
    }

    private void LoadGroups()
    {
        try
        {
            GroupCombo.ItemsSource = _host.Vault.ListCredentialGroups();
        }
        catch
        {
            GroupCombo.ItemsSource = Array.Empty<string>();
        }
    }

    private static void SelectTag(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }

    private static string SelectedTag(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
    private string SelectedKind => SelectedTag(TypeCombo);
    private CredentialPermission SelectedPermission => SelectedTag(PermissionCombo) switch
    {
        "allowed" => CredentialPermission.Allowed,
        "hidden" => CredentialPermission.Hidden,
        _ => CredentialPermission.Ask,
    };

    private void Type_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        UpdatePanelVisibility();
    }

    private void UpdatePanelVisibility()
    {
        var showContent = _existing is null || _secretsLoaded;
        TextPanel.Visibility = showContent && SelectedKind == "text" ? Visibility.Visible : Visibility.Collapsed;
        FilePanel.Visibility = showContent && SelectedKind == "file" ? Visibility.Visible : Visibility.Collapsed;
        BundlePanel.Visibility = showContent && SelectedKind == "bundle" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowContentPanels(bool show)
    {
        _loading = true;
        try
        {
            TextPanel.Visibility = show && SelectedKind == "text" ? Visibility.Visible : Visibility.Collapsed;
            FilePanel.Visibility = show && SelectedKind == "file" ? Visibility.Visible : Visibility.Collapsed;
            BundlePanel.Visibility = show && SelectedKind == "bundle" ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _loading = false; }
    }

    private void Authenticate_Click(object sender, RoutedEventArgs e)
    {
        if (_existing is null) return;
        try
        {
            _revealed = _host.Vault.RevealTextCredential(_existing.Id,
                _host.AuthenticateAndBeginManagement(CredentialManagementCopy.RevealReason));
            _secretsLoaded = true;
            ExistingSecretPanel.Visibility = Visibility.Collapsed;
            if (_revealed.PayloadKind == CredentialPayloadKind.Text)
            {
                SetTextValue(_revealed.Value ?? "");
                EnvironmentBox.Text = _revealed.EnvironmentVariable ?? "";
            }
            else if (_revealed.PayloadKind == CredentialPayloadKind.File)
            {
                _snapshot = _revealed.FileBytes is { } bytes
                    ? new FileImport.FrozenFile(_revealed.OriginalFilename ?? "file", bytes) : null;
                UpdateFileLabel();
                FileEnvironmentBox.Text = _revealed.EnvironmentVariable ?? "";
            }
            else
            {
                _components.Clear();
                foreach (var component in _revealed.Components)
                {
                    var draft = new ComponentDraft(component.Name, component.Value is CredentialComponentValue.File)
                    {
                        Mapping = component.Delivery.Variable ?? "",
                    };
                    switch (component.Value)
                    {
                        case CredentialComponentValue.Text text: draft.Value = text.Value; break;
                        case CredentialComponentValue.File file: draft.SetFile(file.Filename, file.Bytes); break;
                    }
                    _components.Add(draft);
                }
            }
            NotesBox.Text = _revealed.PrivateNotes ?? "";
            ShowContentPanels(true);
        }
        catch (Exception error)
        {
            ShowError(error.Message);
        }
    }

    private async void ChooseFile_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePicker.PickAsync("Choose a file to store");
        if (path is null) return;
        try
        {
            _snapshot = FileImport.Freeze(path);
            UpdateFileLabel();
        }
        catch (Exception error) { ShowError(error.Message); }
    }

    private void UpdateFileLabel()
    {
        FileNameText.Text = _snapshot?.OriginalFilename ?? Loc.T("No file selected");
        FileSizeText.Text = _snapshot is null ? "" : Loc.F("{0} · frozen before save", FormatBytes(_snapshot.Bytes.Length));
    }

    private static string FormatBytes(int bytes) => bytes < 1024 ? $"{bytes} B" :
        bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes / (1024.0 * 1024.0):0.#} MB";

    private void ToggleValue_Click(object sender, RoutedEventArgs e)
    {
        if (ValuePlainBox.Visibility == Visibility.Visible)
        {
            ValueBox.Password = ValuePlainBox.Text;
            ValuePlainBox.Visibility = Visibility.Collapsed;
            ValueBox.Visibility = Visibility.Visible;
        }
        else
        {
            ValuePlainBox.Text = ValueBox.Password;
            ValueBox.Visibility = Visibility.Collapsed;
            ValuePlainBox.Visibility = Visibility.Visible;
        }
    }

    private string TextValue => ValuePlainBox.Visibility == Visibility.Visible ? ValuePlainBox.Text : ValueBox.Password;

    private void SetTextValue(string value)
    {
        ValueBox.Password = value;
        ValuePlainBox.Text = value;
        ValueBox.Visibility = Visibility.Visible;
        ValuePlainBox.Visibility = Visibility.Collapsed;
    }

    private void AddTextComponent_Click(object sender, RoutedEventArgs e) => _components.Add(new ComponentDraft());

    private async void AddFileComponent_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePicker.PickAsync("Choose a component file");
        if (path is null) return;
        try
        {
            var snapshot = FileImport.Freeze(path);
            var draft = new ComponentDraft(Path.GetFileNameWithoutExtension(path), true);
            draft.SetFile(snapshot.OriginalFilename, snapshot.Bytes);
            _components.Add(draft);
        }
        catch (Exception error) { ShowError(error.Message); }
    }

    private async void ChooseComponentFile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ComponentDraft draft) return;
        var path = await FilePicker.PickAsync("Choose a component file");
        if (path is null) return;
        try
        {
            var snapshot = FileImport.Freeze(path);
            draft.SetFile(snapshot.OriginalFilename, snapshot.Bytes);
        }
        catch (Exception error) { ShowError(error.Message); }
    }

    private void RemoveComponent_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ComponentDraft draft) _components.Remove(draft);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = NameBox.Text.Trim();
            if (name.Length == 0) throw new InvalidOperationException(Loc.T("Enter a credential name."));
            var expiry = ParseExpiry(ExpiryBox.Text);
            var group = string.IsNullOrWhiteSpace(GroupCombo.Text) ? null : GroupCombo.Text.Trim();
            var auth = _host.Vault.StoredCredentialCount() == 0
                ? ManagementAuthenticator.Allow
                : _host.AuthenticateAndBeginManagement(CredentialManagementCopy.ManageReason);

            if (_existing is not null && !_secretsLoaded)
            {
                _host.Vault.UpdateCredentialMetadata(_existing.Id, name, InstructionsBox.Text, group,
                    SelectedPermission, expiry, auth);
                _saved = true;
                Close();
                return;
            }

            switch (SelectedKind)
            {
                case "file":
                    if (_snapshot is null) throw new InvalidOperationException(Loc.T("Choose a file to store."));
                    var fileInput = new FileCredentialInput
                    {
                        Name = name,
                        Snapshot = _snapshot,
                        UsageInstructions = InstructionsBox.Text,
                        PrivateNotes = NotesBox.Text,
                        GroupName = group,
                        EnvironmentVariable = NullIfEmpty(FileEnvironmentBox.Text),
                        Permission = SelectedPermission,
                        ExpiresAt = expiry,
                    };
                    if (_existing is null) _host.Vault.CreateFileCredential(fileInput, auth);
                    else _host.Vault.UpdateFileCredential(_existing.Id, fileInput, auth);
                    break;
                case "bundle":
                    var components = _components.Select(draft => draft.ToInput()).OfType<CredentialComponentInput>().ToList();
                    if (components.Count == 0) throw new InvalidOperationException(Loc.T("Add at least one component."));
                    var bundleInput = new BundleCredentialInput
                    {
                        Name = name,
                        Components = components,
                        UsageInstructions = InstructionsBox.Text,
                        PrivateNotes = NotesBox.Text,
                        GroupName = group,
                        Permission = SelectedPermission,
                        ExpiresAt = expiry,
                    };
                    if (_existing is null) _host.Vault.CreateBundleCredential(bundleInput, auth);
                    else _host.Vault.UpdateBundleCredential(_existing.Id, bundleInput, auth);
                    break;
                default:
                    if (TextValue.Length == 0) throw new InvalidOperationException(Loc.T("Enter a value."));
                    var textInput = new TextCredentialInput
                    {
                        Name = name,
                        Value = TextValue,
                        UsageInstructions = InstructionsBox.Text,
                        PrivateNotes = NotesBox.Text,
                        GroupName = group,
                        EnvironmentVariable = NullIfEmpty(EnvironmentBox.Text),
                        Permission = SelectedPermission,
                        ExpiresAt = expiry,
                    };
                    if (_existing is null) _host.Vault.CreateTextCredential(textInput, auth);
                    else _host.Vault.UpdateTextCredential(_existing.Id, textInput, auth);
                    break;
            }
            _saved = true;
            Close();
        }
        catch (Exception error)
        {
            ShowError(error.Message);
        }
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTimeOffset? ParseExpiry(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!DateTime.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var date))
            throw new InvalidOperationException(Loc.T("Expiry must use YYYY-MM-DD."));
        return new DateTimeOffset(date.Date.AddDays(1).AddTicks(-1), TimeZoneInfo.Local.GetUtcOffset(date));
    }

    private void ShowError(string message)
    {
        ErrorText.Text = Loc.T(message);
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    public sealed class ComponentDraft : INotifyPropertyChanged
    {
        private string _name;
        private readonly bool _isFile;
        private readonly bool _isOptional;
        private string _value = "";
        private string _mapping = "";
        private string? _fileName;
        private byte[]? _bytes;

        public ComponentDraft(string name = "", bool isFile = false, bool isOptional = false)
        {
            _name = name;
            _isFile = isFile;
            _isOptional = isOptional;
        }

        public string Name { get => _name; set { _name = value; OnPropertyChanged(); } }
        public string Value { get => _value; set { _value = value; OnPropertyChanged(); } }
        public string Mapping { get => _mapping; set { _mapping = value; OnPropertyChanged(); } }
        public bool HasValue => _isFile ? _bytes is not null : !string.IsNullOrWhiteSpace(Value);
        public Visibility TextVisibility => _isFile ? Visibility.Collapsed : Visibility.Visible;
        public Visibility FileVisibility => _isFile ? Visibility.Visible : Visibility.Collapsed;
        public string FileDescription => _bytes is null ? Loc.T("No file selected") : Loc.F("{0} · {1}", _fileName, FormatBytes(_bytes.Length));

        public void SetFile(string filename, byte[] bytes)
        {
            _fileName = filename;
            _bytes = bytes;
            if (string.IsNullOrWhiteSpace(_name)) _name = Path.GetFileNameWithoutExtension(filename);
            OnPropertyChanged(nameof(TextVisibility));
            OnPropertyChanged(nameof(FileVisibility));
            OnPropertyChanged(nameof(FileDescription));
            OnPropertyChanged(nameof(Name));
        }

        public CredentialComponentInput? ToInput()
        {
            if (string.IsNullOrWhiteSpace(Name)) throw new InvalidOperationException(Loc.T("Every component needs a name."));
            if (_isOptional && !HasValue) return null;
            if (_isFile && _bytes is null) throw new InvalidOperationException(Loc.F("Choose a file for \"{0}\".", Name.Trim()));
            CredentialComponentValue value = _isFile
                ? new CredentialComponentValue.File(_fileName ?? "file", _bytes!)
                : new CredentialComponentValue.Text(Value);
            var mapping = string.IsNullOrWhiteSpace(Mapping) ? null : Mapping.Trim();
            CredentialComponentDelivery delivery = mapping is null ? CredentialComponentDelivery.None
                : _isFile ? CredentialComponentDelivery.TemporaryFile(mapping)
                : CredentialComponentDelivery.EnvironmentVariable(mapping);
            return new CredentialComponentInput(Name.Trim(), value, delivery);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
