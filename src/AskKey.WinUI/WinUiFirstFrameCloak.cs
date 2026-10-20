using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace AskKeyApp;

/// <summary>DWM can compose a WinUI 3 window's empty surface before XAML's first
/// frame, which shows as a flash over the owner. Cloak the window until the first
/// visible frame is committed, then let DWM draw it.</summary>
internal sealed class WinUiFirstFrameCloak
{
    private const int DwmWindowCloak = 13;

    private readonly Window _window;
    private readonly nint _handle;
    private FrameworkElement? _content;
    private bool _released;

    private WinUiFirstFrameCloak(Window window)
    {
        _window = window;
        _handle = WindowNative.GetWindowHandle(window);
        SetCloaked(true);
        if (window.Content is FrameworkElement content)
        {
            _content = content;
            if (content.IsLoaded) CompositionTarget.Rendered += OnRendered;
            else content.Loaded += OnLoaded;
        }
        window.Closed += OnClosed;
    }

    internal static void Attach(Window window) => _ = new WinUiFirstFrameCloak(window);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_content is not null) _content.Loaded -= OnLoaded;
        CompositionTarget.Rendered += OnRendered;
    }

    private async void OnRendered(object? sender, RenderedEventArgs e)
    {
        // A frame rendered before the window is shown does not count.
        if (!IsWindowVisible(_handle)) return;
        Release();
        try { await _window.Compositor.RequestCommitAsync(); }
        catch { }
        finally { SetCloaked(false); }
    }

    private void OnClosed(object sender, WindowEventArgs args) => Release();

    private void Release()
    {
        if (_released) return;
        _released = true;
        CompositionTarget.Rendered -= OnRendered;
        if (_content is not null) _content.Loaded -= OnLoaded;
        _window.Closed -= OnClosed;
    }

    private void SetCloaked(bool cloaked)
    {
        var value = cloaked ? 1 : 0;
        DwmSetWindowAttribute(_handle, DwmWindowCloak, ref value, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);
}
