using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace AskKeyApp;

public sealed partial class TrayMenuWindow : Window
{
    private const int AnchorSize = 2;
    private bool _showPending;
    private bool _closing;
    private int _pendingCount;
    private bool _paused;

    public TrayMenuWindow()
    {
        InitializeComponent();
        ApplyLanguage();
        ApplyTheme(ThemeManager.IsDark);
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }
    }

    public event Action? ShowRequested;
    public event Action? PendingRequested;
    public event Action? TogglePausedRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    public void ApplyTheme(bool dark)
    {
        MenuAnchor.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
    }

    public void ApplyLanguage()
    {
        OpenMenuItem.Text = Loc.T("Open Ask Key");
        SettingsMenuItem.Text = Loc.T("Settings");
        QuitMenuItem.Text = Loc.T("Quit Ask Key");
        AutomationProperties.SetName(OpenMenuItem, OpenMenuItem.Text);
        AutomationProperties.SetName(SettingsMenuItem, SettingsMenuItem.Text);
        AutomationProperties.SetName(QuitMenuItem, QuitMenuItem.Text);
        UpdatePendingCount(_pendingCount);
        UpdatePaused(_paused);
    }

    public void UpdatePendingCount(int count)
    {
        _pendingCount = count;
        PendingMenuItem.Text = count > 0
            ? Loc.F("Pending requests ({0})", count)
            : Loc.T("Pending requests");
        PendingMenuItem.IsEnabled = count > 0;
        AutomationProperties.SetName(PendingMenuItem, PendingMenuItem.Text);
    }

    public void UpdatePaused(bool paused)
    {
        _paused = paused;
        PauseMenuItem.Text = paused ? Loc.T("Resume Agent access") : Loc.T("Pause Agent access");
        AutomationProperties.SetName(PauseMenuItem, PauseMenuItem.Text);
    }

    public void ShowAt(int x, int y)
    {
        if (_closing || _showPending || TrayMenuFlyout.IsOpen) return;

        _showPending = true;
        var workArea = DisplayArea.GetFromPoint(
            new PointInt32(x, y),
            DisplayAreaFallback.Primary).WorkArea;
        var anchorX = Math.Clamp(x, workArea.X, workArea.X + workArea.Width - AnchorSize);
        var anchorY = Math.Clamp(y, workArea.Y, workArea.Y + workArea.Height - AnchorSize);
        AppWindow.MoveAndResize(new RectInt32(anchorX, anchorY, AnchorSize, AnchorSize));
        Activate();
        if (!MenuAnchor.DispatcherQueue.TryEnqueue(() =>
            {
                _showPending = false;
                TrayMenuFlyout.ShowAt(MenuAnchor);
            }))
        {
            _showPending = false;
            AppWindow.Hide();
        }
    }

    public void CloseMenuHost()
    {
        _closing = true;
        TrayMenuFlyout.Hide();
        Close();
    }

    private void OpenClick(object sender, RoutedEventArgs e)
    {
        DismissMenu();
        ShowRequested?.Invoke();
    }

    private void PendingClick(object sender, RoutedEventArgs e)
    {
        DismissMenu();
        PendingRequested?.Invoke();
    }

    private void PauseClick(object sender, RoutedEventArgs e)
    {
        DismissMenu();
        TogglePausedRequested?.Invoke();
    }

    private void SettingsClick(object sender, RoutedEventArgs e)
    {
        DismissMenu();
        SettingsRequested?.Invoke();
    }

    private void QuitClick(object sender, RoutedEventArgs e)
    {
        DismissMenu();
        ExitRequested?.Invoke();
    }

    private void TrayMenuClosed(object sender, object e)
    {
        if (!_closing) AppWindow.Hide();
    }

    private void DismissMenu()
    {
        TrayMenuFlyout.Hide();
        AppWindow.Hide();
    }
}
