using System.Text;

namespace AskKey.Integrations;

/// <summary>Diff redaction for Codex TOML. Secrets must never reach a confirmation screen.</summary>
internal static class CodexTomlRedaction
{
    public static string Redact(string text) => RedactLineValues(RedactMultilineStrings(text));

    private static string RedactMultilineStrings(string text)
    {
        var result = new StringBuilder();
        var index = 0;
        var inComment = false;
        var inBasic = false;
        var inLiteral = false;
        var escaped = false;
        while (index < text.Length)
        {
            var character = text[index];
            if (inComment)
            {
                result.Append(character);
                if (character == '\n') inComment = false;
                index++;
                continue;
            }
            if (inBasic)
            {
                result.Append(character);
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == '"') inBasic = false;
                index++;
                continue;
            }
            if (inLiteral)
            {
                result.Append(character);
                if (character == '\'') inLiteral = false;
                index++;
                continue;
            }
            if (character == '#')
            {
                inComment = true;
                result.Append(character);
                index++;
                continue;
            }
            if (text.AsSpan(index).StartsWith("\"\"\""))
            {
                result.Append("\"\"\"***\"\"\"");
                index += 3;
                while (index < text.Length && !text.AsSpan(index).StartsWith("\"\"\"")) index++;
                if (index < text.Length) index += 3;
                continue;
            }
            if (text.AsSpan(index).StartsWith("'''"))
            {
                result.Append("'''***'''");
                index += 3;
                while (index < text.Length && !text.AsSpan(index).StartsWith("'''")) index++;
                if (index < text.Length) index += 3;
                continue;
            }
            if (character == '"')
            {
                inBasic = true;
                result.Append(character);
                index++;
                continue;
            }
            if (character == '\'')
            {
                inLiteral = true;
                result.Append(character);
                index++;
                continue;
            }
            result.Append(character);
            index++;
        }
        return result.ToString();
    }

    private static string RedactLineValues(string text)
        => string.Join("\n", text.Split('\n').Select(RedactLine));

    private static string RedactLine(string raw)
    {
        var quoted = new StringBuilder();
        var index = 0;
        while (index < raw.Length)
        {
            var character = raw[index];
            if (character is '"' or '\'')
            {
                quoted.Append(character).Append("***");
                var quote = character;
                index++;
                var escaped = false;
                while (index < raw.Length)
                {
                    var inner = raw[index];
                    index++;
                    if (escaped) { escaped = false; continue; }
                    if (inner == '\\' && quote == '"') { escaped = true; continue; }
                    if (inner == quote) break;
                }
                quoted.Append(quote);
                continue;
            }
            quoted.Append(character);
            index++;
        }
        var text = quoted.ToString();
        var equals = text.IndexOf('=');
        if (equals < 0) return text;
        var cursor = equals + 1;
        while (cursor < text.Length && (text[cursor] == ' ' || text[cursor] == '\t')) cursor++;
        if (cursor >= text.Length) return text;
        if (text[cursor] is '"' or '\'' or '[' or '{' or '#') return text;
        var comment = text.IndexOf('#', cursor);
        if (comment < 0) comment = text.Length;
        return text[..cursor] + "***" + text[comment..];
    }
}
