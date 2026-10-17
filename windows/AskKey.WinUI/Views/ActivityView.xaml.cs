using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using AskKey.Broker.Approval;
using AskKey.Vault;

namespace AskKeyApp;

public partial class ActivityView : UserControl
{
    private readonly AppHost _host;
    private ActivitySection _section = ActivitySection.Pending;
    private bool _managementAvailable;

    public ActivityView(AppHost host)
    {
        _host = host;
        InitializeComponent();
        _host.PendingChanged += RefreshPending;
        Loaded += (_, _) => Refresh();
    }

    public void Select(ActivitySection section)
    {
        _section = section;
        ShowSection();
        Refresh();
    }

    public void Refresh()
    {
        RefreshPending();
        _managementAvailable = _host.Vault.HasActiveManagementSession;
        if (_managementAvailable)
        {
            RefreshRecycle();
            RefreshRecords();
        }
        LockedPanel.Visibility = !_managementAvailable && _section != ActivitySection.Pending
            ? Visibility.Visible : Visibility.Collapsed;
        if (!_managementAvailable && _section != ActivitySection.Pending)
        {
            PendingPanel.Visibility = Visibility.Collapsed;
            RecyclePanel.Visibility = Visibility.Collapsed;
            RecordsPanel.Visibility = Visibility.Collapsed;
        }
        ShowSection();
    }

