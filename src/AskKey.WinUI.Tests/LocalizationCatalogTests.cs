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

    /// <summary>Loc.T falls back to English, so a string that reaches the UI without a
    /// catalog entry shows up as English text inside an otherwise Chinese window. Scan the
    /// shipped sources for those keys instead of discovering them from a screenshot.</summary>
    [Fact]
    public void EveryUserFacingStringHasAChineseTranslation()
    {
        var root = SourceRoot();
        Assert.True(root is not null, "Could not locate the AskKey.WinUI source folder.");

        var catalog = LocalizationCatalog.SimplifiedChinese.Keys.ToHashSet(StringComparer.Ordinal);
        var untranslated = new List<string>();

        foreach (var path in Directory.EnumerateFiles(root!, "*.*", SearchOption.AllDirectories))
        {
            if (Path.GetExtension(path) is not (".cs" or ".xaml")) continue;
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || Path.GetFileName(path) == "LocalizationCatalog.cs") continue;

            var text = File.ReadAllText(path);
            foreach (var key in KeysIn(text))
            {
                if (key.Length > 0 && !catalog.Contains(key)) untranslated.Add($"{Path.GetFileName(path)}: {key}");
            }
        }

        Assert.Empty(untranslated);
    }

    private static IEnumerable<string> KeysIn(string text)
    {
        foreach (Match match in LiteralCall().Matches(text))
        {
            var parts = Quoted().Matches(match.Groups[1].Value)
                .Select(part => part.Groups[1].Value.Replace("\\\"", "\"").Replace("\\n", "\n"));
            yield return string.Concat(parts);
        }
        foreach (Match match in LiteralAttribute().Matches(text))
        {
            // XAML carries the quote as an entity; the catalog stores the real character.
            yield return match.Groups[1].Value.Replace("&quot;", "\"");
        }
    }

    private static string? SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "AskKey.WinUI");
            if (File.Exists(Path.Combine(candidate, "AskKey.WinUI.csproj"))) return candidate;
            directory = directory.Parent;
        }
        return null;
    }

    [GeneratedRegex(@"Loc\.[TF]\(\s*((?:""(?:[^""\\]|\\.)*""\s*(?:\+\s*)?)+)")]
    private static partial Regex LiteralCall();

    [GeneratedRegex(@"""((?:[^""\\]|\\.)*)""")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"Loc\.(?:Key|ToolTipKey)=""([^""]*)""")]
    private static partial Regex LiteralAttribute();
}