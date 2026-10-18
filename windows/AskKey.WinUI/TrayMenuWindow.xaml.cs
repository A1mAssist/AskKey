using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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
    private const int WhMouseLl = 14;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmRButtonDown = 0x0204;
    private const uint WmMButtonDown = 0x0207;
    private static readonly IntPtr HwndTopmost = new(-1);
    private readonly IntPtr _windowHandle;
    private bool _hostReady;
    private bool _showPending;
    private bool _allowFlyoutClose;
    private IntPtr _mouseHook;
    private LowLevelMouseProc? _mouseHookProc;
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
        TrayMenuFlyout.Opened += TrayMenuOpened;
        TrayMenuFlyout.Closing += TrayMenuClosing;
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
        _allowFlyoutClose = true;
        TrayMenuFlyout.Hide();
        RemoveMouseHook();
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

    // The tray overflow deactivates its popup when the pointer leaves it. Keep
    // the flyout alive and use the hook only for real clicks outside this process.
    private void TrayMenuOpened(object? sender, object e)
    {
        _allowFlyoutClose = false;
        RemoveMouseHook();
        _mouseHookProc = MouseHookProc;
        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseHookProc, GetModuleHandle(null), 0);
    }

    private void TrayMenuClosing(object? sender, FlyoutBaseClosingEventArgs args)
    {
        if (_allowFlyoutClose) return;
        if (_mouseHook == IntPtr.Zero) return;
        if ((GetAsyncKeyState(0x1B) & 0x8000) != 0) return;
        args.Cancel = true;
    }

    private IntPtr MouseHookProc(int code, IntPtr message, IntPtr data)
    {
        var notification = (uint)message.ToInt64();
        if (code >= 0 &&
            (notification == WmLButtonDown ||
             notification == WmRButtonDown ||
             notification == WmMButtonDown))
        {
            var point = Marshal.PtrToStructure<MouseHookData>(data).Point;
            var target = WindowFromPoint(point);
            _ = GetWindowThreadProcessId(target, out var processId);
            if (processId != (uint)Environment.ProcessId)
                MenuAnchor.DispatcherQueue.TryEnqueue(DismissMenu);
        }
        return CallNextHookEx(_mouseHook, code, message, data);
    }

    private void RemoveMouseHook()
    {
        if (_mouseHook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_mouseHook);
        _mouseHook = IntPtr.Zero;
        _mouseHookProc = null;
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
        RemoveMouseHook();
        _allowFlyoutClose = false;
        if (_closing) return;
        AppWindow.Hide();
    }

    private void DismissMenu()
    {
        if (_closing) return;
        _allowFlyoutClose = true;
        TrayMenuFlyout.Hide();
        AppWindow.Hide();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookData
    {
        public NativePoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr LowLevelMouseProc(int code, IntPtr message, IntPtr data);

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

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookType, LowLevelMouseProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
