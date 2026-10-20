using System.Text;

namespace AskKey.Integrations;

/// <summary>
/// Byte-preserving editor for Codex's user config.toml. It parses only enough TOML
/// to find table boundaries and validate duplicates, then rewrites nothing except
/// the Ask Key server table.
/// </summary>
internal static partial class CodexAskKeyTOML
{
    /// <summary>Single-pass TOML lexer state. Depth rides with the state so
    /// multi-line strings inside collections resume at the right nesting.</summary>
    internal readonly struct Scan : IEquatable<Scan>
    {
        internal enum ScanKind
        {
            Normal,
            BasicMultiLine,
            LiteralMultiLine,
            Collections,
        }

        private Scan(ScanKind kind, int brackets, int braces)
        {
            Kind = kind;
            Brackets = brackets;
            Braces = braces;
        }

        public ScanKind Kind { get; }
        public int Brackets { get; }
        public int Braces { get; }

        public static Scan Normal { get; } = new(ScanKind.Normal, 0, 0);

        public static Scan Resume(int brackets, int braces)
            => brackets == 0 && braces == 0 ? Normal : new(ScanKind.Collections, brackets, braces);

        public static Scan Multi(ScanKind kind, int brackets, int braces) => new(kind, brackets, braces);

        public bool IsNormal => Kind == ScanKind.Normal;

        public bool Equals(Scan other)
            => Kind == other.Kind && Brackets == other.Brackets && Braces == other.Braces;

        public override bool Equals(object? obj) => obj is Scan other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Kind, Brackets, Braces);

        public static bool operator ==(Scan left, Scan right) => left.Equals(right);

