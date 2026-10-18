namespace AskKeyApp;

public sealed class TrayHost : IDisposable
{
    private readonly NativeTrayIcon _icon;
    private readonly Action _showMain;
    private readonly Action _showPending;
    private readonly Action _showSettings;
    private readonly Action _toggleAgentAccess;
    private readonly Action _quitAction;
    private TrayMenuWindow? _menuWindow;
    private int _pendingCount;
    private bool _paused;
    private Action? _notificationClick;

    public TrayHost(IntPtr windowHandle, Action showMain, Action showPending, Action showSettings,
        Action toggleAgentAccess, Action quit)
    {
        _showMain = showMain;
        _showPending = showPending;
        _showSettings = showSettings;
        _toggleAgentAccess = toggleAgentAccess;
        _quitAction = quit;

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AskKey.ico");
        _icon = new NativeTrayIcon(windowHandle, Loc.T("Ask Key"), iconPath);
        _icon.ShowRequested += _showMain;
        _icon.MenuRequested += ShowMenuAt;
        _icon.NotificationClicked += () =>
        {
            var action = _notificationClick;
            _notificationClick = null;
            (action ?? _showPending)();
        };
        ApplyLanguage();
    }

    public void UpdatePendingCount(int count)
    {
        _pendingCount = count;
        _menuWindow?.UpdatePendingCount(count);
        UpdateIconText();
    }

    public void UpdatePaused(bool paused)
    {
        _paused = paused;
        _menuWindow?.UpdatePaused(paused);
        UpdateIconText();
    }

    public void ApplyTheme(bool dark) => _menuWindow?.ApplyTheme(dark);

    public void ApplyLanguage()
    {
        _menuWindow?.ApplyLanguage();
        UpdatePendingCount(_pendingCount);
        UpdatePaused(_paused);
    }

    public void ShowNotification(string title, string text, Action? onClick = null)
    {
        _notificationClick = onClick;
        _icon.ShowBalloon(title, text);
    }

    private void ShowMenuAt(int x, int y)
    {
        try
        {
            _menuWindow ??= CreateMenuWindow();
            _menuWindow.ShowAt(x, y);
        }
        catch (Exception error)
        {
            _menuWindow = null;
            AppHost.Current.ShowError(error.Message);
        }
    }

    private TrayMenuWindow CreateMenuWindow()
    {
        var window = new TrayMenuWindow();
        window.ShowRequested += _showMain;
        window.PendingRequested += _showPending;
        window.TogglePausedRequested += _toggleAgentAccess;
        window.SettingsRequested += _showSettings;
        window.ExitRequested += _quitAction;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_menuWindow, window)) _menuWindow = null;
        };
        window.ApplyTheme(ThemeManager.IsDark);
        window.UpdatePendingCount(_pendingCount);
        window.UpdatePaused(_paused);
        window.ApplyLanguage();
        return window;
    }

    private void UpdateIconText()
    {
        var status = _pendingCount > 0 ? Loc.F(" · {0} waiting", _pendingCount) : "";
        if (_paused) status += Loc.T(" · paused");
        _icon.UpdateToolTip(Loc.T("Ask Key") + status);
    }

    public void Dispose()
    {
        _menuWindow?.CloseMenuHost();
        _menuWindow = null;
        _icon.Dispose();
    }
}
