using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using AskKey.Vault;

namespace AskKeyApp;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is Visibility.Visible;
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is not Visibility.Visible;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, string language)
        => value is null ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, string language) => null!;
}

public sealed class InverseNullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, string language)
        => value is null ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, string language) => null!;
}

public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, string language)
        => value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, string language) => null!;
}

public sealed class PermissionLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, string language)
        => value is CredentialPermission permission
            ? Loc.T(permission switch
            {
                CredentialPermission.Allowed => "Allow",
                CredentialPermission.Ask => "Ask",
                _ => "Hidden",
            })
            : "";
    public object ConvertBack(object value, Type targetType, object parameter, string language) => null!;
}

public sealed class PermissionBadgeBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, string language)
    {
        var permission = value is CredentialPermission current ? current : CredentialPermission.Hidden;
        var text = string.Equals(parameter as string, "text", StringComparison.Ordinal);
        var key = (permission, text) switch
        {
            (CredentialPermission.Allowed, true) => "SuccessBrush",
            (CredentialPermission.Allowed, false) => "PermissionAllowedSoftBrush",
            (CredentialPermission.Ask, true) => "PermissionAskTextBrush",
            (CredentialPermission.Ask, false) => "PermissionAskSoftBrush",
            (_, true) => "TextSecondaryBrush",
            _ => "SurfaceMutedBrush",
        };
        return Application.Current.Resources[key];
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => null!;
}

public sealed class PayloadKindLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, string language)
        => value is CredentialPayloadKind kind
            ? kind switch
            {
                CredentialPayloadKind.Text => Loc.T("Text"),
                CredentialPayloadKind.File => Loc.T("File"),
                _ => Loc.T("Bundle"),
            }
            : "";
    public object ConvertBack(object value, Type targetType, object parameter, string language) => null!;
}

public sealed class TimestampConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, string language)
    {
        if (value is DateTimeOffset offset) return offset.ToLocalTime().ToString("g", Loc.Culture);
        if (value is string text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed.ToLocalTime().ToString("g", Loc.Culture);
        }
        return value?.ToString() ?? "";
    }
    public object ConvertBack(object value, Type targetType, object parameter, string language) => null!;
}

public sealed class LocalTextOrFallbackConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, string language)
        => value is string text && !string.IsNullOrWhiteSpace(text)
            ? text
            : Loc.T(parameter as string ?? "");
    public object ConvertBack(object value, Type targetType, object parameter, string language) => null!;
}

public sealed class ByteCountConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, string language)
    {
        if (value is not int bytes) return "";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes / (1024.0 * 1024.0):0.#} MB";
    }
    public object ConvertBack(object value, Type targetType, object parameter, string language) => null!;
}
