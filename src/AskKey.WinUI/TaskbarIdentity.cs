using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AskKeyApp;

/// <summary>
/// Unpackaged WinUI 3 has no PackageIdentity. The Velopack stub sets an explicit
/// AppUserModelID on the process, but nothing registers an icon for it, so Explorer
/// falls back to a generic blank page on the taskbar. Windows App SDK only registers
/// that icon when an app uses WinRT toasts; Ask Key uses Shell_NotifyIcon, so it never
/// gets one. Registering the ID with an IconUri is what makes the taskbar and Start
/// menu show the real icon.
/// </summary>
internal static class TaskbarIdentity
{
    // Must stay velopack.AskKey: the Velopack stub sets this ID before launch and the
    // generated Start Menu shortcut is grouped under it. Changing it splits existing
    // shortcuts into a second taskbar entry.
    private const string AppUserModelId = "velopack.AskKey";
    private const string DisplayName = "Ask Key";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);

    public static void Register()
    {
        // Runs before any window so Explorer groups the taskbar entry under this ID.
        SetCurrentProcessExplicitAppUserModelID(AppUserModelId);

        try
        {
            var iconPath = WriteIcon();
            using var key = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\AppUserModelId\{AppUserModelId}");
            key?.SetValue("DisplayName", DisplayName, RegistryValueKind.String);
            if (iconPath is not null) key?.SetValue("IconUri", iconPath, RegistryValueKind.String);
        }
        catch (Exception)
        {
            // A missing taskbar icon is cosmetic; never block startup on it.
        }
    }

    private static string? WriteIcon()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Assets", "AskKey.ico");
        if (!File.Exists(source)) return null;

        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsAppSDK");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, AppUserModelId + ".png");
        if (!File.Exists(target)) ConvertToPng(source, target);
        return File.Exists(target) ? target : null;
    }

    // The shell reads IconUri as a bitmap, so the .ico has to be flattened to PNG.
    private static void ConvertToPng(string source, string target)
    {
        using var icon = new Icon(source, 32, 32);
        using var bitmap = icon.ToBitmap();
        bitmap.Save(target, System.Drawing.Imaging.ImageFormat.Png);
    }
}
