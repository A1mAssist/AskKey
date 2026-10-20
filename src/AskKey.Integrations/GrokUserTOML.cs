using System.Text;

namespace AskKey.Integrations;

public enum GrokTomlTransportKind
{
    Missing,
    Remote,
    Stdio,
    Other,
}

public readonly record struct GrokTomlTransport(
    GrokTomlTransportKind Kind, string? Command = null, string[]? Args = null)
{
    public bool IsStdio(string command, string[] args)
        => Kind == GrokTomlTransportKind.Stdio
            && Command == command
            && Args is not null
            && Args.SequenceEqual(args, StringComparer.Ordinal);
}

/// <summary>
/// Table-level editor for Grok's user <c>config.toml</c>. It understands only
/// enough TOML to find table boundaries and to refuse shapes it cannot edit
/// safely, then splices the Ask Key table in place. Every byte outside the
/// block it owns is returned exactly as it came in.
/// </summary>
internal static class GrokUserTOML
{
    /// <summary>One table, plus the byte range it occupies in the source text so
    /// the writer can splice instead of rebuilding.</summary>
    internal readonly record struct Table(string Header, string Body, int Start, int Length);

    public static List<Table> Parse(string text)
    {
        if (text.Contains('\0')) throw Invalid();
        var tables = new List<Table>();
        var header = "";
        var start = 0;
        var cursor = 0;
        var arrayDepth = 0;
        var objectDepth = 0;
        var inMultilineBasic = false;
        var inMultilineLiteral = false;
        var sawHeader = false;
        var headerStart = 0;
        foreach (var rawLine in SplitLines(text))
        {
            if (inMultilineBasic)
            {
                if (CountUnescaped(rawLine.Text, "\"\"\"") % 2 == 1) inMultilineBasic = false;
                cursor = rawLine.End;
                continue;
            }
            if (inMultilineLiteral)
            {
                if (rawLine.Text.Contains("'''", StringComparison.Ordinal)) inMultilineLiteral = false;
                cursor = rawLine.End;
                continue;
            }
            var trimmed = TrimComment(rawLine.Text);
            if (arrayDepth == 0 && objectDepth == 0 && TableHeader(trimmed) is { } next)
            {
                // Settle the region before this header: the preamble keys when no
                // header has been seen yet, otherwise the previous table.
                var pending = text[start..rawLine.Start];
                if (!sawHeader)
                {
                    // Preamble keys: only a real one is worth keeping.
                    if (pending.Trim().Length > 0)
                    {
                        tables.Add(new Table("", pending, start, pending.Length));
                    }
                }
                else if (header.Length > 0 || pending.Trim().Length > 0)
                {
                    // The span reaches back over the header line so replacing the
                    // table also replaces its "[mcp_servers.x]" line.
                    tables.Add(new Table(header, pending, headerStart, rawLine.Start - headerStart));
                }
                header = next;
                sawHeader = true;
                headerStart = rawLine.Start;
                start = rawLine.End;
                cursor = rawLine.End;
                continue;
            }
            if (arrayDepth == 0 && objectDepth == 0
                && trimmed.Length > 0
                && !trimmed.Contains('=', StringComparison.Ordinal)
                && !trimmed.StartsWith('['))
            {
                throw Invalid();
            }
            if (arrayDepth == 0 && objectDepth == 0 && trimmed.IndexOf('=') is var index and >= 0)
            {
                var value = trimmed[(index + 1)..].Trim();
                if (value.StartsWith("[[", StringComparison.Ordinal)) throw Invalid();
                if (value.Contains("\"\"\"", StringComparison.Ordinal))
                {
                    inMultilineBasic = CountUnescaped(value, "\"\"\"") % 2 == 1;
                }
                if (value.Contains("'''", StringComparison.Ordinal)) inMultilineLiteral = true;
            }
            arrayDepth += Count(trimmed, '[') - Count(trimmed, ']');
            objectDepth += Count(trimmed, '{') - Count(trimmed, '}');
            if (arrayDepth < 0 || objectDepth < 0) throw Invalid();
            cursor = rawLine.End;
        }
        if (inMultilineBasic || inMultilineLiteral || arrayDepth != 0 || objectDepth != 0)
        {
            throw Invalid();
        }
        // A header with an empty body is still a real table this tool may own.
        if (header.Length > 0)
        {
            tables.Add(new Table(header, text[start..cursor], headerStart, cursor - headerStart));
        }
        else if (text[start..cursor].Trim().Length > 0)
        {
            tables.Add(new Table("", text[start..cursor], start, cursor - start));
        }
        return tables;
    }

