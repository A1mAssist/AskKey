using System.Drawing;
using System.Windows.Forms;
using Microsoft.UI.Dispatching;

namespace AskKeyApp;

public sealed class TrayHost : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Action _showMain;
    private readonly Action _showPending;
    private readonly Action _showSettings;
    private readonly Action _toggleAgentAccess;
    private readonly Action _quitAction;
    private readonly DispatcherQueue? _dispatcher;
    private TrayMenuWindow? _menuWindow;
    private int _pendingCount;
    private bool _paused;
    private Action? _notificationClick;

    public TrayHost(Action showMain, Action showPending, Action showSettings,
        Action toggleAgentAccess, Action quit)
    {
        _showMain = showMain;
        _showPending = showPending;
        _showSettings = showSettings;
        _toggleAgentAccess = toggleAgentAccess;
        _quitAction = quit;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = Loc.T("Ask Key"),
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => _showMain();
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right) ShowMenuAtCursor();
        };
        _icon.BalloonTipClicked += (_, _) =>
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
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.BalloonTipIcon = ToolTipIcon.Info;
        _icon.ShowBalloonTip(4000);
    }

    private void ShowMenuAtCursor()
    {
        var position = Cursor.Position;
        if (_dispatcher is { HasThreadAccess: false })
        {
            _dispatcher.TryEnqueue(() => ShowMenuAt(position));
            return;
        }
        ShowMenuAt(position);
    }

    private void ShowMenuAt(Point position)
    {
        try
        {
            _menuWindow ??= CreateMenuWindow();
            _menuWindow.ShowAt(position.X, position.Y);
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
        _icon.Text = Loc.T("Ask Key") + status;
    }

    private static Icon LoadIcon()
    {
        var packaged = Path.Combine(AppContext.BaseDirectory, "Assets", "AskKey.ico");
        try
        {
            if (File.Exists(packaged)) return new Icon(packaged);
        }
        catch
        {
        }
        return (Icon)SystemIcons.Application.Clone();
    }

    public void Dispose()
    {
        _menuWindow?.CloseMenuHost();
        _menuWindow = null;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
