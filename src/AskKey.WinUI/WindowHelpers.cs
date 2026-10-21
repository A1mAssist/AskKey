using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace AskKeyApp;

internal static class WindowHelpers
{
    private const int GwlStyle = -16;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmWindowCornerRound = 2;
    private const int DwmWindowCornerDoNotRound = 1;
    private const int DwmBorderColor = 34;
    private const int DwmBorderColorNone = unchecked((int)0xFFFFFFFE);
    private const long WsCaption = 0x00C00000L;
    private const long WsThickFrame = 0x00040000L;

    public static nint Handle(Window window) => WindowNative.GetWindowHandle(window);

    public static void Configure(Window window, int width, int height,
        int minWidth = 0, int minHeight = 0, bool showInSwitchers = true)
    {
        var scale = GetDpiForWindow(Handle(window)) / 96d;
        if (scale <= 0) scale = 1;
        width = (int)Math.Round(width * scale);
        height = (int)Math.Round(height * scale);
        minWidth = (int)Math.Round(minWidth * scale);
        minHeight = (int)Math.Round(minHeight * scale);
        var display = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Primary);
        if (display is not null)
        {
            width = Math.Min(width, Math.Max(320, display.WorkArea.Width - 32));
            height = Math.Min(height, Math.Max(240, display.WorkArea.Height - 32));
            minWidth = Math.Min(minWidth, width);
            minHeight = Math.Min(minHeight, height);
        }
        window.AppWindow.Resize(new SizeInt32(width, height));
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AskKey.ico");
        if (File.Exists(iconPath)) window.AppWindow.SetIcon(iconPath);
        window.AppWindow.IsShownInSwitchers = showInSwitchers;
        var handle = Handle(window);
        if (minWidth > 0 || minHeight > 0)
        {
            var style = GetWindowLongPtr(handle, GwlStyle).ToInt64();
            SetWindowLongPtr(handle, GwlStyle, new IntPtr(style | WsCaption | WsThickFrame));
            SetWindowSubclass(handle, MinMaxProc, 0x41534B32, UIntPtr.Zero);
            MinSizeStore.Set(handle, minWidth, minHeight);
            window.Closed += (_, _) => MinSizeStore.Remove(handle);
        }
        EnableRoundedCorners(window);
        Center(window);
    }

    // DWM clips the top-level frame; extended title-bar content does not request this itself.
    public static void EnableRoundedCorners(Window window)
    {
        var corner = DwmWindowCornerRound;
        DwmSetWindowAttribute(Handle(window), DwmWindowCornerPreference, ref corner, sizeof(int));
    }

    // Menus draw their own surface and corner region; DWM's own frame would add a
    // grey halo outside it. Border=none drops it; donotround defers corners to SetWindowRgn.
    public static void DisableWindowFrame(Window window)
    {
        var handle = Handle(window);
        var border = DwmBorderColorNone;
        DwmSetWindowAttribute(handle, DwmBorderColor, ref border, sizeof(int));
        var corners = DwmWindowCornerDoNotRound;
        DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref corners, sizeof(int));
    }

    /// <summary>
    /// Brings the window to the foreground when something other than the user asked for
    /// it (an Agent request or a tray click). Activate() alone is routinely denied by the
    /// foreground lock, which leaves the window behind whatever the user is doing; attach
    /// to the foreground thread to take the right, and flash the taskbar button if that
    /// still fails so the request is never silent.
    /// </summary>
    public static void BringToFront(Window window)
    {
        var handle = Handle(window);
        if (handle == IntPtr.Zero) return;
        var foreground = GetForegroundWindow();
        if (foreground == handle) return;
        var foregroundThread = GetWindowThreadProcessId(foreground, out _);
        var currentThread = GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != currentThread
            && AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            BringWindowToTop(handle);
            SetForegroundWindow(handle);
        }
        finally
        {
            if (attached) AttachThreadInput(currentThread, foregroundThread, false);
        }
        if (GetForegroundWindow() == handle) return;
        var flash = new FlashInfo
        {
            Size = Marshal.SizeOf<FlashInfo>(), Window = handle, Flags = FlashAll, Count = 3,
        };
        FlashWindowEx(ref flash);
    }

    public static async Task<T> ShowDialogAsync<T>(Window dialog, Window? owner, Task<T> completion)
    {
        var ownerHandle = owner is null || ReferenceEquals(dialog, owner) ? IntPtr.Zero : Handle(owner);
        var ownerVisible = ownerHandle != IntPtr.Zero && IsWindowVisible(ownerHandle);
        if (ownerHandle != IntPtr.Zero) SetOwner(dialog, owner);
        WinUiFirstFrameCloak.Attach(dialog);
        try
        {
            // Let the dialog take activation before the owner becomes disabled. Disabling
            // an active owner first makes it repaint once while the dialog is still blank.
            dialog.Activate();
            if (ownerHandle != IntPtr.Zero) EnableWindow(ownerHandle, false);
            return await completion;
        }
        finally
        {
            if (ownerHandle != IntPtr.Zero)
            {
                EnableWindow(ownerHandle, true);
                if (ownerVisible) owner!.Activate();
            }
        }
    }

    public static void SetOwner(Window window, Window? owner)
    {
        if (owner is null) return;
        SetWindowLongPtr(Handle(window), -8, Handle(owner));
    }

    public static void Center(Window window)
    {
        var display = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Primary);
        if (display is null) return;
        var work = display.WorkArea;
        var size = window.AppWindow.Size;
        window.AppWindow.Move(new PointInt32(
            work.X + Math.Max(0, (work.Width - size.Width) / 2),
            work.Y + Math.Max(0, (work.Height - size.Height) / 2)));
    }

    public static async Task<bool> ShowAsync(Window window, Func<bool> result)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Closed(object sender, WindowEventArgs args) => completion.TrySetResult(result());
        window.Closed += Closed;
        window.Activate();
        try { return await completion.Task; }
        finally { window.Closed -= Closed; }
    }

    private static IntPtr MinMaxProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam,
        UIntPtr subclassId, UIntPtr referenceData)
    {
        if (message == 0x0024 && MinSizeStore.TryGet(hwnd, out var minWidth, out var minHeight))
        {
            var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            info.MinTrackSize.X = minWidth;
            info.MinTrackSize.Y = minHeight;
            Marshal.StructureToPtr(info, lParam, false);
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private sealed class MinSizeStore
    {
        private static readonly Dictionary<IntPtr, (int Width, int Height)> Sizes = [];
        public static void Set(IntPtr handle, int width, int height) => Sizes[handle] = (width, height);
        public static void Remove(IntPtr handle) => Sizes.Remove(handle);
        public static bool TryGet(IntPtr handle, out int width, out int height)
        {
            if (Sizes.TryGetValue(handle, out var size))
            {
                (width, height) = size;
                return true;
            }
            width = height = 0;
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved;
        public Point MaxSize;
        public Point MaxPosition;
        public Point MinTrackSize;
        public Point MaxTrackSize;
    }

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam,
        UIntPtr subclassId, UIntPtr referenceData);

    private static readonly SubclassProc MinMaxProcDelegate = MinMaxProc;

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id, UIntPtr data);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool EnableWindow(IntPtr hwnd, bool enable);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public int Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    public const uint FlashAll = 3;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool attachOrDetach);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "FlashWindowEx")]
    private static extern bool FlashWindowEx(ref FlashInfo info);
}