    public static string UpsertStdio(
        string text, string serverName, string command, IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> env)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var section = RenderStdioSection(newline, serverName, command, args, env);
        var spans = OwnedSpans(text, serverName);

        if (spans.Count == 0)
        {
            if (text.Length == 0) return section;
            var prefix = text.EndsWith(newline, StringComparison.Ordinal) ? text : text + newline;
            var separator = prefix.EndsWith(newline + newline, StringComparison.Ordinal) ? "" : newline;
            return prefix + separator + section;
        }

        // Minimal splice: cut every block this tool owns and drop the new one in
        // the first one's place. Everything outside the cut is returned verbatim.
        var builder = new StringBuilder(text);
        var removed = 0;
        foreach (var (start, length) in spans)
        {
            builder.Remove(start - removed, length);
            removed += length;
        }
        // spans[0].Start is where the first owned block began, and every deletion
        // starts at or after it, so the offset is still valid after the removals.
        builder.Insert(spans[0].Start, section);
        return builder.ToString();
    }

    /// <summary>
    /// Byte ranges this tool owns: the server's own table and any child table
    /// beneath it. A server declared as a dotted key or as a member of an inline
    /// <c>mcp_servers</c> table cannot be replaced without rewriting that line,
    /// so those shapes are refused rather than duplicated.
    /// </summary>
    private static List<(int Start, int Length)> OwnedSpans(string text, string serverName)
    {
        var spans = new List<(int Start, int Length)>();
        var exact = $"mcp_servers.{serverName}";
        var childPrefix = exact + ".";
        foreach (var table in Parse(text))
        {
            if (table.Header == exact || table.Header.StartsWith(childPrefix, StringComparison.Ordinal))
            {
                spans.Add((table.Start, table.Length));
                continue;
            }
            if (table.Header.Length == 0) RejectUneditableRoot(text, table, serverName);
        }
        return Merge(spans);
    }

    private static void RejectUneditableRoot(string text, Table root, string serverName)
    {
        foreach (var line in root.Body.Split('\n'))
        {
            var code = TrimComment(line).Trim();
            var equals = code.IndexOf('=');
            if (equals < 0) continue;
            var key = code[..equals].Trim();
            var value = code[(equals + 1)..];
            if (key == $"mcp_servers.{serverName}") throw Invalid();
            if (key == "mcp_servers" && MentionsServer(value, serverName)) throw Invalid();
        }
    }

    /// <summary>True when an inline table names the server as a top-level member.</summary>
    private static bool MentionsServer(string value, string serverName)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith('{') || !trimmed.EndsWith('}')) return false;
        var inner = trimmed[1..^1];
        var depth = 0;
        var start = 0;
        for (var index = 0; index <= inner.Length; index++)
        {
            if (index < inner.Length)
            {
                var character = inner[index];
                if (character is '{' or '[') { depth++; continue; }
                if (character is '}' or ']') { depth--; continue; }
                if (character != ',' || depth != 0) continue;
            }
            var member = inner[start..index].Trim();
            if (member.Length > 0
                && member.AsSpan().StartsWith(serverName)
                && (member.Length == serverName.Length || !char.IsLetterOrDigit(member[serverName.Length])))
            {
                return true;
            }
            start = index + 1;
        }
        return false;
    }

    private static List<(int Start, int Length)> Merge(List<(int Start, int Length)> spans)
    {
        if (spans.Count < 2) return spans;
        var merged = new List<(int Start, int Length)> { spans[0] };
        foreach (var span in spans.Skip(1))
        {
            var previous = merged[^1];
            if (span.Start <= previous.Start + previous.Length)
            {
                merged[^1] = (previous.Start, span.Start + span.Length - previous.Start);
            }
            else
            {
                merged.Add(span);
            }
        }
        return merged;
    }

    private static string RenderStdioSection(
        string newline, string serverName, string command,
        IReadOnlyList<string> args, IReadOnlyDictionary<string, string> env)
    {
        var section = new StringBuilder()
            .Append('[').Append("mcp_servers").Append('.').Append(serverName).Append(']').Append(newline)
            .Append("command = ").Append(Quote(command)).Append(newline)
            .Append("args = [").Append(string.Join(", ", args.Select(Quote))).Append(']').Append(newline)
            .Append("enabled = true");
        if (env.Count > 0)
        {
            var pairs = env.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key} = {Quote(pair.Value)}");
            section.Append(newline).Append("env = { ").Append(string.Join(", ", pairs)).Append(" }");
        }
        return section.Append(newline).ToString();
    }
    /// <summary>Lines with their byte ranges, so a splice can address the source
    /// directly instead of guessing offsets from reconstructed text.</summary>
    private static List<(string Text, int Start, int End)> SplitLines(string text)
    {
        var lines = new List<(string, int, int)>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '\n') continue;
            lines.Add((text[start..index], start, index + 1));
            start = index + 1;
        }
        if (start < text.Length) lines.Add((text[start..], start, text.Length));
        return lines;
    }
    public static GrokTomlTransport AskKeyTransport(string text, string serverName = "askkey")
    {
        List<Table> tables;
        try { tables = Parse(text); }
        catch { return new GrokTomlTransport(GrokTomlTransportKind.Other); }
        var header = $"mcp_servers.{serverName}";
        var table = tables.FirstOrDefault(candidate => candidate.Header == header);
        if (table.Header is null) return new GrokTomlTransport(GrokTomlTransportKind.Missing);
        string? command = null;
        var remote = false;
        var args = new List<string>();
        var collectingArgs = false;
        var argsRaw = "";
        foreach (var raw in table.Body.Split('\n'))
        {
            var trimmed = TrimComment(raw);
            if (collectingArgs)
            {
                argsRaw += trimmed;
                if (trimmed.Contains(']', StringComparison.Ordinal))
                {
                    collectingArgs = false;
                    args = ArrayValue(argsRaw);
                }
                continue;
            }
            var equals = trimmed.IndexOf('=');
            if (equals < 0) continue;
            var key = trimmed[..equals].Trim();
            var value = trimmed[(equals + 1)..].Trim();
            if (key == "url") remote = true;
            if (key == "command") command = Unquote(value);
            if (key == "args")
            {
                argsRaw = value;
                if (value.Contains(']', StringComparison.Ordinal)) args = ArrayValue(value);
                else collectingArgs = true;
            }
        }
        if (remote) return new GrokTomlTransport(GrokTomlTransportKind.Remote);
        return command is null
            ? new GrokTomlTransport(GrokTomlTransportKind.Other)
            : new GrokTomlTransport(GrokTomlTransportKind.Stdio, command, args.ToArray());
    }

    public static string RedactedDiff(string before, string after)
        => Redact(before) == Redact(after)
            ? ""
            : $"before:\n{Redact(before)}\n after:\n{Redact(after)}\n";

    private static string? TableHeader(string trimmed)
        => trimmed.StartsWith('[') && trimmed.EndsWith(']') && !trimmed.StartsWith("[[", StringComparison.Ordinal)
            ? trimmed[1..^1]
            : null;

    private static List<string> ArrayValue(string raw)
    {
        var trimmed = raw.Trim('[', ']', ' ');
        if (trimmed.Length == 0) return new List<string>();
        return trimmed.Split(',').Select(item => Unquote(item.Trim())).ToList();
    }

    private static string Unquote(string raw)
    {
        var value = raw.Trim();
        if (value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"'))
        {
            value = value[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        }
        return value;
    }

    private static string Quote(string raw)
        => "\"" + raw.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string TrimComment(string line)
    {
        var inString = false;
        var escaped = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (escaped) { escaped = false; continue; }
            if (character == '\\' && inString) { escaped = true; continue; }
            if (character == '"') { inString = !inString; continue; }
            if (character == '#' && !inString) return line[..index].Trim();
        }
        return line.Trim();
    }

    private static int Count(string text, char character)
    {
        var inString = false;
        var escaped = false;
        var total = 0;
        foreach (var item in text)
        {
            if (escaped) { escaped = false; continue; }
            if (item == '\\' && inString) { escaped = true; continue; }
            if (item == '"') { inString = !inString; continue; }
            if (!inString && item == character) total++;
        }
        return total;
    }

    private static int CountUnescaped(string text, string delimiter)
        => text.Split(delimiter).Length - 1;

    private static string Redact(string text)
    {
        var insideEnvironmentTable = false;
        var inlineEnvironmentDepth = 0;
        return string.Join("\n", text.Split('\n').Select(line =>
        {
            var lower = line.ToLowerInvariant();
            if (inlineEnvironmentDepth > 0)
            {
                var structuralLine = TrimComment(line);
                inlineEnvironmentDepth += Count(structuralLine, '{') - Count(structuralLine, '}');
                inlineEnvironmentDepth = Math.Max(0, inlineEnvironmentDepth);
                return "";
            }
            if (TableHeader(TrimComment(line))?.ToLowerInvariant() is { } header)
            {
                insideEnvironmentTable = header.EndsWith(".env", StringComparison.Ordinal)
                    || header.Contains(".env.", StringComparison.Ordinal)
                    || header.EndsWith(".headers", StringComparison.Ordinal)
                    || header.Contains(".headers.", StringComparison.Ordinal);
                return line;
            }
            var equals = line.IndexOf('=');
            if (equals >= 0)
            {
                var key = line[..equals].Trim().ToLowerInvariant();
                var normalizedKey = new string(key.Where(char.IsLetterOrDigit).ToArray());
                if (key == "env" || normalizedKey == "headers")
                {
                    var structuralValue = TrimComment(line[(equals + 1)..]);
                    inlineEnvironmentDepth = Math.Max(
                        0, Count(structuralValue, '{') - Count(structuralValue, '}'));
                    return line[..equals] + "= \"***\"";
                }
            }
            if (lower.Contains("authorization", StringComparison.Ordinal)
                || lower.Contains("token", StringComparison.Ordinal)
                || lower.Contains("secret", StringComparison.Ordinal)
                || lower.Contains("api_key", StringComparison.Ordinal)
                || lower.Contains("x-api-key", StringComparison.Ordinal)
                || lower.Contains("headers", StringComparison.Ordinal)
                || lower.Contains("password", StringComparison.Ordinal)
                || lower.Contains("askkey_broker_pipe", StringComparison.Ordinal)
                || lower.Contains("askkey_broker_socket", StringComparison.Ordinal)
                || lower.Contains(".env.", StringComparison.Ordinal)
                || lower.Contains(".headers.", StringComparison.Ordinal)
                || insideEnvironmentTable)
            {
                if (equals >= 0) return line[..equals] + "= \"***\"";
            }
            return line;
        }));
    }

    private static GrokCLIAdapterException Invalid()
        => new(GrokCLIAdapterError.InvalidConfig);
}