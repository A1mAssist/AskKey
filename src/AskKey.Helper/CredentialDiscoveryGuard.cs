using System.Text.RegularExpressions;

namespace AskKey.Helper;

/// <summary>
/// A per-turn discovery reminder. This never reads the vault, executes a
/// command, or grants credential access; the Broker remains the authority.
/// </summary>
internal sealed class CredentialDiscoveryGuard
{
    private readonly record struct Turn(string Session, string TurnId);

    private readonly HashSet<Turn> _catalogAttempts = new();
    private readonly Dictionary<string, Turn> _pendingCatalogAttempts = new(StringComparer.Ordinal);
    private readonly List<string> _pendingOrder = new();
    private readonly List<Turn> _order = new();
    private readonly object _lock = new();

    public void CatalogAttemptFinished(string? token)
    {
        if (token is null) return;
        lock (_lock)
        {
            if (!_pendingCatalogAttempts.Remove(token, out var turn)) return;
            _pendingOrder.RemoveAll(value => value == token);
            if (!_catalogAttempts.Add(turn)) return;
            _order.Add(turn);
            if (_order.Count > 1024)
            {
                var oldest = _order[0];
                _order.RemoveAt(0);
                _catalogAttempts.Remove(oldest);
            }
        }
    }

    public global::System.Text.Json.Nodes.JsonObject Response(
        global::System.Text.Json.Nodes.JsonObject arguments)
    {
        if (String(arguments, "session_id") is not { Length: > 0 } session
            || String(arguments, "turn_id") is not { Length: > 0 } turnId
            || String(arguments, "tool_name") is not { } tool)
        {
            return new global::System.Text.Json.Nodes.JsonObject();
        }

        var turn = new Turn(session, turnId);
        lock (_lock)
        {
            if (tool == "mcp__askkey__list_credentials")
            {
                if (_catalogAttempts.Contains(turn))
                {
                    return new global::System.Text.Json.Nodes.JsonObject();
                }
                var token = Guid.NewGuid().ToString();
                _pendingCatalogAttempts[token] = turn;
                _pendingOrder.Add(token);
                if (_pendingOrder.Count > 1024)
                {
                    var oldest = _pendingOrder[0];
                    _pendingOrder.RemoveAt(0);
                    _pendingCatalogAttempts.Remove(oldest);
                }
                var input = Object(arguments, "tool_input")?.DeepClone()
                    as global::System.Text.Json.Nodes.JsonObject
                    ?? new global::System.Text.Json.Nodes.JsonObject();
                input["discovery_token"] = token;
                return new global::System.Text.Json.Nodes.JsonObject
                {
                    ["hookSpecificOutput"] = new global::System.Text.Json.Nodes.JsonObject
                    {
                        ["hookEventName"] = "PreToolUse",
                        ["permissionDecision"] = "allow",
                        ["updatedInput"] = input,
                    },
                };
            }

            if (tool != "Bash" || _catalogAttempts.Contains(turn)) return new global::System.Text.Json.Nodes.JsonObject();
            var command = String(Object(arguments, "tool_input"), "command");
            if (command is null || !IsSshConnection(command)) return new global::System.Text.Json.Nodes.JsonObject();
            return new global::System.Text.Json.Nodes.JsonObject
            {
                ["hookSpecificOutput"] = new global::System.Text.Json.Nodes.JsonObject
                {
                    ["hookEventName"] = "PreToolUse",
                    ["permissionDecision"] = "deny",
                    ["permissionDecisionReason"] = Reminder,
                },
            };
        }
    }

    public const string Reminder =
        "This SSH command has NOT run. Before the first SSH connection in this turn, discover AskKey's list_credentials tool and query its credential catalog. " +
        "In Code Mode, search ALL_TOOLS for askkey/list_credentials, then call the discovered tool. Catalog lookup returns metadata, not secret values. " +
        "If a credential matches the user's authorized task, follow its usage metadata and AskKey's run instructions; use the CLI with --wait-for-approval when command output is needed. " +
        "If the catalog is unavailable or no credential matches, continue with an appropriate existing connection method. " +
        "Do not ask the user to paste a password before checking the catalog. " +
        "This reminder does not authorize any connection or grant credential access.";

