using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AskKeyApp;

public sealed record HotkeyOption(string Id, string Label, uint Modifiers, uint VirtualKey)
{
    public static readonly HotkeyOption CtrlShiftSpace = new("ctrlShiftSpace", "Ctrl+Shift+Space", 0x0002 | 0x0004, 0x20);
    public static readonly HotkeyOption CtrlShiftL = new("ctrlShiftL", "Ctrl+Shift+L", 0x0002 | 0x0004, 0x4C);
    public static readonly HotkeyOption CtrlShiftK = new("ctrlShiftK", "Ctrl+Shift+K", 0x0002 | 0x0004, 0x4B);
    public static readonly HotkeyOption CtrlAltSpace = new("ctrlAltSpace", "Ctrl+Alt+Space", 0x0002 | 0x0001, 0x20);
    public static readonly HotkeyOption Disabled = new("disabled", "Off", 0, 0);

    public static readonly IReadOnlyList<HotkeyOption> All = [CtrlShiftSpace, CtrlShiftL, CtrlShiftK, CtrlAltSpace, Disabled];
    public static IReadOnlyList<HotkeyOption> AllLocalized => [CtrlShiftSpace, CtrlShiftL, CtrlShiftK, CtrlAltSpace, Disabled with { Label = Loc.T("Off") }];

    public static HotkeyOption FromId(string id) => All.FirstOrDefault(option => option.Id == id) ?? CtrlShiftSpace;
}

public sealed class GlobalHotkey : IDisposable
{
    private const int HotkeyId = 0x41534B31;
    private HwndSource? _source;
    private nint _handle;
    private bool _registered;

    public event Action? Pressed;

    public bool Register(HotkeyOption option)
    {
        Unregister();
        if (option == HotkeyOption.Disabled) return true;
        // A message-only window keeps the shortcut alive while the App runs in the tray.
        _source = new HwndSource(new HwndSourceParameters("AskKey hotkey")
        {
            ParentWindow = new nint(-3),
            WindowStyle = 0,
        });
        _handle = _source.Handle;
        _source.AddHook(WndProc);
        _registered = RegisterHotKey(_handle, HotkeyId, option.Modifiers, option.VirtualKey);
        return _registered;
    }

    public void Unregister()
    {
        if (_registered && _handle != 0) UnregisterHotKey(_handle, HotkeyId);
        _registered = false;
        _source?.RemoveHook(WndProc);
        _source?.Dispose();
        _source = null;
        _handle = 0;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == 0x0312 && wParam == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return 0;
    }

    public void Dispose() => Unregister();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}
