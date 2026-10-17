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
/// Lossless table-level editor for Grok's user <c>config.toml</c>. It only
/// understands enough TOML to find table boundaries, reject obviously invalid
/// input, and rewrite the Ask Key server table while preserving every other line.
/// </summary>
internal static class GrokUserTOML
{
    internal readonly record struct Table(string Header, string Body);

    public static List<Table> Parse(string text)
    {
        if (text.Contains('\0')) throw Invalid();
        var tables = new List<Table>();
        var header = "";
        var body = new List<string>();
        var arrayDepth = 0;
        var objectDepth = 0;
        var inMultilineBasic = false;
        var inMultilineLiteral = false;
        foreach (var rawLine in text.Split('\n'))
        {
            if (inMultilineBasic)
            {
                body.Add(rawLine);
                if (CountUnescaped(rawLine, "\"\"\"") % 2 == 1) inMultilineBasic = false;
                continue;
            }
            if (inMultilineLiteral)
            {
                body.Add(rawLine);
                if (rawLine.Contains("'''", StringComparison.Ordinal)) inMultilineLiteral = false;
                continue;
            }
            var trimmed = TrimComment(rawLine);
            if (arrayDepth == 0 && objectDepth == 0 && TableHeader(trimmed) is { } next)
            {
                if (header.Length > 0 || string.Join("\n", body).Trim().Length > 0)
                {
                    tables.Add(new Table(header, string.Join("\n", body)));
                }
                else if (header.Length == 0 && body.Count > 0)
                {
                    tables.Add(new Table("", string.Join("\n", body)));
                }
                header = next;
                body = new List<string>();
                continue;
            }
            if (trimmed.Length == 0)
            {
                body.Add(rawLine);
                continue;
            }
            if (arrayDepth == 0 && objectDepth == 0
                && !trimmed.Contains('=', StringComparison.Ordinal)
                && !trimmed.StartsWith('['))
            {
                throw Invalid();
            }
            if (arrayDepth == 0 && objectDepth == 0 && trimmed.IndexOf('=') is var index and >= 0)
            {
                var value = trimmed[(index + 1)..].Trim();
                if (!value.StartsWith('"') && value.Contains("[[", StringComparison.Ordinal)) throw Invalid();
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
            body.Add(rawLine);
        }
        if (inMultilineBasic || inMultilineLiteral || arrayDepth != 0 || objectDepth != 0)
        {
            throw Invalid();
        }
        if (header.Length > 0 || string.Join("\n", body).Trim().Length > 0)
        {
            tables.Add(new Table(header, string.Join("\n", body)));
        }
        return tables;
    }

    public static string UpsertStdio(
        string text, string serverName, string command, IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> env)
    {
        var tables = Parse(text);
        var exact = $"mcp_servers.{serverName}";
        var prefix = exact + ".";
        var kept = tables.Where(table => table.Header != exact && !table.Header.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();
        var rendered = string.Join("\n", kept.Select(table =>
        {
            if (table.Header.Length == 0) return table.Body;
            var heading = $"[{table.Header}]";
            var body = table.Body.Trim('\n');
            return body.Length == 0 ? heading : heading + "\n" + body;
        })).Trim('\n');
        var section = new StringBuilder()
            .Append('[').Append(exact).Append(']').Append('\n')
            .Append("command = ").Append(Quote(command)).Append('\n')
            .Append("args = [").Append(string.Join(", ", args.Select(Quote))).Append(']').Append('\n')
            .Append("enabled = true");
        if (env.Count > 0)
        {
            var pairs = env.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key} = {Quote(pair.Value)}");
            section.Append("\nenv = { ").Append(string.Join(", ", pairs)).Append(" }");
        }
        if (rendered.Length > 0) rendered += "\n\n";
        rendered += section.ToString();
        if (!rendered.EndsWith('\n')) rendered += "\n";
        return rendered;
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