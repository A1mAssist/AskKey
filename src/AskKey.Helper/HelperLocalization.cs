using System.Globalization;

namespace AskKey.Helper;

internal static class HelperLocalization
{
    public const string CatalogReminder = "Ask Key: Query the credential catalog before connecting.";

    public static string Localized(string key, IReadOnlyList<string>? preferredLanguages = null)
    {
        var preferred = preferredLanguages?.FirstOrDefault();
        if (string.IsNullOrEmpty(preferred))
        {
            preferred = CultureInfo.CurrentUICulture.Name;
        }
        if (string.IsNullOrEmpty(preferred))
        {
            preferred = CultureInfo.CurrentCulture.Name;
        }
        var language = preferred.ToLowerInvariant();
        var chinese = language == "zh" || language.StartsWith("zh-", StringComparison.Ordinal)
            || language.StartsWith("zh_", StringComparison.Ordinal);
        if (chinese && key == CatalogReminder)
        {
            return "Ask Key：连接前先查询凭证目录。";
        }
        return key;
    }
}
