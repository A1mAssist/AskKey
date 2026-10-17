using ScanKind = AskKey.Integrations.CodexAskKeyTOML.Scan.ScanKind;

using System.Text;

namespace AskKey.Integrations;

internal static partial class CodexAskKeyTOML
{
    public static Section[] Sections(string text)
    {
        if (text.Contains('\0')) throw new CodexUserMCPException(CodexUserMCPError.IllegalConfig);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var sourceLines = Lines(text, newline);
        var scan = Scan.Normal;
        var sections = new List<Section>();
        var raw = new StringBuilder();
        var body = new StringBuilder();
        TableName? header = null;
        var isArray = false;

        void Push()
        {
            sections.Add(new Section(raw.ToString(), body.ToString(), header, isArray, newline));
            raw.Clear();
            body.Clear();
            header = null;
            isArray = false;
        }

        for (var index = 0; index < sourceLines.Count; index++)
        {
            var line = sourceLines[index];
            var isLast = index == sourceLines.Count - 1;
            var suffix = isLast && !text.EndsWith(newline, StringComparison.Ordinal) ? "" : newline;
            if (scan.IsNormal)
            {
                var code = CodePortion(line).Trim();
                if (code.StartsWith('['))
                {
                    var parsed = ParseHeader(code)
                        ?? throw new CodexUserMCPException(CodexUserMCPError.IllegalConfig);
                    if (raw.Length > 0 || header is not null) Push();
                    header = parsed.Name;
                    isArray = parsed.IsArray;
                    if (parsed.IsArray && parsed.Name.IsAskKey)
                    {
                        throw new CodexUserMCPException(CodexUserMCPError.IllegalConfig);
                    }
                    raw.Append(line).Append(suffix);
                    continue;
                }
                if (code.Length > 0 && !code.Contains('=') && !code.StartsWith('#'))
                {
                    throw new CodexUserMCPException(CodexUserMCPError.IllegalConfig);
                }
            }
            raw.Append(line).Append(suffix);
            if (header is not null) body.Append(line).Append(suffix);
            scan = Advance(scan, line);
        }
        if (!scan.IsNormal) throw new CodexUserMCPException(CodexUserMCPError.IllegalConfig);
        if (raw.Length > 0 || sections.Count == 0) Push();
        return sections.ToArray();
    }