        public static bool operator !=(Scan left, Scan right) => !left.Equals(right);
    }

    internal readonly record struct Section(string Raw, string Body, TableName? Name, bool IsArray, string Newline);

    internal readonly record struct TableName(string[] Parts)
    {
        public bool IsAskKey => Parts.Length >= 2 && Parts[0] == "mcp_servers" && Parts[1] == "askkey";
        public bool IsMcpServersRoot => Parts.Length == 1 && Parts[0] == "mcp_servers";
    }

    public static void Validate(string text)
    {
        AssertBalanced(text);
        AssertAssignmentsAndKeyPaths(Sections(text));
    }

    public static string Upsert(string original, string command, IReadOnlyList<string> args)
    {
        var parts = Sections(original);
        var kept = new StringBuilder();
        foreach (var part in parts)
        {
            if (part.Name?.IsAskKey == true) continue;
            if (part.Name is null)
            {
                kept.Append(StripRootAskKeyKeys(part.Raw, part.Newline));
                continue;
            }
            if (part.Name?.IsMcpServersRoot == true)
            {
                kept.Append(StripAskKeyKeys(part.Raw, part.Newline));
                continue;
            }
            kept.Append(part.Raw);
        }
        // Sections() already rejected a file with mixed line endings, so the first
        // section's newline is the only style this document uses.
        var newline = parts.Length > 0 ? parts[0].Newline : "\n";
        var table = $"[mcp_servers.askkey]{newline}"
            + $"command = {TomlString(command)}{newline}"
            + $"args = [{string.Join(", ", args.Select(TomlString))}]";
        var body = kept.ToString();
        if (string.IsNullOrWhiteSpace(body)) return table + newline;
        while (body.EndsWith(newline + newline, StringComparison.Ordinal)) body = body[..^newline.Length];
        if (!body.EndsWith(newline, StringComparison.Ordinal)) body += newline;
        return body + newline + table + newline;
    }

    public static bool HasServer(string text, string serverName)
    {
        Validate(text);
        var target = new[] { "mcp_servers", serverName };
        foreach (var part in Sections(text))
        {
            var tablePath = part.Name?.Parts ?? Array.Empty<string>();
            if (StartsWith(tablePath, target)) return true;
            var scan = Scan.Normal;
            foreach (var line in Lines(part.Name is null ? part.Raw : part.Body, part.Newline))
            {
                if (scan.IsNormal)
                {
                    var code = CodePortion(line);
                    var key = KeyName(code);
                    if (key is not null && DottedParts(key) is { } keyPath)
                    {
                        var path = tablePath.Concat(keyPath).ToArray();
                        if (StartsWith(path, target)) return true;
                        if (path.Length == 1 && path[0] == "mcp_servers")
                        {
                            var equals = code.IndexOf('=');
                            if (equals >= 0)
                            {
                                var value = code[(equals + 1)..];
                                if (InlineAssignmentPaths(value).Any(p => p.Length > 0 && p[0] == serverName))
                                {
                                    return true;
                                }
                            }
                        }
                    }
                }
                scan = Advance(scan, line);
            }
        }
        return false;
    }

    public static (string Command, string[] Args, bool Enabled)? AskKey(string text)
    {
        var parts = Sections(text);
        string? command = null;
        string[]? args = null;
        var enabled = true;
        foreach (var part in parts)
        {
            if (part.Name?.IsAskKey == true && part.Name.Value.Parts.Length == 2)
            {
                foreach (var line in Lines(part.Body, part.Newline))
                {
                    var code = CodePortion(line).Trim();
                    if (code.StartsWith("command", StringComparison.Ordinal))
                    {
                        command = ScalarValue(code);
                    }
                    else if (code.StartsWith("args", StringComparison.Ordinal))
                    {
                        args = ArrayValue(code);
                    }
                    else if (KeyName(code) is { } name && DottedParts(name) is ["enabled"])
                    {
                        enabled = ScalarValue(code) == "true";
                    }
                }
            }
            if (part.Name?.IsMcpServersRoot == true)
            {
                var stripped = StripAskKeyKeys(part.Raw, part.Newline);
                if (stripped != part.Raw)
                {
                    foreach (var line in Lines(part.Body, part.Newline))
                    {
                        var code = CodePortion(line).Trim();
                        if (code.StartsWith("askkey", StringComparison.Ordinal)
                            || code.StartsWith("\"askkey\"", StringComparison.Ordinal))
                        {
                            if (InlineTable(code) is { } inline)
                            {
                                if (inline.TryGetValue("command", out var inlineCommand)) command = inlineCommand;
                                if (inline.TryGetValue("args", out var rawArgs)) args = ArrayLiteral(rawArgs);
                                if (inline.TryGetValue("enabled", out var rawEnabled)) enabled = rawEnabled == "true";
                            }
                        }
                    }
                }
            }
        }
        return command is not null && args is not null ? (command, args, enabled) : null;
    }

    public static bool PreservesNonAskKey(string original, string current)
    {
        static string Joined(IEnumerable<Section> parts)
            => string.Concat(parts.Where(p => p.Name?.IsAskKey != true).Select(p => p.Raw)).Trim();

        return Joined(Sections(original)) == Joined(Sections(current));
    }

    private static bool StartsWith(IReadOnlyList<string> path, IReadOnlyList<string> prefix)
    {
        if (path.Count < prefix.Count) return false;
        for (var index = 0; index < prefix.Count; index++)
        {
            if (path[index] != prefix[index]) return false;
        }
        return true;
    }

    /// <summary>Splits only this inline table's assignments. Commas inside strings,
    /// nested tables and arrays cannot create a sibling server entry.</summary>
    private static string[][] InlineAssignmentPaths(string raw)
    {
        var value = raw.Trim();
        if (value.Length < 2 || value[0] != '{' || value[^1] != '}') return Array.Empty<string[]>();
        var pieces = new List<string>();
        var piece = new StringBuilder();
        var depth = 0;
        char? quote = null;
        var escaped = false;
        foreach (var character in value[1..^1])
        {
            if (quote is { } current)
            {
                piece.Append(character);
                if (escaped) escaped = false;
                else if (character == '\\' && current == '"') escaped = true;
                else if (character == current) quote = null;
            }
            else if (character is '"' or '\'')
            {
                quote = character;
                piece.Append(character);
            }
            else if (character is '{' or '[')
            {
                depth++;
                piece.Append(character);
            }
            else if (character is '}' or ']')
            {
                depth--;
                piece.Append(character);
            }
            else if (character == ',' && depth == 0)
            {
                pieces.Add(piece.ToString());
                piece.Clear();
            }
            else
            {
                piece.Append(character);
            }
        }
        pieces.Add(piece.ToString());
        return pieces
            .Select(assignment => KeyName(assignment) is { } key ? DottedParts(key) : null)
            .Where(parts => parts is not null)
            .Select(parts => parts!)
            .ToArray();
    }

    public static string TomlString(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}