    private void RefreshPending()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(RefreshPending);
            return;
        }
        var pending = _host.PendingApprovals();
        PendingList.ItemsSource = pending;
        PendingEmpty.Visibility = pending.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshRecycle()
    {
        try
        {
            var recycled = _host.Vault.ListRecycledTextCredentials();
            RecycleList.ItemsSource = recycled;
            RecycleEmpty.Visibility = recycled.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (VaultException error) when (error.Code == VaultErrorCode.ManagementAuthenticationRequired)
        {
            _managementAvailable = false;
        }
        catch (Exception error)
        {
            _host.ShowError(error.Message);
        }
    }

    private void RefreshRecords()
    {
        try
        {
            var names = _host.Vault.ListTextCredentials()
                .Concat(_host.Vault.ListRecycledTextCredentials())
                .GroupBy(item => item.Id)
                .ToDictionary(group => group.Key, group => group.First().Name);
            var rows = _host.Vault.ListCredentialAccessRecords()
                .OrderByDescending(record => record.Timestamp)
                .Select(record => new AccessRow(
                    record.Timestamp.ToLocalTime().ToString("g", Loc.Culture),
                    Describe(record, names),
                    ResultLabel(record.Result)))
                .ToList();
            RecordsList.ItemsSource = rows;
            RecordsEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (VaultException error) when (error.Code == VaultErrorCode.ManagementAuthenticationRequired)
        {
            _managementAvailable = false;
        }
        catch (Exception error)
        {
            _host.ShowError(error.Message);
        }
    }

    private static string Describe(CredentialAccessEvent record, IReadOnlyDictionary<string, string> names)
    {
        var credential = record.CredentialId is { } id && names.TryGetValue(id, out var name)
            ? $"“{name}”" : Loc.T("a credential");
        var caller = string.IsNullOrWhiteSpace(record.CallerHint) ? Loc.T("Local Agent") : record.CallerHint;
        var action = record.Operation switch
        {
            CredentialAccessOperation.Catalog => Loc.T("listed credentials"),
            CredentialAccessOperation.RuntimeRead => Loc.F("used {0}", credential),
            CredentialAccessOperation.Create => Loc.F("tried to create {0}", credential),
            CredentialAccessOperation.Modify => Loc.F("tried to change {0}", credential),
            _ => Loc.F("tried to delete {0}", credential),
        };
        var executable = record.ExecutableBasename is { } exe ? Loc.F(" with {0}", exe) : "";
        return Loc.F("{0} {1}{2}", caller, action, executable);
    }

    private static string ResultLabel(CredentialAccessResult result) => result switch
    {
        CredentialAccessResult.Allowed => Loc.T("Allowed"),
        CredentialAccessResult.Denied => Loc.T("Denied"),
        CredentialAccessResult.Expired => Loc.T("Expired"),
        CredentialAccessResult.Failed => Loc.T("Failed"),
        _ => Loc.T("Hidden name rejected"),
    };

    private void ShowSection()
    {
        if (!_managementAvailable && _section != ActivitySection.Pending) return;
        PendingPanel.Visibility = _section == ActivitySection.Pending ? Visibility.Visible : Visibility.Collapsed;
        RecyclePanel.Visibility = _section == ActivitySection.RecycleBin ? Visibility.Visible : Visibility.Collapsed;
        RecordsPanel.Visibility = _section == ActivitySection.AccessRecords ? Visibility.Visible : Visibility.Collapsed;
        ClearRecordsButton.Visibility = _section == ActivitySection.AccessRecords ? Visibility.Visible : Visibility.Collapsed;
        SetTab(PendingTab, _section == ActivitySection.Pending);
        SetTab(RecycleTab, _section == ActivitySection.RecycleBin);
        SetTab(RecordsTab, _section == ActivitySection.AccessRecords);
    }

    private static void SetTab(Button button, bool selected)
        => button.Style = (Style)Application.Current.Resources[
            selected ? "SegmentSelectedButtonStyle" : "SegmentButtonStyle"];

    private void PendingTab_Click(object sender, RoutedEventArgs e)
    {
        _section = ActivitySection.Pending;
        Refresh();
    }

    private void RecycleTab_Click(object sender, RoutedEventArgs e)
    {
        _section = ActivitySection.RecycleBin;
        EnsureManagement();
    }

    private void RecordsTab_Click(object sender, RoutedEventArgs e)
    {
        _section = ActivitySection.AccessRecords;
        EnsureManagement();
    }

    private void EnsureManagement()
    {
        if (_managementAvailable || _host.EnsureManagementSession("Unlock Ask Key activity")) _managementAvailable = true;
        Refresh();
    }

    private void Unlock_Click(object sender, RoutedEventArgs e) => EnsureManagement();

    private void ReviewPending_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is BrokerPendingApproval pending) _host.ReviewApproval(pending);
    }

    private void DenyPending_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is BrokerPendingApproval pending)
            _host.Decide(pending, BrokerApprovalDecision.Deny);
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ManagedTextCredential credential) return;
        try
        {
            _host.Vault.RestoreRecycledTextCredential(credential.Id,
                _host.ManagementAuthenticator(CredentialManagementCopy.ManageReason));
            _host.NotifyCredentialsChanged();
        }
        catch (Exception error)
        {
            _host.ShowError(error.Message);
        }
    }

    private async void PermanentDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ManagedTextCredential credential) return;
        if (!await Dialogs.ConfirmAsync(App.CurrentWindow, Loc.T("Delete permanently"),
                Loc.F("Permanently delete “{0}”? This cannot be undone.", credential.Name), destructive: true)) return;
        try
        {
            _host.Vault.PermanentlyDeleteRecycledTextCredential(credential.Id,
                _host.ManagementAuthenticator(CredentialManagementCopy.ManageReason));
            _host.NotifyCredentialsChanged();
        }
        catch (Exception error)
        {
            _host.ShowError(error.Message);
        }
    }

    private async void ClearRecords_Click(object sender, RoutedEventArgs e)
    {
        if (!await Dialogs.ConfirmAsync(App.CurrentWindow, Loc.T("Clear access records"),
                Loc.T("Clear all Ask Key access records? This cannot be undone."), destructive: true)) return;
        try
        {
            _host.Vault.ClearCredentialAccessRecords(
                _host.ManagementAuthenticator("Clear Ask Key access records"));
            Refresh();
        }
        catch (Exception error)
        {
            _host.ShowError(error.Message);
        }
    }

    private sealed record AccessRow(string Time, string Description, string Result);
}
