using System.Runtime.InteropServices;
using System.Windows.Forms;

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
    private HotkeyWindow? _window;

    public event Action? Pressed;

    public bool Register(HotkeyOption option)
    {
        Unregister();
        if (option == HotkeyOption.Disabled) return true;
        _window = new HotkeyWindow(this);
        return RegisterHotKey(_window.Handle, HotkeyId, option.Modifiers, option.VirtualKey);
    }

    public void Unregister()
    {
        if (_window is not null)
        {
            UnregisterHotKey(_window.Handle, HotkeyId);
            _window.DestroyHandle();
            _window = null;
        }
    }

    public void Dispose() => Unregister();

    private sealed class HotkeyWindow : NativeWindow
    {
        private readonly GlobalHotkey _owner;

        public HotkeyWindow(GlobalHotkey owner)
        {
            _owner = owner;
            CreateHandle(new CreateParams
            {
                Parent = new IntPtr(-3),
                Caption = "AskKey hotkey",
            });
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0312 && message.WParam == HotkeyId)
            {
                _owner.Pressed?.Invoke();
                return;
            }
            base.WndProc(ref message);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}
