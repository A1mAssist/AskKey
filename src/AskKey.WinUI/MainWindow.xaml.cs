using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Microsoft.UI.Windowing;
using AskKey.Integrations;
using AskKey.Vault;

namespace AskKeyApp;

public enum WorkspaceSection
{
    Credentials,
    Activity,
    AgentAccess,
    Settings,
}

public enum ActivitySection
{
    Pending,
    RecycleBin,
    AccessRecords,
}

public partial class MainWindow : Window
{
    private const int SwRestore = 9;
    private readonly AppHost _host;
    private readonly CredentialsView _credentialsView;
    private readonly ActivityView _activityView;
    private readonly AgentAccessView _agentAccessView;
    private readonly SettingsView _settingsView;
    private readonly OnboardingView _onboardingView;
    private bool _allowClose;
    private bool _refreshingGroups;
    private bool _initialShow = true;
    private bool _showPassphrase;

    public MainWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        VersionText.Text = $"v{AskKeyVersion.Current} · Windows";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleDragRegion);
        _host.PendingChanged += RefreshChrome;
        _host.AgentAccessChanged += RefreshChrome;
        _host.CredentialsChanged += RefreshChrome;
        _host.BrokerStateChanged += RefreshChrome;
        _host.LockStateChanged += RefreshLock;

        _credentialsView = new CredentialsView(_host);
        _activityView = new ActivityView(_host);
        _agentAccessView = new AgentAccessView(_host);
        _settingsView = new SettingsView(_host);
        _onboardingView = new OnboardingView(_host);

        App.CurrentWindow = this;
        WindowHelpers.Configure(this, 1120, 740, 940, 620);
        ThemeManager.Apply(this);
        AppWindow.Closing += AppWindow_Closing;
        NavList.SelectedIndex = 0;
        RefreshChrome();
        RefreshLock();
        if (!_host.Preferences.HasCompletedOnboarding)
        {
            ContentHost.Content = _onboardingView;
            // First run is a single task: leaving the nav visible let the user navigate
            // to Settings and lose the onboarding page without completing it.
            Sidebar.Visibility = Visibility.Collapsed;
            RootGrid.ColumnDefinitions[0].Width = new GridLength(0);
        }
    }

    private void RestoreChrome()
    {
        if (Sidebar.Visibility == Visibility.Visible) return;
        Sidebar.Visibility = Visibility.Visible;
        RootGrid.ColumnDefinitions[0].Width = new GridLength(222);
    }

    public void ShowFromTray()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Minimized)
            ShowWindow(WindowHelpers.Handle(this), SwRestore);
        AppWindow.Show();
        WindowHelpers.EnableRoundedCorners(this);
        Activate();
        // Activate() is only a request; the foreground lock ignores it when the user is
        // working in another app, which would leave a waiting Agent's prompt unnoticed.
        WindowHelpers.BringToFront(this);
        RefreshLock();
        if (_initialShow)
        {
            _initialShow = false;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => WindowHelpers.Center(this));
        }
    }

    public void ShowActivity(ActivitySection section)
    {
        RestoreChrome();
        NavList.SelectedIndex = 1;
        ContentHost.Content = _activityView;
        _activityView.Select(section);
    }

    public void ShowSettings()
    {
        RestoreChrome();
        NavList.SelectedIndex = 3;
        ContentHost.Content = _settingsView;
        _settingsView.Refresh();
    }

    public void ShowCredentials(string? group = null)
    {
        RestoreChrome();
        NavList.SelectedIndex = 0;
        ContentHost.Content = _credentialsView;
        _credentialsView.Refresh();
        if (group is not null) _credentialsView.SetGroupFilter(group, ungrouped: false);
    }

    public void ImportCredentials(bool onboarding = false) => _ = ImportCredentialsAsync(onboarding);
    public async Task ImportCredentialsAsync(bool onboarding = false) => await _credentialsView.ImportFromDialogAsync(onboarding);

    public void ShowAgentAccess()
    {
        RestoreChrome();
        NavList.SelectedIndex = 2;
        ContentHost.Content = _agentAccessView;
        _agentAccessView.Refresh();
    }

    public void RefreshAll()
    {
        RefreshChrome();
        if (_host.Vault.IsLocked)
        {
            RefreshLock();
            return;
        }
        _credentialsView.Refresh();
        _activityView.Refresh();
        _agentAccessView.Refresh();
        _settingsView.Refresh();
    }

    /// <summary>Shows the blocking lock screen whenever the vault key is unavailable.</summary>
    public void RefreshLock()
    {
        var locked = _host.Vault.IsLocked;
        LockOverlay.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
        // The overlay hides the workspace, but Tab and automation still reach what is
        // underneath. Collapse it so a locked window has exactly one interactive surface.
        ContentHost.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
        var showSidebar = !locked && _host.Preferences.HasCompletedOnboarding;
        Sidebar.Visibility = showSidebar ? Visibility.Visible : Visibility.Collapsed;
        RootGrid.ColumnDefinitions[0].Width = new GridLength(showSidebar ? 222 : 0);
        if (!locked)
        {
            _showPassphrase = false;
            LockError.Visibility = Visibility.Collapsed;
            LockPassphrase.Password = "";
            return;
        }

        var hello = _host.Vault.IsGuardUnlockEnabled;
        var passphrase = _host.Vault.IsPassphraseProtected;
        if (!hello) _showPassphrase = true;
        var showPassphrase = passphrase && (!hello || _showPassphrase);
        ForgotPassphraseButton.Visibility = passphrase ? Visibility.Visible : Visibility.Collapsed;
        HelloButton.Visibility = hello ? Visibility.Visible : Visibility.Collapsed;
        UsePassphraseButton.Visibility = hello && !_showPassphrase
            ? Visibility.Visible
            : Visibility.Collapsed;
        LockPassphrase.Visibility = showPassphrase ? Visibility.Visible : Visibility.Collapsed;
        UnlockButton.Visibility = showPassphrase ? Visibility.Visible : Visibility.Collapsed;
        LockMessage.Text = Loc.T(showPassphrase
            ? "Enter your passphrase to unlock Ask Key."
            : hello
                ? "Use Windows Hello to unlock Ask Key."
                : "Click Unlock to open Ask Key.");
        if (showPassphrase)
            DispatcherQueue.TryEnqueue(() => LockPassphrase.Focus(FocusState.Programmatic));
    }

    public void FocusLock()
        => DispatcherQueue.TryEnqueue(() => LockPassphrase.Focus(FocusState.Programmatic));

    private void UsePassphrase_Click(object sender, RoutedEventArgs e)
    {
        _showPassphrase = true;
        RefreshLock();
    }

    private async void ForgotPassphrase_Click(object sender, RoutedEventArgs e)
    {
        var confirmed = await Dialogs.ConfirmAsync(this,
            "Erase local data and start over",
            "This permanently deletes every credential, key and record on this PC. It cannot be undone.",
            "Erase", destructive: true);
        if (!confirmed) return;
        try
        {
            await _host.ForgotPassphraseAsync();
        }
        catch (Exception error)
        {
            ShowLockError(error.Message);
        }
    }

    private async void Unlock_Click(object sender, RoutedEventArgs e) => await TryUnlockAsync();

    private async void Hello_Click(object sender, RoutedEventArgs e)
    {
        if (LockProgress.Visibility == Visibility.Visible) return;
        SetLockBusy(true);
        try
        {
            if (!await _host.UnlockWithHelloAsync())
            {
                ShowLockError("Windows Hello could not unlock Ask Key.");
                FocusLock();
            }
        }
        finally
        {
            SetLockBusy(false);
        }
    }

    private async void LockPassphrase_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        await TryUnlockAsync();
    }

    private async Task TryUnlockAsync()
    {
        if (LockProgress.Visibility == Visibility.Visible) return;
        SetLockBusy(true);
        try
        {
            var passphrase = _host.Vault.IsPassphraseProtected ? LockPassphrase.Password : null;
            var result = await _host.UnlockWithPassphraseAsync(passphrase);
            if (result.Ok) return;
            ShowLockError(result.Error);
            if (_host.Vault.IsPassphraseProtected)
            {
                LockPassphrase.SelectAll();
                LockPassphrase.Focus(FocusState.Programmatic);
            }
        }
        finally
        {
            SetLockBusy(false);
        }
    }

    private void ShowLockError(string? message)
    {
        LockError.Text = Loc.T(string.IsNullOrWhiteSpace(message)
            ? "Ask Key could not unlock."
            : message);
        LockError.Visibility = Visibility.Visible;
    }

    private void SetLockBusy(bool busy)
    {
        LockProgress.IsActive = busy;
        LockProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UnlockButton.IsEnabled = !busy;
        HelloButton.IsEnabled = !busy;
        LockPassphrase.IsEnabled = !busy;
    }

    public void HotkeyUnavailable()
    {
        if (_host.Preferences.HasCompletedOnboarding)
            _host.ShowError(Loc.T("The global shortcut is already in use. Choose another shortcut in Settings."));
    }

    public void AllowClose() => _allowClose = true;
    public void HideToTray() => AppWindow.Hide();

    private void RefreshChrome()
    {
        Title = Loc.T("Ask Key");
        PendingBadge.Visibility = _host.PendingCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        PendingBadgeText.Text = _host.PendingCount.ToString();
        AgentStatusText.Text = _host.IsAgentAccessPaused ? Loc.T("Agent access paused") : Loc.T("Agent access running");
        AgentStatusDot.Fill = (Brush)Application.Current.Resources[
            _host.IsAgentAccessPaused ? "WarningBrush" : "SuccessBrush"];
        RefreshGroups();
    }

    private void RefreshGroups()
    {
        var selected = (GroupList.SelectedItem as GroupChoice)?.Tag;
        _refreshingGroups = true;
        var items = new List<GroupChoice>
        {
            new(Loc.T("All credentials"), ""),
            new(Loc.T("Ungrouped"), "\u0001"),
        };
        try
        {
            items.AddRange(_host.Vault.ListCredentialGroups().Where(g => g.Length > 0).Select(g => new GroupChoice(g, g)));
        }
        catch
        {
        }
        GroupList.ItemsSource = items;
        GroupList.SelectedItem = items.FirstOrDefault(item => item.Tag == selected) ?? items[0];
        _refreshingGroups = false;
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is not ListViewItem item) return;
        switch (item.Tag as string)
        {
            case "Activity":
                ContentHost.Content = _activityView;
                _activityView.Refresh();
                break;
            case "AgentAccess":
                ContentHost.Content = _agentAccessView;
                _agentAccessView.Refresh();
                break;
            case "Settings":
                ContentHost.Content = _settingsView;
                _settingsView.Refresh();
                break;
            default:
                ContentHost.Content = _credentialsView;
                _credentialsView.Refresh();
                break;
        }
    }

    private void GroupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingGroups || GroupList.SelectedItem is not GroupChoice choice) return;
        if (choice.Tag == "\u0001") _credentialsView.SetGroupFilter(null, ungrouped: true);
        else _credentialsView.SetGroupFilter(choice.Tag.Length == 0 ? null : choice.Tag, ungrouped: false);
        if (NavList.SelectedIndex != 0)
        {
            NavList.SelectedIndex = 0;
            ContentHost.Content = _credentialsView;
        }
    }

    private void GroupList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (GroupList.SelectedItem is not GroupChoice choice || choice.Tag.Length == 0 || choice.Tag == "\u0001") return;
        var flyout = new MenuFlyout();
        var delete = new MenuFlyoutItem { Text = Loc.T("Delete group") };
        delete.Click += async (_, _) =>
        {
            var confirmed = await Dialogs.ConfirmAsync(this, Loc.T("Delete group"),
                Loc.F("Delete the group “{0}”? Its credentials will become ungrouped.", choice.Label), destructive: true);
            if (!confirmed) return;
            try
            {
                _host.Vault.DeleteCredentialGroup(choice.Tag, _host.AuthenticateAndBeginManagement("Manage credential groups"));
                RefreshGroups();
                _host.NotifyCredentialsChanged();
            }
            catch (Exception error) { _host.ShowError(error.Message); }
        };
        flyout.Items.Add(delete);
        flyout.ShowAt(GroupList, e.GetPosition(GroupList));
    }

    private void AgentStatusPill_Click(object sender, PointerRoutedEventArgs e) => _host.ToggleAgentAccess();

    private async void NewGroup_Click(object sender, object e)
    {
        var name = await Dialogs.PromptAsync(this, Loc.T("New group"), Loc.T("Group name"));
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            _host.Vault.CreateCredentialGroup(name.Trim(), _host.AuthenticateAndBeginManagement("Manage credential groups"));
            RefreshGroups();
            _host.NotifyCredentialsChanged();
        }
        catch (Exception error) { _host.ShowError(error.Message); }
    }

    private void Quit_Click(object sender, object e) => _host.Quit();

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        AppWindow.Hide();
    }

    private sealed record GroupChoice(string Label, string Tag)
    {
        public override string ToString() => Label;
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int command);
}
