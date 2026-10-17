using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace AskKeyApp;

public static class AppearanceManager
{
    private const string DarkThemeSource = "Theme.Dark.xaml";
    private const int DwmWindowCornerPreference = 33;
    private const int DwmWindowCornerRound = 2;
    private static string _mode = "system";
    private static bool _watchingSystem;

    public static void Apply(string? mode)
    {
        _mode = mode switch
        {
            "light" => "light",
            "dark" => "dark",
            _ => "system",
        };
        WatchSystemTheme();
        var app = Application.Current;
        if (app is null) return;
        var dark = IsDark();
        var merged = app.Resources.MergedDictionaries;
        var current = merged.FirstOrDefault(dictionary =>
            dictionary.Source?.OriginalString.EndsWith(DarkThemeSource, StringComparison.OrdinalIgnoreCase) == true);
        if (dark && current is null)
        {
            merged.Add(new ResourceDictionary { Source = new Uri(DarkThemeSource, UriKind.Relative) });
        }
        else if (!dark && current is not null)
        {
            merged.Remove(current);
        }
        foreach (Window window in app.Windows) ApplyTitleBar(window, dark);
    }

    public static void ApplyToWindow(Window window)
    {
        if (window.IsLoaded) ApplyTitleBar(window, IsDark());
    }

    internal static bool IsDark()
        => _mode == "dark" || (_mode == "system" && !SystemUsesLightTheme());

    private static bool SystemUsesLightTheme()
    {
        try
        {
            return Convert.ToInt32(Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1)) != 0;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static void WatchSystemTheme()
    {
        if (_watchingSystem) return;
        _watchingSystem = true;
        SystemEvents.UserPreferenceChanged += (_, args) =>
        {
            if (args.Category != UserPreferenceCategory.General) return;
            var app = Application.Current;
            if (app is null) return;
            app.Dispatcher.BeginInvoke(() =>
            {
                if (_mode == "system") Apply(_mode);
            });
        };
    }

    private static void ApplyTitleBar(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var corner = DwmWindowCornerRound;
        DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref corner, sizeof(int));
        var value = dark ? 1 : 0;
        if (DwmSetWindowAttribute(handle, 20, ref value, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(handle, 19, ref value, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}