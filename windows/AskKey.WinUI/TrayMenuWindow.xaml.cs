using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace AskKeyApp;

public sealed partial class TrayMenuWindow : Window
{
    private const int AnchorSize = 2;
    private const int HostSize = 64;
    private const int GwlExStyle = -20;
    private const long WsExLayered = 0x00080000;
    private const long WsExToolWindow = 0x00000080;
    private const uint LwaAlpha = 0x00000002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private static readonly IntPtr HwndTopmost = new(-1);
    private readonly IntPtr _windowHandle;
    private bool _hostReady;
    private bool _showPending;
    private bool _menuOpen;
    private bool _closing;
    private int _pendingCount;
    private bool _paused;
    private (int X, int Y)? _queuedShow;

    public TrayMenuWindow()
    {
        InitializeComponent();
        ApplyLanguage();
        ApplyTheme(ThemeManager.IsDark);
        MenuAnchor.Loaded += MenuAnchorLoaded;
        Activated += TrayMenuActivated;
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.PreferredMinimumWidth = HostSize;
            presenter.PreferredMinimumHeight = HostSize;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        _windowHandle = WindowHelpers.Handle(this);
        var extendedStyle = GetWindowLongPtr(_windowHandle, GwlExStyle).ToInt64();
        SetWindowLongPtr(
            _windowHandle,
            GwlExStyle,
            new IntPtr(extendedStyle | WsExLayered | WsExToolWindow));
        SetLayeredWindowAttributes(_windowHandle, 0, 0, LwaAlpha);

        Activate();
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
        if (_closing) return;
        if (!_hostReady)
        {
            _queuedShow = (x, y);
            return;
        }
        if (_showPending || TrayMenuFlyout.IsOpen) return;

        _showPending = true;
        var workArea = DisplayArea.GetFromPoint(
            new PointInt32(x, y),
            DisplayAreaFallback.Primary).WorkArea;
        var anchorX = Math.Clamp(x, workArea.X, workArea.X + workArea.Width - AnchorSize);
        var anchorY = Math.Clamp(y, workArea.Y, workArea.Y + workArea.Height - AnchorSize);
        if (!SetWindowPos(
                _windowHandle,
                HwndTopmost,
                anchorX,
                anchorY,
                HostSize,
                HostSize,
                SwpNoActivate | SwpShowWindow | SwpNoOwnerZOrder) ||
            !GetWindowRect(_windowHandle, out var bounds) ||
            bounds.Left != anchorX ||
            bounds.Top != anchorY)
        {
            _showPending = false;
            AppWindow.Hide();
            return;
        }

        MenuAnchor.UpdateLayout();
        if (!MenuAnchor.DispatcherQueue.TryEnqueue(() =>
            {
                _showPending = false;
                _menuOpen = true;
                SetForegroundWindow(_windowHandle);
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
        _menuOpen = false;
        TrayMenuFlyout.Hide();
        Close();
    }

    private void MenuAnchorLoaded(object sender, RoutedEventArgs e)
    {
        MenuAnchor.Loaded -= MenuAnchorLoaded;
        AppWindow.MoveAndResize(new RectInt32(0, 0, HostSize, HostSize));
        MenuAnchor.DispatcherQueue.TryEnqueue(() =>
        {
            MenuAnchor.UpdateLayout();
            _hostReady = true;
            AppWindow.Hide();
            if (_queuedShow is { } queued)
            {
                _queuedShow = null;
                ShowAt(queued.X, queued.Y);
            }
        });
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
        if (_closing) return;
        _menuOpen = false;
        AppWindow.Hide();
    }

    private void TrayMenuActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated && _menuOpen)
            DismissMenu();
    }

    private void DismissMenu()
    {
        _menuOpen = false;
        TrayMenuFlyout.Hide();
        AppWindow.Hide();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr windowHandle, int index);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr windowHandle, int index, IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(
        IntPtr windowHandle, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out WindowRect bounds);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);
}
