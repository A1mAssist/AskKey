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

    /// <summary>The part before the first table header. Its keys are root keys, so
    /// only two shapes there declare mcp_servers.askkey: a dotted assignment
    /// (mcp_servers.askkey...) and an mcp_servers inline table holding askkey.
    /// A bare root "askkey" is an unrelated key and is kept.</summary>
    public static string StripRootAskKeyKeys(string raw, string newline)
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
            if (KeyName(code) is { } key && DottedParts(key) is { } path)
            {
                if (path is ["mcp_servers", "askkey", ..])
                {
                    if (code.Contains('{'))
                    {
                        skippingBraces = BraceDelta(code);
                        if (skippingBraces > 0) continue;
                    }
                    continue;
                }
                if (path is ["mcp_servers"])
                {
                    // mcp_servers is an inline value here, so every member is a
                    // whole server table. Removing askkey would leave a value that
                    // cannot coexist with the [mcp_servers.askkey] header Upsert
                    // appends, and expanding the rest would need a real TOML writer.
                    if (!HasAskKeyMember(code[(code.IndexOf('=') + 1)..]))
                    {
                        kept.Add(line + suffix);
                        continue;
                    }
                    if (SingleServerInline(code[(code.IndexOf('=') + 1)..]))
                    {
                        continue;
                    }
                    throw new CodexUserMCPException(CodexUserMCPError.IllegalConfig);
                }
            }
            kept.Add(line + suffix);
        }
        return string.Concat(kept);
    }

    /// <summary>True when this inline mcp_servers value holds an askkey member at
    /// its top level.</summary>
    private static bool HasAskKeyMember(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith('{') || !trimmed.EndsWith('}')) return false;
        return InlineMembers(trimmed[1..^1])
            .Any(member => IsAskKeyMember(trimmed[1..^1], member));
    }

    /// <summary>True when askkey is the only member, so dropping the assignment
    /// leaves a document the appended table header can legally complete.</summary>
    private static bool SingleServerInline(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith('{') || !trimmed.EndsWith('}')) return false;
        var inner = trimmed[1..^1];
        var members = InlineMembers(inner);
        return members.Count == 1 && IsAskKeyMember(inner, members[0]);
    }

    private static bool IsAskKeyMember(string inner, (int Start, int End) member)
        => DottedParts(KeyName(inner[member.Start..member.End]) ?? "") is ["askkey", ..];

    /// <summary>Top-level member ranges of an inline table body.</summary>
    private static List<(int Start, int End)> InlineMembers(string inner)
    {
        var members = new List<(int Start, int End)>();
        var depth = 0;
        char? quote = null;
        var escaped = false;
        var start = 0;
        for (var index = 0; index < inner.Length; index++)
        {
            var character = inner[index];
            if (quote is { } current)
            {
                if (escaped) escaped = false;
                else if (character == '\\' && current == '"') escaped = true;
                else if (character == current) quote = null;
                continue;
            }
            if (character is '"' or '\'')
            {
                quote = character;
                continue;
            }
            if (character is '{' or '[') depth++;
            else if (character is '}' or ']') depth--;
            else if (character == ',' && depth == 0)
            {
                if (inner[start..index].Trim().Length > 0) members.Add((start, index));
                start = index + 1;
            }
        }
        if (inner[start..].Trim().Length > 0) members.Add((start, inner.Length));
        return members;
    }
}
