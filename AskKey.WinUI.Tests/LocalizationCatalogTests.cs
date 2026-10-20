using System.Text.RegularExpressions;

namespace AskKeyApp.Tests;

public sealed partial class LocalizationCatalogTests
{
    /// <summary>Loc.F ignores surplus arguments, so a translation that drops a
    /// placeholder silently loses a value instead of failing. Compare the argument
    /// indices of every formatted entry with its English source.</summary>
    [Fact]
    public void EveryTranslationKeepsThePlaceholdersOfItsEnglishString()
    {
        var mismatched = new List<string>();
        foreach (var (english, translated) in LocalizationCatalog.SimplifiedChinese)
        {
            if (!english.Contains('{')) continue;
            var expected = Placeholders(english);
            var actual = Placeholders(translated);
            if (!expected.OrderBy(index => index).SequenceEqual(actual.OrderBy(index => index)))
            {
                mismatched.Add($"{english} -> {translated}");
            }
        }

        Assert.Empty(mismatched);
    }

    [Fact]
    public void PlaceholdersSurviveFormattingWithSeveralArguments()
    {
        Assert.Equal([0, 1, 2], Placeholders("{0} {1} {2}"));
        Assert.Equal([1, 0], Placeholders("{1:00} of {0}"));
        Assert.Empty(Placeholders("no placeholders here"));
    }

    private static int[] Placeholders(string format) =>
        PlaceholderPattern().Matches(format)
            .Select(match => int.Parse(match.Groups[1].Value))
            .ToArray();

    [GeneratedRegex(@"\{(\d+)(?:,[^}]*)?(?::[^}]*)?\}")]
    private static partial Regex PlaceholderPattern();
}
