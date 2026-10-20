using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace AskKeyApp;

public static class ThemeManager
{
    private static string _mode = "system";

    public static string Mode => _mode;
    public static bool IsDark => _mode == "dark" || (_mode == "system" && IsSystemDark());

    public static void Apply(string? mode, Window? window = null)
    {
        _mode = mode switch
        {
            "light" => "light",
            "dark" => "dark",
            _ => "system",
        };
        if (window is not null) Apply(window);
    }

    public static void Apply(Window window)
    {
        var theme = _mode switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        if (window.Content is FrameworkElement root) root.RequestedTheme = theme;
        ApplyTitleBar(window, theme == ElementTheme.Dark || (theme == ElementTheme.Default && IsSystemDark()));
    }

    private static bool IsSystemDark()
    {
        try
        {
            var value = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1);
            return Convert.ToInt32(value) == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyTitleBar(Window window, bool dark)
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;
        var titleBar = window.AppWindow.TitleBar;
        var background = dark ? ColorHelper.FromArgb(255, 27, 30, 34) : ColorHelper.FromArgb(255, 248, 248, 246);
        var foreground = dark ? Colors.White : ColorHelper.FromArgb(255, 28, 31, 35);
        titleBar.BackgroundColor = background;
        titleBar.InactiveBackgroundColor = background;
        titleBar.ForegroundColor = foreground;
        titleBar.InactiveForegroundColor = dark
            ? ColorHelper.FromArgb(255, 129, 136, 144)
            : ColorHelper.FromArgb(255, 142, 148, 154);
        titleBar.ButtonBackgroundColor = background;
        titleBar.ButtonInactiveBackgroundColor = background;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = dark
            ? ColorHelper.FromArgb(255, 129, 136, 144)
            : ColorHelper.FromArgb(255, 142, 148, 154);
        titleBar.ButtonHoverBackgroundColor = dark
            ? ColorHelper.FromArgb(255, 42, 46, 51)
            : ColorHelper.FromArgb(255, 236, 238, 236);
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedBackgroundColor = dark
            ? ColorHelper.FromArgb(255, 48, 53, 59)
            : ColorHelper.FromArgb(255, 225, 227, 223);
    }
}
