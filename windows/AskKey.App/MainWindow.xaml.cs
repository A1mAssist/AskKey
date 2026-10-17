using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    private readonly AppHost _host;
    private readonly CredentialsView _credentialsView;
    private readonly ActivityView _activityView;
    private readonly AgentAccessView _agentAccessView;
    private readonly SettingsView _settingsView;
    private readonly OnboardingView _onboardingView;
    private bool _allowClose;
    private bool _refreshingGroups;

    public MainWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        _credentialsView = new CredentialsView(_host);
        _activityView = new ActivityView(_host);
        _agentAccessView = new AgentAccessView(_host);
        _settingsView = new SettingsView(_host);
        _onboardingView = new OnboardingView(_host);
        _host.PendingChanged += RefreshChrome;
        _host.AgentAccessChanged += RefreshChrome;
        _host.CredentialsChanged += RefreshChrome;
        _host.BrokerStateChanged += RefreshChrome;
        NavList.SelectedIndex = 0;
        RefreshChrome();
        if (!_host.Preferences.HasCompletedOnboarding)
        {
            ContentHost.Content = _onboardingView;
        }
    }

    public void ShowActivity(ActivitySection section)
    {
        NavList.SelectedIndex = 1;
        ContentHost.Content = _activityView;
        _activityView.Select(section);
    }

    public void ShowSettings()
    {
        NavList.SelectedIndex = 3;
        ContentHost.Content = _settingsView;
        _settingsView.Refresh();
    }

    public void ShowCredentials(string? group = null)
    {
        NavList.SelectedIndex = 0;
        ContentHost.Content = _credentialsView;
        if (group is not null) _credentialsView.SetGroupFilter(group, ungrouped: false);
    }

    public void ImportCredentials(bool onboarding = false)
        => _credentialsView.ImportFromDialog(onboarding);

    public void ShowAgentAccess()
    {
        NavList.SelectedIndex = 2;
        ContentHost.Content = _agentAccessView;
        _agentAccessView.Refresh();
    }

    public void RefreshAll()
    {
        RefreshChrome();
        _credentialsView.Refresh();
        _activityView.Refresh();
        _agentAccessView.Refresh();
        _settingsView.Refresh();
    }

    public void HotkeyUnavailable()
    {
        // The app remains fully usable from the tray; report once without blocking startup.
        if (_host.Preferences.HasCompletedOnboarding)
        {
            _host.ShowError(Loc.T("The global shortcut is already in use. Choose another shortcut in Settings."));
        }
    }

    public void AllowClose() => _allowClose = true;

    private void RefreshChrome()
    {
        PendingBadge.Visibility = _host.PendingCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        PendingBadgeText.Text = _host.PendingCount.ToString();
        AgentStatusText.Text = _host.IsAgentAccessPaused ? Loc.T("Agent access paused") : Loc.T("Agent access running");
        AgentStatusDot.Fill = (Brush)FindResource(_host.IsAgentAccessPaused ? "Warning" : "Success");
        RefreshGroups();
    }

    private void RefreshGroups()
    {
        var selected = (GroupList.SelectedItem as ListBoxItem)?.Tag as string;
        _refreshingGroups = true;
        GroupList.Items.Clear();
        GroupList.Items.Add(GroupItem(Loc.T("All credentials"), ""));
        GroupList.Items.Add(GroupItem(Loc.T("Ungrouped"), "\u0001"));
        IReadOnlyList<string> groups;
        try
        {
            groups = _host.Vault.ListCredentialGroups();
        }
        catch (Exception)
        {
            groups = Array.Empty<string>();
        }
        foreach (var group in groups)
        {
            GroupList.Items.Add(GroupItem(group, group));
        }
        var match = GroupList.Items.OfType<ListBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, selected, StringComparison.Ordinal));
        GroupList.SelectedItem = match ?? GroupList.Items[0];
        _refreshingGroups = false;
    }

    private ListBoxItem GroupItem(string label, string tag)
    {
        var item = new ListBoxItem
        {
            Content = new TextBlock { Text = label, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis },
            Tag = tag,
            Style = (Style)Application.Current.FindResource("NavItem"),
        };
        if (tag.Length > 0 && tag != "\u0001")
        {
            var delete = new MenuItem { Header = Loc.T("Delete group") };
            delete.Click += (_, _) => DeleteGroup(tag);
            item.ContextMenu = new ContextMenu { Items = { delete } };
        }
        return item;
    }

    private void DeleteGroup(string group)
    {
        var confirm = MessageBox.Show(
            Loc.F("Delete the group “{0}”? Its credentials will become ungrouped.", group),
            Loc.T("Delete group"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        try
        {
            var auth = _host.AuthenticateAndBeginManagement("Manage credential groups");
            _host.Vault.DeleteCredentialGroup(group, auth);
            RefreshGroups();
            _host.NotifyCredentialsChanged();
        }
        catch (Exception error)
        {
            _host.ShowError(error.Message);
        }
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is not ListBoxItem item) return;
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
        if (_refreshingGroups || GroupList.SelectedItem is not ListBoxItem item) return;
        var tag = item.Tag as string ?? "";
        if (tag == "\u0001")
        {
            _credentialsView.SetGroupFilter(null, ungrouped: true);
        }
        else
        {
            _credentialsView.SetGroupFilter(tag.Length == 0 ? null : tag, ungrouped: false);
        }
        if (NavList.SelectedIndex != 0)
        {
            NavList.SelectedIndex = 0;
            ContentHost.Content = _credentialsView;
        }
    }

    private void AgentStatusPill_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => _host.ToggleAgentAccess();

    private void NewGroup_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptDialog.Ask(this, Loc.T("New group"), Loc.T("Group name"));
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var auth = _host.AuthenticateAndBeginManagement("Manage credential groups");
            _host.Vault.CreateCredentialGroup(name.Trim(), auth);
            RefreshGroups();
            _host.NotifyCredentialsChanged();
        }
        catch (Exception error)
        {
            _host.ShowError(error.Message);
        }
    }

    private void Quit_Click(object sender, RoutedEventArgs e) => _host.Quit();

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        Hide();
    }
}
