using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace AskKeyApp;

public static class AppLanguage
{
    public const string System = "system";
    public const string English = "en";
    public const string SimplifiedChinese = "zh-Hans";
}

/// <summary>
/// Resolves the UI language and updates every XAML element tagged with
/// <c>loc:Loc.Key</c>. Keys are the English source strings, which keeps the
/// catalog easy to compare with the Swift catalog.
/// </summary>
public static class Loc
{
    private static readonly CultureInfo ChineseCulture = CultureInfo.GetCultureInfo("zh-CN");
    private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en-US");
    private static readonly List<WeakReference<DependencyObject>> Tracked = [];

    public static string Mode { get; private set; } = AppLanguage.System;
    public static string Current { get; private set; } = AppLanguage.English;
    public static event Action? LanguageChanged;

    public static CultureInfo Culture => Current == AppLanguage.SimplifiedChinese ? ChineseCulture : EnglishCulture;

    public static void Initialize(string? mode)
    {
        Mode = Normalize(mode);
        Current = Resolve(Mode);
    }

    public static void SetMode(string? mode)
    {
        Mode = Normalize(mode);
        Current = Resolve(Mode);
        ApplyToAll();
        LanguageChanged?.Invoke();
    }

    public static string Resolve(string mode)
    {
        if (mode == AppLanguage.SimplifiedChinese) return AppLanguage.SimplifiedChinese;
        if (mode == AppLanguage.English) return AppLanguage.English;
        var name = CultureInfo.CurrentUICulture.Name;
        return name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? AppLanguage.SimplifiedChinese
            : AppLanguage.English;
    }

    public static string T(string english)
        => Current == AppLanguage.SimplifiedChinese
            && LocalizationCatalog.SimplifiedChinese.TryGetValue(english, out var translated)
                ? translated
                : english;

    public static string F(string english, params object?[] arguments)
        => string.Format(Culture, T(english), arguments);

    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(string), typeof(Loc), new PropertyMetadata(null, OnKeyChanged));

    public static void SetKey(DependencyObject element, string? value) => element.SetValue(KeyProperty, value);
    public static string? GetKey(DependencyObject element) => (string?)element.GetValue(KeyProperty);

    public static readonly DependencyProperty ToolTipKeyProperty = DependencyProperty.RegisterAttached(
        "ToolTipKey", typeof(string), typeof(Loc), new PropertyMetadata(null, OnToolTipKeyChanged));

    public static void SetToolTipKey(DependencyObject element, string? value) => element.SetValue(ToolTipKeyProperty, value);
    public static string? GetToolTipKey(DependencyObject element) => (string?)element.GetValue(ToolTipKeyProperty);

    private static string Normalize(string? mode)
    {
        var value = (mode ?? "").Trim().ToLowerInvariant();
        return value switch
        {
            "zh" or "zh-cn" or "zh-hans" or "zh-hans-cn" => AppLanguage.SimplifiedChinese,
            "en" or "en-us" or "en-gb" => AppLanguage.English,
            _ => AppLanguage.System,
        };
    }

    private static void OnKeyChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        Track(element);
        if (args.NewValue is string key) ApplyKey(element, key);
    }

    private static void OnToolTipKeyChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        Track(element);
        if (args.NewValue is string key) ApplyToolTip(element, key);
    }

    private static void ApplyKey(DependencyObject element, string key)
    {
        var text = T(key);
        switch (element)
        {
            case Window window:
                window.Title = text;
                break;
            case TextBlock block:
                block.Text = text;
                break;
            case Run run:
                run.Text = text;
                break;
            case ToolTip tip:
                tip.Content = text;
                break;
            case HeaderedContentControl headered:
                headered.Header = text;
                break;
            case MenuItem menuItem:
                menuItem.Header = text;
                break;
            case ContentControl content:
                content.Content = text;
                break;
        }
    }

    private static void ApplyToolTip(DependencyObject element, string key)
    {
        var text = T(key);
        switch (element)
        {
            case FrameworkElement elementWithTip:
                elementWithTip.ToolTip = text;
                break;
            case FrameworkContentElement elementWithTip:
                elementWithTip.ToolTip = text;
                break;
        }
    }

    private static void Track(DependencyObject element)
    {
        lock (Tracked) Tracked.Add(new WeakReference<DependencyObject>(element));
    }

    private static void ApplyToAll()
    {
        List<DependencyObject> live = [];
        lock (Tracked)
        {
            for (var index = Tracked.Count - 1; index >= 0; index--)
            {
                if (Tracked[index].TryGetTarget(out var element)) live.Add(element);
                else Tracked.RemoveAt(index);
            }
        }
        foreach (var element in live)
        {
            if (GetKey(element) is { } key) ApplyKey(element, key);
            if (GetToolTipKey(element) is { } toolTipKey) ApplyToolTip(element, toolTipKey);
        }
    }
}