    public static bool IsSshConnection(string command, int depth = 0)
    {
        if (depth >= 4 || ShellCommands(command) is not { } commands) return false;
        foreach (var raw in commands)
        {
            var words = raw;
            while (words.Count > 0 && Regex.IsMatch(words[0], "^[A-Za-z_][A-Za-z_0-9]*="))
            {
                words.RemoveAt(0);
            }
            if (words.Count > 0 && Path.GetFileName(words[0]) == "env")
            {
                words.RemoveAt(0);
                while (words.Count > 0 && Regex.IsMatch(words[0], "^[A-Za-z_][A-Za-z_0-9]*="))
                {
                    words.RemoveAt(0);
                }
            }
            if (words.Count == 0) continue;
            var executable = Path.GetFileName(words[0]);
            if (executable is "sh" or "bash" or "zsh" && words.Count >= 3
                && words[1] is "-c" or "-lc")
            {
                if (IsSshConnection(words[2], depth + 1)) return true;
                continue;
            }
            if (executable != "ssh") continue;
            // Interpret only SSH options before the destination, never words in
            // the remote command. -G, -Q and -V do not connect to a host.
            const string takesValue = "BbcDEeFIiJLlmOopQRSWw";
            var index = 1;
            while (index < words.Count)
            {
                var word = words[index];
                if (word == "--") return index + 1 < words.Count;
                if (!word.StartsWith('-')) return word.Length > 0;
                var flags = word[1..];
                for (var offset = 0; offset < flags.Length; offset++)
                {
                    var flag = flags[offset];
                    if (flag is 'G' or 'Q' or 'V') return false;
                    if (takesValue.Contains(flag, StringComparison.Ordinal))
                    {
                        if (offset == flags.Length - 1) index++;
                        break;
                    }
                }
                index++;
            }
        }
        return false;
    }

    /// <summary>
    /// Recognize common literal shell commands without running a shell. Shell
    /// substitutions and input redirects are deliberately left alone. This is a
    /// routing reminder, not a shell security parser.
    /// </summary>
    private static List<List<string>>? ShellCommands(string command)
    {
        var commands = new List<List<string>>();
        var words = new List<string>();
        var word = new global::System.Text.StringBuilder();
        var started = false;
        char? quote = null;
        var escaped = false;

        void FinishWord()
        {
            if (!started) return;
            words.Add(word.ToString());
            word.Clear();
            started = false;
        }

        void FinishCommand()
        {
            FinishWord();
            if (words.Count == 0) return;
            commands.Add(words);
            words = new List<string>();
        }

        foreach (var character in command)
        {
            if (escaped)
            {
                word.Append(character);
                started = true;
                escaped = false;
                continue;
            }
            if (character == '\\' && quote != '\'')
            {
                escaped = true;
                continue;
            }
            if (quote is { } currentQuote)
            {
                if (character == currentQuote)
                {
                    quote = null;
                }
                else
                {
                    if (currentQuote == '"' && character == '`') return null;
                    word.Append(character);
                }
                continue;
            }
            if (character is '\'' or '"')
            {
                quote = character;
                started = true;
            }
            else if ("<`()".Contains(character))
            {
                return null;
            }
            else if (character == '>')
            {
                FinishWord();
                words.Add(">");
            }
            else if (character == '#' && !started)
            {
                return null;
            }
            else if (";|&\n".Contains(character))
            {
                FinishCommand();
            }
            else if (char.IsWhiteSpace(character))
            {
                FinishWord();
            }
            else
            {
                word.Append(character);
                started = true;
            }
        }
        if (quote is not null || escaped) return null;
        FinishCommand();
        return commands;
    }

    private static string? String(global::System.Text.Json.Nodes.JsonObject? node, string name)
        => node?[name] is global::System.Text.Json.Nodes.JsonValue value
            && value.TryGetValue<string>(out var text)
                ? text
                : null;

    private static global::System.Text.Json.Nodes.JsonObject? Object(
        global::System.Text.Json.Nodes.JsonObject node,
        string name)
        => node[name] as global::System.Text.Json.Nodes.JsonObject;
}
