using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace AskKeyApp;

public static class ComboBoxPlacement
{
    public static readonly DependencyProperty OpenBelowProperty = DependencyProperty.RegisterAttached(
        "OpenBelow", typeof(bool), typeof(ComboBoxPlacement), new PropertyMetadata(false, OnOpenBelowChanged));

    private const int DwmWindowCornerPreference = 33;
    private const int DwmWindowCornerRound = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    private static readonly ConditionalWeakTable<ComboBox, PendingAdjustment> Pending = new();

    private sealed class PendingAdjustment
    {
        public bool Queued;
    }

    public static bool GetOpenBelow(DependencyObject element) => (bool)element.GetValue(OpenBelowProperty);

    public static void SetOpenBelow(DependencyObject element, bool value) => element.SetValue(OpenBelowProperty, value);

    private static void OnOpenBelowChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not ComboBox combo) return;
        combo.DropDownOpened -= Combo_DropDownOpened;
        if (args.NewValue is true) combo.DropDownOpened += Combo_DropDownOpened;
    }

    private static async void Combo_DropDownOpened(object? sender, object e)
    {
        if (sender is not ComboBox combo) return;
        var pending = Pending.GetOrCreateValue(combo);
        if (pending.Queued) return;
        pending.Queued = true;
        // WinUI positions the popup after its open animation; fix it now and again once it settles.
        combo.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => Adjust(combo));
        try
        {
            await Task.Delay(300);
            Adjust(combo);
        }
        catch
        {
            // Keep WinUI's default placement if the popup has already closed.
        }
        finally
        {
            pending.Queued = false;
        }
    }

    private static void Adjust(ComboBox combo)
    {
        if (!GetOpenBelow(combo) || combo.XamlRoot is null) return;
        if (FindPopup(combo) is not { IsOpen: true } popup || popup.Child is not FrameworkElement child) return;
        if (child.ActualHeight <= 0) return;

        var scale = combo.XamlRoot.RasterizationScale;
        var host = Win32Interop.GetWindowFromWindowId(combo.XamlRoot.ContentIslandEnvironment.AppWindowId);
        var popupWindow = FindPopupWindow(host);
        if (popupWindow == IntPtr.Zero) return;

        RoundPopupWindow(popupWindow, scale);

        var origin = default(POINT);
        if (!ClientToScreen(host, ref origin) || !GetWindowRect(popupWindow, out var popupRect)) return;

        var comboTop = combo.TransformToVisual(combo.XamlRoot.Content).TransformPoint(new Point()).Y;
        var desiredTop = origin.Y + (comboTop + combo.ActualHeight) * scale + 4 * scale;
        GetClientRect(host, out var client);
        var clientBottom = new POINT { Y = client.Bottom };
        if (!ClientToScreen(host, ref clientBottom)) return;
        if (desiredTop + child.ActualHeight * scale <= clientBottom.Y - 8 * scale)
        {
            // Moving Popup.VerticalOffset breaks the split-open animation and blanks the selected label.
            var delta = (int)Math.Round(desiredTop - popupRect.Top);
            if (Math.Abs(delta) >= 1)
            {
                SetWindowPos(popupWindow, IntPtr.Zero, popupRect.Left, popupRect.Top + delta, 0, 0,
                    SwpNoSize | SwpNoZOrder | SwpNoActivate);
            }
        }

        RoundWhenSettled(popupWindow, scale);
    }

    private static async void RoundWhenSettled(IntPtr window, double scale)
    {
        await Task.Delay(200);
        if (IsWindowVisible(window)) RoundPopupWindow(window, scale);
    }

    private static Popup? FindPopup(ComboBox combo)
        => FindDescendant<Popup>(combo) ?? combo.FindName("Popup") as Popup;

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private static IntPtr FindPopupWindow(IntPtr host)
    {
        var result = IntPtr.Zero;
        var processId = (uint)Environment.ProcessId;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var ownerProcessId);
            if (ownerProcessId != processId || !IsWindowVisible(window) || GetWindow(window, 4) != host) return true;
            var className = new StringBuilder(64);
            GetClassName(window, className, className.Capacity);
            if (className.ToString() != "Microsoft.UI.Content.PopupWindowSiteBridge") return true;
            result = window;
            return false;
        }, IntPtr.Zero);
        return result;
    }

    private static void RoundPopupWindow(IntPtr window, double scale)
    {
        var corner = DwmWindowCornerRound;
        if (DwmSetWindowAttribute(window, DwmWindowCornerPreference, ref corner, sizeof(int)) == 0) return;

        // Windows 10 has no DWM corner preference; clip the composition surface there.
        if (!GetWindowRect(window, out var rect)) return;
        var radius = Math.Max(1, (int)Math.Round(8 * scale));
        var region = CreateRoundRectRgn(0, 0, rect.Right - rect.Left + 1, rect.Bottom - rect.Top + 1, radius * 2, radius * 2);
        if (region != IntPtr.Zero) SetWindowRgn(window, region, true);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr region, bool redraw);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
}
