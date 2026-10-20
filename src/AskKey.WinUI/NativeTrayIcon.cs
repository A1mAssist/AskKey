using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AskKeyApp;

/// <summary>
/// Native shell tray icon. The callback is handled on the main application
/// window, which keeps the custom tray menu from losing activation when the
/// pointer leaves the taskbar.
/// </summary>
public sealed class NativeTrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8001;
    private const uint IconId = 1;
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifInfo = 0x00000010;
    private const uint NiifInfo = 0x00000001;
    private const uint WmContextMenu = 0x007B;
    private const uint WmTimer = 0x0113;
    private const uint WmLeftButtonDoubleClick = 0x0203;
    private const uint WmRightButtonUp = 0x0205;
    private const uint NinBalloonUserClick = 0x0405;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x0010;
    private const int TrayIconPixelSize = 32;
    private const int IdiApplication = 32512;
    private const uint RetryTimerId = 2;
    private const int MaxReAddAttempts = 20;

    private readonly IntPtr _windowHandle;
    private readonly SubclassProcedure _subclassProcedure;
    private readonly uint _taskbarCreatedMessage;
    private IntPtr _iconHandle;
    private bool _ownsIcon;
    private string _toolTip;
    private int _reAddAttempts;
    private bool _unavailable;
    private bool _disposed;

    public NativeTrayIcon(IntPtr windowHandle, string toolTip, string? iconPath = null)
    {
        if (windowHandle == IntPtr.Zero)
        {
            throw new ArgumentException("The tray icon requires a valid window handle.", nameof(windowHandle));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(toolTip);
        _windowHandle = windowHandle;
        _toolTip = Truncate(toolTip, 127);
        _subclassProcedure = WindowProcedure;
        _taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");
        if (_taskbarCreatedMessage == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not register the Explorer restart notification.");
        }

        if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
        {
            _iconHandle = LoadImageW(
                IntPtr.Zero,
                iconPath,
                ImageIcon,
                TrayIconPixelSize,
                TrayIconPixelSize,
                LrLoadFromFile);
            _ownsIcon = _iconHandle != IntPtr.Zero;
        }

        if (_iconHandle == IntPtr.Zero)
        {
            _iconHandle = LoadIconW(IntPtr.Zero, new IntPtr(IdiApplication));
        }

        if (_iconHandle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not load the tray icon.");
        }

        if (!SetWindowSubclass(_windowHandle, _subclassProcedure, IconId, UIntPtr.Zero))
        {
            ReleaseOwnedIcon();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not register the tray window message.");
        }

        if (!AddIcon())
        {
            // Explorer may not own the tray yet at login. Keep the icon and retry
            // instead of failing startup with a modal error.
            _reAddAttempts = 1;
            if (SetTimer(_windowHandle, new UIntPtr(RetryTimerId), 500, IntPtr.Zero) == UIntPtr.Zero)
                MarkUnavailable();
        }
    }

    public event Action? ShowRequested;
    public event Action<int, int>? MenuRequested;
    public event Action? NotificationClicked;
    public event Action? Unavailable;

    public void UpdateToolTip(string toolTip)
    {
        if (_disposed) return;
        _toolTip = Truncate(toolTip, 127);
        if (_unavailable) return;
        var data = CreateIconData();
        data.Flags = NifTip;
        ShellNotifyIconW(NimModify, ref data);
    }

    public void ShowBalloon(string title, string text)
    {
        if (_disposed || _unavailable) return;
        var data = CreateIconData();
        data.Flags = NifInfo;
        data.InfoTitle = Truncate(title, 63);
        data.Info = Truncate(text, 255);
        data.InfoFlags = NiifInfo;
        data.TimeoutOrVersion = 4000;
        ShellNotifyIconW(NimModify, ref data);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        KillTimer(_windowHandle, new UIntPtr(RetryTimerId));
        var data = CreateIconData();
        ShellNotifyIconW(NimDelete, ref data);
        RemoveWindowSubclass(_windowHandle, _subclassProcedure, IconId);
        ReleaseOwnedIcon();
        GC.SuppressFinalize(this);
    }

    private bool AddIcon()
    {
        var data = CreateIconData();
        return ShellNotifyIconW(NimAdd, ref data);
    }

    private NotifyIconData CreateIconData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        WindowHandle = _windowHandle,
        Id = IconId,
        Flags = NifMessage | NifIcon | NifTip,
        CallbackMessage = CallbackMessage,
        IconHandle = _iconHandle,
        Tip = _toolTip,
        Info = string.Empty,
        InfoTitle = string.Empty,
    };

    private IntPtr WindowProcedure(
        IntPtr windowHandle,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr referenceData)
    {
        if (message == _taskbarCreatedMessage)
        {
            ReAddIcon();
        }
        else if (message == WmTimer && wParam.ToUInt64() == RetryTimerId)
        {
            RetryAddIcon();
        }
        else if (message == CallbackMessage)
        {
            var notification = unchecked((uint)lParam.ToInt64()) & 0xFFFF;
            if (notification == WmLeftButtonDoubleClick)
            {
                ShowRequested?.Invoke();
            }
            else if (notification is WmRightButtonUp or WmContextMenu)
            {
                if (GetCursorPos(out var cursor))
                {
                    MenuRequested?.Invoke(cursor.X, cursor.Y);
                }
            }
            else if (notification == NinBalloonUserClick)
            {
                NotificationClicked?.Invoke();
            }
        }

        return DefSubclassProc(windowHandle, message, wParam, lParam);
    }

    private void ReAddIcon()
    {
        if (_disposed || _unavailable) return;
        KillTimer(_windowHandle, new UIntPtr(RetryTimerId));
        _reAddAttempts = 1;
        if (AddIcon()) return;
        if (SetTimer(_windowHandle, new UIntPtr(RetryTimerId), 500, IntPtr.Zero) == UIntPtr.Zero)
        {
            MarkUnavailable();
        }
    }

    private void RetryAddIcon()
    {
        if (AddIcon())
        {
            KillTimer(_windowHandle, new UIntPtr(RetryTimerId));
            return;
        }

        _reAddAttempts++;
        if (_reAddAttempts >= MaxReAddAttempts) MarkUnavailable();
    }

    private void MarkUnavailable()
    {
        KillTimer(_windowHandle, new UIntPtr(RetryTimerId));
        _unavailable = true;
        Unavailable?.Invoke();
    }

    private void ReleaseOwnedIcon()
    {
        if (_ownsIcon && _iconHandle != IntPtr.Zero) DestroyIcon(_iconHandle);
        _iconHandle = IntPtr.Zero;
        _ownsIcon = false;
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr WindowHandle;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr IconHandle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;

        public uint State;
        public uint StateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;

        public uint TimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;

        public uint InfoFlags;
        public Guid ItemGuid;
        public IntPtr BalloonIconHandle;
    }

    private delegate IntPtr SubclassProcedure(
        IntPtr windowHandle,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr referenceData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIconW(uint message, ref NotifyIconData data);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        IntPtr windowHandle,
        SubclassProcedure subclassProcedure,
        UIntPtr subclassId,
        UIntPtr referenceData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        IntPtr windowHandle,
        SubclassProcedure subclassProcedure,
        UIntPtr subclassId);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(
        IntPtr windowHandle,
        uint message,
        UIntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImageW(
        IntPtr instance,
        string name,
        uint type,
        int desiredWidth,
        int desiredHeight,
        uint loadFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadIconW(IntPtr instance, IntPtr iconName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern UIntPtr SetTimer(
        IntPtr windowHandle,
        UIntPtr timerId,
        uint intervalMilliseconds,
        IntPtr timerProcedure);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool KillTimer(IntPtr windowHandle, UIntPtr timerId);
}
