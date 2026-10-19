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
            if (_managementAvailable) RefreshRecords();
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
            var rows = AccessRecordPresentation.Build(
                _host.Vault.ListCredentialAccessRecords(), names, Loc.Culture);
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

    private async void OpenRecord_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AccessRecordRow row) return;
        var details = new StackPanel();
        AddDetail(details, Loc.T("Time"),
            row.Record.Timestamp.ToLocalTime().ToString("F", Loc.Culture));
        AddDetail(details, Loc.T("Operation"),
            AccessRecordPresentation.OperationLabel(row.Record.Operation));
        AddDetail(details, Loc.T("Result"), row.Result);
        if (row.Record.Operation != CredentialAccessOperation.Catalog)
            AddDetail(details, Loc.T("Credential"), row.Credential);
        AddDetail(details, Loc.T("Requested by"),
            string.IsNullOrWhiteSpace(row.Record.CallerHint) ? Loc.T("Local Agent") : row.Record.CallerHint);
        if (!string.IsNullOrWhiteSpace(row.Record.DeclaredPurpose))
            AddDetail(details, Loc.T("Purpose"), row.Record.DeclaredPurpose);
        if (!string.IsNullOrWhiteSpace(row.Record.ExecutableBasename))
            AddDetail(details, Loc.T("Executable"), row.Record.ExecutableBasename);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.T("Access record details"),
            Content = new ScrollViewer
            {
                Content = details,
                MaxHeight = 460,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
            CloseButtonText = Loc.T("Close"),
            DefaultButton = ContentDialogButton.None,
            MinWidth = 420,
            RequestedTheme = RequestedTheme,
        };
        await dialog.ShowAsync();
    }

    private static void AddDetail(Panel panel, string label, string value)
    {
        var item = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        item.Children.Add(new TextBlock
        {
            Text = label,
            Style = (Style)Application.Current.Resources["CaptionTextStyle"],
        });
        item.Children.Add(new TextBlock
        {
            Text = value,
            Style = (Style)Application.Current.Resources["BodyTextStyle"],
            Margin = new Thickness(0, 3, 0, 0),
            IsTextSelectionEnabled = true,
        });
        panel.Children.Add(item);
    }

    private void ShowSection()
    {
        var locked = !_managementAvailable && _section != ActivitySection.Pending;
        LockedPanel.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
        PendingPanel.Visibility = _section == ActivitySection.Pending && !locked
            ? Visibility.Visible : Visibility.Collapsed;
        RecyclePanel.Visibility = _section == ActivitySection.RecycleBin && !locked
            ? Visibility.Visible : Visibility.Collapsed;
        RecordsPanel.Visibility = _section == ActivitySection.AccessRecords && !locked
            ? Visibility.Visible : Visibility.Collapsed;
        ClearRecordsButton.Visibility = _section == ActivitySection.AccessRecords && !locked
            ? Visibility.Visible : Visibility.Collapsed;
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
        Refresh();
    }

    private void RecordsTab_Click(object sender, RoutedEventArgs e)
    {
        _section = ActivitySection.AccessRecords;
        Refresh();
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
            HandleManagementError(error);
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
            HandleManagementError(error);
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
            HandleManagementError(error);
        }
    }

    private void HandleManagementError(Exception error)
    {
        if (error is VaultException { Code: VaultErrorCode.ManagementAuthenticationRequired })
        {
            _managementAvailable = false;
            Refresh();
            return;
        }
        _host.ShowError(error.Message);
    }
}
