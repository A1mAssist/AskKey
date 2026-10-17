using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using AskKey.Vault;

namespace AskKeyApp;

public partial class CredentialsView : UserControl
{
    private readonly AppHost _host;
    private List<ManagedTextCredential> _credentials = [];
    private List<CredentialItem> _items = [];
    private ManagedTextCredential? _selected;
    private ManagedTextCredential? _revealed;
    private string? _groupFilter;
    private bool _ungrouped;
    private bool _refreshing;

    public CredentialsView(AppHost host)
    {
        _host = host;
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    public void SetGroupFilter(string? group, bool ungrouped)
    {
        _groupFilter = group;
        _ungrouped = ungrouped;
        ApplyFilter();
    }

    public void Refresh()
    {
        try
        {
            if (!_host.Vault.HasActiveManagementSession)
            {
                ShowLocked();
                return;
            }
            var selectedId = _selected?.Id;
            _credentials = _host.Vault.ListTextCredentials();
            _selected = selectedId is null ? null : _credentials.FirstOrDefault(item => item.Id == selectedId);
            _revealed = null;
            RefreshGroupChoices();
            ApplyFilter();
            ShowLocked(false);
            RenderSelection();
        }
        catch (VaultException error) when (error.Code == VaultErrorCode.ManagementAuthenticationRequired)
        {
            ShowLocked();
        }
        catch (Exception error)
        {
            _host.ShowError(error.Message);
            ShowLocked();
        }
    }

    private void ShowLocked(bool locked = true)
    {
        LockedPanel.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
        if (!locked) return;
        CredentialList.ItemsSource = null;
        DetailScroll.Visibility = Visibility.Collapsed;
        NoSelection.Visibility = Visibility.Visible;
        CountText.Text = "";
    }

    private void Unlock_Click(object sender, RoutedEventArgs e)
    {
        if (!_host.EnsureManagementSession("Unlock Ask Key management")) return;
        _host.NotifyCredentialsChanged();
    }

    private void RefreshGroupChoices()
    {
        _refreshing = true;
        try
        {
            var values = new List<GroupChoice> { new(Loc.T("Ungrouped"), null) };
            values.AddRange(_host.Vault.ListCredentialGroups().Where(group => group.Length > 0)
                .Select(group => new GroupChoice(group, group)));
            GroupCombo.ItemsSource = values;
            GroupCombo.SelectedItem = values.FirstOrDefault(choice =>
                string.Equals(choice.Value, _selected?.GroupName, StringComparison.Ordinal)) ?? values[0];
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim();
        _items = _credentials
            .Where(credential => !_ungrouped || credential.GroupName is null)
            .Where(credential => _groupFilter is null || string.Equals(credential.GroupName, _groupFilter, StringComparison.Ordinal))
            .Where(credential => query.Length == 0
                || credential.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (credential.GroupName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                || (credential.UsageInstructions?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                || credential.PayloadKind.ToString().Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(credential => new CredentialItem(credential))
            .ToList();

        _refreshing = true;
        CredentialList.ItemsSource = _items;
        CredentialList.SelectedItem = _items.FirstOrDefault(item => item.Credential.Id == _selected?.Id);
        _refreshing = false;

        CountText.Text = _items.Count == 1 ? Loc.T("1 credential") : Loc.F("{0} credentials", _items.Count);
        EmptyState.Visibility = _items.Count == 0 && _credentials.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SectionTitle.Text = _ungrouped ? Loc.T("Ungrouped credentials")
            : _groupFilter is not null ? _groupFilter : Loc.T("All credentials");
        SectionSubtitle.Text = _ungrouped ? Loc.T("Credentials without a group.")
            : _groupFilter is not null ? Loc.T("Credentials in this group.")
            : Loc.T("Each credential is delivered and authorized as one complete set.");
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void CredentialList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing) return;
        _selected = (CredentialList.SelectedItem as CredentialItem)?.Credential;
        _revealed = null;
        RenderSelection();
    }

    private void RenderSelection()
    {
        if (_selected is null)
        {
            NoSelection.Visibility = Visibility.Visible;
            DetailScroll.Visibility = Visibility.Collapsed;
            return;
        }
        NoSelection.Visibility = Visibility.Collapsed;
        DetailScroll.Visibility = Visibility.Visible;

        var credential = _selected;
        DetailName.Text = credential.Name;
        DetailSubtitle.Text = $"{KindLabel(credential.PayloadKind)} · {(credential.GroupName ?? Loc.T("Ungrouped"))}";
        EditButton.IsEnabled = true;
        DeleteButton.IsEnabled = true;
        RevealButton.IsEnabled = true;
        CopyButton.IsEnabled = true;
        ComponentList.ItemsSource = Components(credential);
        RevealedValue.Visibility = Visibility.Collapsed;
        RevealedValue.Text = "";

        _refreshing = true;
        try
        {
            GroupCombo.SelectedItem = (GroupCombo.ItemsSource as IEnumerable<GroupChoice>)?.FirstOrDefault(choice =>
                string.Equals(choice.Value, credential.GroupName, StringComparison.Ordinal))
                ?? (GroupCombo.ItemsSource as IEnumerable<GroupChoice>)?.FirstOrDefault();
            PermissionCombo.SelectedIndex = credential.Permission switch
            {
                CredentialPermission.Allowed => 0,
                CredentialPermission.Ask => 1,
                _ => 2,
            };
        }
        finally
        {
            _refreshing = false;
        }

        var instructions = credential.UsageInstructions;
        InstructionsPanel.Visibility = string.IsNullOrWhiteSpace(instructions) ? Visibility.Collapsed : Visibility.Visible;
        InstructionsText.Text = instructions;
        NotesPanel.Visibility = Visibility.Collapsed;
        NotesText.Text = "";
        ExpiryText.Text = credential.ExpiresAt is { } expiry
            ? Loc.F("Expires {0}", expiry.ToLocalTime().ToString("g", Loc.Culture))
            : Loc.T("No expiry");

        var deadline = _host.Vault.ApprovalRequests.TimedAllowanceDeadline(credential.Id);
        if (deadline is { } until && until > DateTimeOffset.UtcNow)
        {
            TimedAllowanceCard.Visibility = Visibility.Visible;
            TimedAllowanceText.Text = Loc.F("Timed Agent access ends {0}.", until.ToLocalTime().ToString("g", Loc.Culture));
        }
        else
        {
            TimedAllowanceCard.Visibility = Visibility.Collapsed;
        }
    }

    private IReadOnlyList<ComponentRow> Components(ManagedTextCredential credential)
    {
        var source = _revealed?.Id == credential.Id ? _revealed : credential;
        var rows = new List<ComponentRow>();
        if (source.PayloadKind == CredentialPayloadKind.Bundle && source.Components.Count > 0)
        {
            foreach (var component in source.Components)
            {
                var display = component.Value switch
                {
                    CredentialComponentValue.Text text => text.Value,
                    CredentialComponentValue.File file => Loc.F("[file] {0} ({1:N0} bytes)", file.Filename, file.Bytes.Length),
                    _ => Loc.T("Hidden"),
                };
                rows.Add(new ComponentRow(component.Name, display, DeliveryText(component.Delivery)));
            }
            return rows;
        }
        if (source.PayloadKind == CredentialPayloadKind.File)
        {
            var filename = source.OriginalFilename ?? Loc.T("Encrypted file");
            var size = source.ByteSize is { } bytes ? $" · {FormatBytes(bytes)}" : "";
            rows.Add(new ComponentRow(Loc.T("FILE"), $"{filename}{size}", Loc.T("Delivered as a temporary file")));
        }
        else
        {
            var value = _revealed?.Id == source.Id ? source.Value : null;
            rows.Add(new ComponentRow(Loc.T("VALUE"), value ?? Loc.T("Hidden"), DeliveryText(
                new CredentialComponentDelivery(CredentialDeliveryKind.EnvironmentVariable,
                    source.EnvironmentVariable))));
        }
        return rows;
    }

    private static string DeliveryText(CredentialComponentDelivery delivery) => delivery.Kind switch
    {
        CredentialDeliveryKind.EnvironmentVariable => Loc.F("Environment variable {0}", delivery.Variable),
        CredentialDeliveryKind.TemporaryFile => Loc.F("Temporary file ({0})", delivery.Variable),
        _ => Loc.T("Kept in Ask Key"),
    };

    private static string KindLabel(CredentialPayloadKind kind) => kind switch
    {
        CredentialPayloadKind.Text => Loc.T("Text credential"),
        CredentialPayloadKind.File => Loc.T("File credential"),
        _ => Loc.T("Bundle credential"),
    };

    private static string FormatBytes(int bytes) => bytes < 1024
        ? $"{bytes} B"
        : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes / (1024.0 * 1024.0):0.#} MB";

    private async void New_Click(object sender, RoutedEventArgs e)
    {
        var chooser = new CredentialTemplateWindow();
        var selected = await chooser.ShowAsync();
        if (selected is not { } template) return;
        var editor = template == CredentialTemplate.Custom
            ? new CredentialEditorWindow(_host, null)
            : new CredentialEditorWindow(_host, template);
        if (await editor.ShowAsync()) _host.NotifyCredentialsChanged();
    }

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var editor = new CredentialEditorWindow(_host, _selected);
        if (await editor.ShowAsync()) _host.NotifyCredentialsChanged();
    }

    private void Import_Click(object sender, RoutedEventArgs e) => _ = ImportFromDialogAsync();

    public async Task ImportFromDialogAsync(bool onboarding = false)
    {
        var path = await FilePicker.PickAsync("Import credential material");
        if (path is null) return;
        try
        {
            var window = new ImportCredentialWindow(_host, path, _groupFilter, _credentials);
            if (await window.ShowAsync()) _host.NotifyCredentialsChanged();
        }
        catch (Exception error)
        {
            _host.ShowError(error.Message);
        }
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        try
        {
            var auth = _host.AuthenticateAndBeginManagement("Reveal a credential");
            _revealed = _host.Vault.RevealTextCredential(_selected.Id, auth);
            RenderSelection();
            if (_revealed.PayloadKind == CredentialPayloadKind.Text && _revealed.Value is { } value)
            {
                RevealedValue.Text = value;
                RevealedValue.Visibility = Visibility.Visible;
            }
            if (_revealed.PayloadKind == CredentialPayloadKind.Bundle)
            {
                NotesPanel.Visibility = string.IsNullOrWhiteSpace(_revealed.PrivateNotes)
                    ? Visibility.Collapsed : Visibility.Visible;
                NotesText.Text = _revealed.PrivateNotes ?? "";
            }
        }
        catch (Exception error)
        {
            _host.ShowError(error.Message);
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        try
        {
            var revealed = _host.Vault.RevealTextCredential(_selected.Id,
                _host.AuthenticateAndBeginManagement("Copy a credential"));
            var value = revealed.PayloadKind switch
            {
                CredentialPayloadKind.Text => revealed.Value,
                CredentialPayloadKind.File => revealed.FileBytes is { } bytes ? Encoding.UTF8.GetString(bytes) : null,
                _ => revealed.Components.Select(component => component.Value switch
                {
                    CredentialComponentValue.Text text => text.Value,
                    CredentialComponentValue.File file => Encoding.UTF8.GetString(file.Bytes),
                    _ => "",
                }).FirstOrDefault(text => text.Length > 0),
            };
            if (string.IsNullOrEmpty(value))
            {
                _host.ShowError(Loc.T("This credential has no text value to copy."));
                return;
            }
            ClipboardSafety.Copy(value);
        }
        catch (Exception error)
        {
            _host.ShowError(error.Message);
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        if (!await Dialogs.ConfirmAsync(App.CurrentWindow, Loc.T("Delete credential"),
                Loc.F("Move “{0}” to the Recycle Bin?", _selected.Name), destructive: true)) return;
        try
        {
            _host.Vault.DeleteTextCredential(_selected.Id,
                _host.ManagementAuthenticator(CredentialManagementCopy.ManageReason));
            _selected = null;
            _host.NotifyCredentialsChanged();
        }
        catch (Exception error)
        {
            _host.ShowError(error.Message);
        }
    }

    private void GroupCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || _selected is null || GroupCombo.SelectedItem is not GroupChoice choice) return;
        if (string.Equals(choice.Value, _selected.GroupName, StringComparison.Ordinal)) return;
        try
        {
            _host.Vault.UpdateCredentialGroup(_selected.Id, choice.Value,
                _host.ManagementAuthenticator(CredentialManagementCopy.ManageReason));
            _host.NotifyCredentialsChanged();
        }
        catch (Exception error)
        {
            _host.ShowError(error.Message);
            RenderSelection();
        }
    }

    private void PermissionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || _selected is null || PermissionCombo.SelectedIndex < 0) return;
        var permission = PermissionCombo.SelectedIndex switch
        {
            0 => CredentialPermission.Allowed,
            1 => CredentialPermission.Ask,
            _ => CredentialPermission.Hidden,
        };
        if (permission == _selected.Permission) return;
        try
        {
            _host.Vault.UpdateCredentialPermission(_selected.Id, permission,
                _host.ManagementAuthenticator(CredentialManagementCopy.ManageReason));
            _host.NotifyCredentialsChanged();
        }
        catch (Exception error)
        {
            _host.ShowError(error.Message);
            RenderSelection();
        }
    }

    private void RevokeAllowance_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        _host.Vault.ApprovalRequests.RevokeTimedAllowance(_selected.Id);
        RenderSelection();
    }

    public sealed class CredentialItem
    {
        public CredentialItem(ManagedTextCredential credential) => Credential = credential;
        public ManagedTextCredential Credential { get; }
        public string Name => Credential.Name;
        public string GroupName => Credential.GroupName ?? Loc.T("Ungrouped");
        public string PayloadKindLabel => KindLabel(Credential.PayloadKind);
        public CredentialPermission Permission => Credential.Permission;
        public string PermissionLabel => Credential.Permission switch
        {
            CredentialPermission.Allowed => Loc.T("Allow"),
            CredentialPermission.Ask => Loc.T("Ask"),
            _ => Loc.T("Hidden"),
        };
        public int ComponentCount => Credential.PayloadKind == CredentialPayloadKind.Bundle
            ? Math.Max(1, Credential.Components.Count) : 1;
    }

    public sealed record ComponentRow(string Name, string DisplayValue, string DeliveryText);
    public sealed record GroupChoice(string Label, string? Value);
}
