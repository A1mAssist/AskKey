using System.Text;

namespace AskKey.Vault;

public enum EnvFileFormatErrorKind
{
    InvalidLine,
    InvalidKey,
    DuplicateKey,
    UnterminatedQuote,
    InvalidEscape,
    TrailingCharacters,
    MultilineValue,
}

public sealed class EnvFileFormatException : Exception
{
    public EnvFileFormatErrorKind Kind { get; }
    public int Line { get; }

    public EnvFileFormatException(EnvFileFormatErrorKind kind, int line) : base(MessageFor(kind))
    {
        Kind = kind;
        Line = line;
    }

    public string LocalizationKey => Kind switch
    {
        EnvFileFormatErrorKind.InvalidLine => "env.import.invalidLine",
        EnvFileFormatErrorKind.InvalidKey => "env.import.invalidKey",
        EnvFileFormatErrorKind.DuplicateKey => "env.import.duplicateKey",
        EnvFileFormatErrorKind.UnterminatedQuote => "env.import.unterminatedQuote",
        EnvFileFormatErrorKind.InvalidEscape => "env.import.invalidEscape",
        EnvFileFormatErrorKind.TrailingCharacters => "env.import.trailingCharacters",
        _ => "env.import.multilineValue",
    };

    private static string MessageFor(EnvFileFormatErrorKind kind) => kind switch
    {
        EnvFileFormatErrorKind.InvalidLine => "The .env file contains an invalid assignment.",
        EnvFileFormatErrorKind.InvalidKey => "The .env file contains an invalid variable name.",
        EnvFileFormatErrorKind.DuplicateKey => "The .env file contains a duplicate variable name.",
        EnvFileFormatErrorKind.UnterminatedQuote => "The .env file contains an unterminated quoted value.",
        EnvFileFormatErrorKind.InvalidEscape => "The .env file contains an unsupported escape sequence.",
        EnvFileFormatErrorKind.TrailingCharacters => "The .env file contains unexpected text after a quoted value.",
        _ => "Multiline quoted values are not supported in .env imports.",
    };
}

public static class EnvFileFormat
{
    public static string Line(string name, string value)
    {
        var escaped = value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
        return $"{name}=\"{escaped}\"";
    }

    /// <summary>Legacy non-throwing entry point: malformed input yields no partial result.</summary>
    public static IReadOnlyList<(string Name, string Value)> Parse(string content)
    {
        try
        {
            return ParseLines(content, skipLegacyKeylessLines: true, validateKeys: false);
        }
        catch (EnvFileFormatException)
        {
            return Array.Empty<(string, string)>();
        }
    }

    public static IReadOnlyList<(string Name, string Value)> ParseValidated(string content)
        => ParseLines(content, skipLegacyKeylessLines: false, validateKeys: true);

    private static List<(string Name, string Value)> ParseLines(string content, bool skipLegacyKeylessLines,
        bool validateKeys)
    {
        var result = new List<(string, string)>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        // Swift splits on .newlines; "\r\n" is one separator there.
        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        for (var offset = 0; offset < lines.Length; offset++)
        {
            var lineNumber = offset + 1;
            var line = lines[offset].Trim(' ', '\t');
            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (line.StartsWith("export", StringComparison.Ordinal))
            {
                var suffix = line["export".Length..];
                if (suffix.Length > 0 && char.IsWhiteSpace(suffix[0]))
                {
                    line = suffix.Trim(' ', '\t');
                }
            }

            var equals = line.IndexOf('=');
            if (equals < 0)
            {
                if (skipLegacyKeylessLines) continue;
                throw new EnvFileFormatException(EnvFileFormatErrorKind.InvalidLine, lineNumber);
            }

            var key = line[..equals].Trim(' ', '\t');
            if (key.Length == 0)
            {
                if (skipLegacyKeylessLines) continue;
                throw new EnvFileFormatException(EnvFileFormatErrorKind.InvalidKey, lineNumber);
            }
            if (validateKeys && !IsValidKey(key))
            {
                throw new EnvFileFormatException(EnvFileFormatErrorKind.InvalidKey, lineNumber);
            }
            if (!seenKeys.Add(key))
            {
                throw new EnvFileFormatException(EnvFileFormatErrorKind.DuplicateKey, lineNumber);
            }

            var value = ParseValue(line[(equals + 1)..], lineNumber, offset + 1 < lines.Length);
            result.Add((key, value));
        }
        return result;
    }

    private static string ParseValue(string rawValue, int line, bool hasFollowingLine)
    {
        var leadingTrimmed = rawValue.TrimStart(' ', '\t');
        if (leadingTrimmed.Length == 0) return "";
        var first = leadingTrimmed[0];

        if (first == '#' && rawValue.Length > 0 && char.IsWhiteSpace(rawValue[0])) return "";

        if (first is '"' or '\'') return ParseQuoted(leadingTrimmed, first, line, hasFollowingLine);

        var bare = StripTrailingComment(leadingTrimmed);
        return bare.Trim(' ', '\t');
    }

    private static string ParseQuoted(string source, char quote, int line, bool hasFollowingLine)
    {
        var index = 1;
        var result = new StringBuilder();

        while (index < source.Length)
        {
            var character = source[index];
            if (character == quote)
            {
                var tail = source[(index + 1)..].Trim(' ', '\t');
                if (tail.Length == 0 || tail[0] == '#') return result.ToString();
                throw new EnvFileFormatException(EnvFileFormatErrorKind.TrailingCharacters, line);
            }

            if (quote == '"' && character == '\\')
            {
                if (index + 1 >= source.Length)
                {
                    throw new EnvFileFormatException(EnvFileFormatErrorKind.InvalidEscape, line);
                }
                index++;
                switch (source[index])
                {
                    case '\\': result.Append('\\'); break;
                    case '"': result.Append('"'); break;
                    case 'n': result.Append('\n'); break;
                    case 'r': result.Append('\r'); break;
                    case 't': result.Append('\t'); break;
                    default: throw new EnvFileFormatException(EnvFileFormatErrorKind.InvalidEscape, line);
                }
            }
            else
            {
                result.Append(character);
            }
            index++;
        }

        if (hasFollowingLine)
        {
            throw new EnvFileFormatException(EnvFileFormatErrorKind.MultilineValue, line);
        }
        throw new EnvFileFormatException(EnvFileFormatErrorKind.UnterminatedQuote, line);
    }

    private static string StripTrailingComment(string value)
    {
        for (var index = 1; index < value.Length; index++)
        {
            if (value[index] == '#' && char.IsWhiteSpace(value[index - 1])) return value[..index];
        }
        return value;
    }

    private static bool IsValidKey(string key)
    {
        if (key.Length == 0) return false;
        var first = key[0];
        if (first != '_' && !char.IsAsciiLetter(first)) return false;
        foreach (var character in key.AsSpan(1))
        {
            if (character != '_' && !char.IsAsciiLetterOrDigit(character)) return false;
        }
        return true;
    }
}
