using System.Text;

namespace AskKey.Integrations;

internal static partial class CodexAskKeyTOML
{
    public static string StripAskKeyKeys(string raw, string newline)
    {
        var kept = new List<string>();
        var skippingBraces = 0;
        var sourceLines = Lines(raw, newline);
        var endsWithNewline = raw.EndsWith(newline, StringComparison.Ordinal);
        for (var index = 0; index < sourceLines.Count; index++)
        {
            var line = sourceLines[index];
            var isLast = index == sourceLines.Count - 1;
            var suffix = isLast && !endsWithNewline ? "" : newline;
            if (skippingBraces > 0)
            {
                skippingBraces += BraceDelta(line);
                continue;
            }
            var code = CodePortion(line).Trim();
            if (KeyName(code) is { } key && DottedParts(key) is [ "askkey", .. ])
            {
                if (code.Contains('{'))
                {
                    skippingBraces = BraceDelta(code);
                    if (skippingBraces > 0) continue;
                }
                continue;
            }
            kept.Add(line + suffix);
        }
        return string.Concat(kept);
    }
}