    public static void AssertBalanced(string text)
    {
        var index = 0;
        var brackets = 0;
        var braces = 0;
        var inBasic = false;
        var inLiteral = false;
        var inBasicMulti = false;
        var inLiteralMulti = false;
        var escaped = false;
        var inComment = false;
        while (index < text.Length)
        {
            var character = text[index];
            if (inComment)
            {
                if (character == '\n') inComment = false;
                index++;
                continue;
            }
            if (inBasicMulti)
            {
                if (text.AsSpan(index).StartsWith("\"\"\""))
                {
                    inBasicMulti = false;
                    index += 3;
                    continue;
                }
                index++;
                continue;
            }
            if (inLiteralMulti)
            {
                if (text.AsSpan(index).StartsWith("'''"))
                {
                    inLiteralMulti = false;
                    index += 3;
                    continue;
                }
                index++;
                continue;
            }
            if (inBasic)
            {
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == '"') inBasic = false;
                index++;
                continue;
            }
            if (inLiteral)
            {
                if (character == '\'') inLiteral = false;
                index++;
                continue;
            }
            if (character == '#')
            {
                inComment = true;
                index++;
                continue;
            }
            if (text.AsSpan(index).StartsWith("\"\"\""))
            {
                inBasicMulti = true;
                index += 3;
                continue;
            }
            if (text.AsSpan(index).StartsWith("'''"))
            {
                inLiteralMulti = true;
                index += 3;
                continue;
            }
            if (character == '"')
            {
                inBasic = true;
                index++;
                continue;
            }
            if (character == '\'')
            {
                inLiteral = true;
                index++;
                continue;
            }
            if (character == '[') brackets++;
            if (character == ']')
            {
                brackets--;
                if (brackets < 0) throw new CodexUserMCPException(CodexUserMCPError.IllegalConfig);
            }
            if (character == '{') braces++;
            if (character == '}')
            {
                braces--;
                if (braces < 0) throw new CodexUserMCPException(CodexUserMCPError.IllegalConfig);
            }
            index++;
        }
        if (inBasic || inLiteral || inBasicMulti || inLiteralMulti || brackets != 0 || braces != 0)
        {
            throw new CodexUserMCPException(CodexUserMCPError.IllegalConfig);
        }
    }

    private static (TableName Name, bool IsArray)? ParseHeader(string code)
    {
        string text;
        var isArray = false;
        if (code.StartsWith("[[", StringComparison.Ordinal) && code.EndsWith("]]", StringComparison.Ordinal))
        {
            isArray = true;
            text = code[2..^2];
        }
        else if (code.StartsWith('[') && code.EndsWith(']'))
        {
            text = code[1..^1];
        }
        else
        {
            return null;
        }
        var parts = DottedParts(text.Trim());
        if (parts is null || parts.Length == 0) return null;
        return (new TableName(parts), isArray);
    }

    public static string? KeyName(string code)
    {
        var equals = code.IndexOf('=');
        return equals < 0 ? null : code[..equals].Trim();
    }

    public static string[]? DottedParts(string text)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        foreach (var character in text)
        {
            if (quote is { } currentQuote)
            {
                if (character == currentQuote) quote = null;
                else current.Append(character);
                continue;
            }
            if (character is '"' or '\'')
            {
                quote = character;
                continue;
            }
            if (character == '.')
            {
                parts.Add(current.ToString());
                current.Clear();
                continue;
            }
            if (character == ' ') continue;
            current.Append(character);
        }
        if (quote is not null) return null;
        parts.Add(current.ToString());
        return parts.Any(string.IsNullOrEmpty) ? null : parts.ToArray();
    }

    public static string ScalarValue(string code)
    {
        var equals = code.IndexOf('=');
        if (equals < 0) throw new CodexUserMCPException(CodexUserMCPError.IllegalConfig);
        return ScalarLiteral(code[(equals + 1)..].Trim());
    }

    private static string ScalarLiteral(string raw)
    {
        if (raw.Length >= 2 && raw.StartsWith('"') && raw.EndsWith('"'))
        {
            return Unescape(raw[1..^1]);
        }
        if (raw.Length >= 2 && raw.StartsWith('\'') && raw.EndsWith('\''))
        {
            return raw[1..^1];
        }
        var comment = raw.IndexOf('#');
        if (comment >= 0) return ScalarLiteral(raw[..comment].Trim());
        return raw;
    }

    public static string[] ArrayValue(string code)
    {
        var equals = code.IndexOf('=');
        if (equals < 0) throw new CodexUserMCPException(CodexUserMCPError.IllegalConfig);
        return ArrayLiteral(code[(equals + 1)..].Trim());
    }

    public static string[] ArrayLiteral(string raw)
    {
        var text = raw;
        var comment = text.IndexOf('#');
        if (comment >= 0 && !text[..comment].Contains('"')) text = text[..comment].Trim();
        if (!text.StartsWith('[') || !text.EndsWith(']'))
        {
            throw new CodexUserMCPException(CodexUserMCPError.IllegalConfig);
        }
        var inner = text[1..^1].Trim();
        if (inner.Length == 0) return Array.Empty<string>();
        return inner.Split(',')
            .Select(piece => ScalarLiteral(piece.Trim()))
            .ToArray();
    }

    public static Dictionary<string, string>? InlineTable(string code)
    {
        var start = code.IndexOf('{');
        if (start < 0 || !code.EndsWith('}')) return null;
        var inner = code[(start + 1)..^1];
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var piece in inner.Split(','))
        {
            var item = piece.Trim();
            var equals = item.IndexOf('=');
            if (equals < 0) continue;
            values[item[..equals].Trim()] = item[(equals + 1)..].Trim();
        }
        return values;
    }

    public static List<string> Lines(string text, string newline)
    {
        if (text.Length == 0) return new List<string> { "" };
        var result = new List<string>();
        var current = new StringBuilder();
        var index = 0;
        while (index < text.Length)
        {
            if (text.AsSpan(index).StartsWith(newline))
            {
                result.Add(current.ToString());
                current.Clear();
                index += newline.Length;
            }
            else
            {
                current.Append(text[index]);
                index++;
            }
        }
        result.Add(current.ToString());
        if (text.EndsWith(newline, StringComparison.Ordinal)) result.RemoveAt(result.Count - 1);
        return result;
    }

    public static string CodePortion(string line)
    {
        char? quote = null;
        var escaped = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (quote is { } current)
            {
                if (escaped) escaped = false;
                else if (character == '\\' && current == '"') escaped = true;
                else if (character == current) quote = null;
            }
            else if (character == '#')
            {
                return line[..index];
            }
            else if (character is '"' or '\'')
            {
                quote = character;
            }
        }
        return line;
    }

    public static Scan Advance(Scan scan, string line)
    {
        var state = scan;
        var brackets = state.Brackets;
        var braces = state.Braces;
        var index = 0;
        var escaped = false;
        while (index < line.Length)
        {
            if (state.Kind == ScanKind.BasicMultiLine)
            {
                if (line.AsSpan(index).StartsWith("\"\"\""))
                {
                    state = Scan.Resume(brackets, braces);
                    index += 3;
                }
                else index++;
                continue;
            }
            if (state.Kind == ScanKind.LiteralMultiLine)
            {
                if (line.AsSpan(index).StartsWith("'''"))
                {
                    state = Scan.Resume(brackets, braces);
                    index += 3;
                }
                else index++;
                continue;
            }

            if (state.Kind == ScanKind.Normal || brackets > 0 || braces > 0)
            {
                if (line.AsSpan(index).StartsWith("\"\"\""))
                {
                    state = Scan.Multi(ScanKind.BasicMultiLine, brackets, braces);
                    index += 3;
                    continue;
                }
                if (line.AsSpan(index).StartsWith("'''"))
                {
                    state = Scan.Multi(ScanKind.LiteralMultiLine, brackets, braces);
                    index += 3;
                    continue;
                }
                var character = line[index];
                if (character == '#') break;
                if (character is '"' or '\'')
                {
                    var quote = character;
                    index++;
                    while (index < line.Length)
                    {
                        var inner = line[index];
                        if (escaped) escaped = false;
                        else if (inner == '\\' && quote == '"') escaped = true;
                        else if (inner == quote)
                        {
                            index++;
                            break;
                        }
                        index++;
                    }
                    continue;
                }
                if (character == '[') brackets++;
                else if (character == ']') brackets--;
                else if (character == '{') braces++;
                else if (character == '}') braces--;
            }
            index++;
        }
        if (state.Kind is ScanKind.BasicMultiLine or ScanKind.LiteralMultiLine)
        {
            return Scan.Multi(state.Kind, brackets, braces);
        }
        return Scan.Resume(brackets, braces);
    }
    public static int BraceDelta(string text)
        => text.Sum(character => character == '{' ? 1 : character == '}' ? -1 : 0);

    private static string Unescape(string text)
    {
        var result = new StringBuilder();
        var escaped = false;
        foreach (var character in text)
        {
            if (escaped)
            {
                result.Append(character == 'n' ? '\n' : character);
                escaped = false;
            }
            else if (character == '\\')
            {
                escaped = true;
            }
            else
            {
                result.Append(character);
            }
        }
        return result.ToString();
    }
}




