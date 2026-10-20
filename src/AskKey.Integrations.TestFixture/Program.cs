using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

Console.OutputEncoding = new UTF8Encoding(false);
Console.InputEncoding = new UTF8Encoding(false);

// One PE fixture serves as askkey.exe, claude.exe and grok.exe in tests.
// It only reads and writes the synthetic directories the test hands it.
var name = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "").ToLowerInvariant();
var arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
return name switch
{
    "askkey" => Helper(arguments),
    "claude" => Claude(arguments),
    "grok" => Grok(arguments),
    _ => 64,
};

static int Helper(string[] arguments)
{
    var broker = Environment.GetEnvironmentVariable("ASKKEY_BROKER_PIPE") ?? "";
    // Any execution is observable, so tests can assert the helper never ran.
    try
    {
        File.WriteAllText(
            Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "helper-ran"),
            string.Join(' ', arguments));
    }
    catch (IOException) { }
    if (arguments.Length >= 1 && arguments[0] == "hook")
    {
        if (arguments.Length >= 2 && arguments[1] == "capabilities")
        {
            Console.Write(JsonSerializer.Serialize(new
            {
                protocolVersion = 1,
                clients = new[] { "codex", "cursor", "grok", "claude" },
            }));
            return 0;
        }
        return 64;
    }
    if (arguments.Length >= 1 && arguments[0] == "health")
    {
        var healthy = broker.StartsWith("ok-", StringComparison.Ordinal);
        Console.Write(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["success"] = new Dictionary<string, object>
            {
                ["_0"] = new Dictionary<string, object>
                {
                    ["health"] = new Dictionary<string, object>
                    {
                        ["_0"] = new Dictionary<string, object>
                        {
                            ["version"] = healthy ? 1 : 0,
                            ["status"] = healthy ? "ok" : "unavailable",
                        },
                    },
                },
            },
        }));
        return healthy ? 0 : 1;
    }
    if (arguments.Length >= 1 && arguments[0] == "mcp")
    {
        var input = Console.In.ReadToEnd();
        var requests = input.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var client = "cursor";
        var protocolVersion = "2025-11-25";
        if (requests.Length > 0)
        {
            using var document = JsonDocument.Parse(requests[0]);
            if (document.RootElement.TryGetProperty("params", out var parameters))
            {
                if (parameters.TryGetProperty("clientInfo", out var info)
                    && info.TryGetProperty("name", out var clientName))
                {
                    client = clientName.GetString() ?? client;
                }
                if (parameters.TryGetProperty("protocolVersion", out var requested)
                    && requested.ValueKind == JsonValueKind.String
                    && requested.GetString() == "2024-11-05")
                {
                    protocolVersion = "2024-11-05";
                }
            }
        }
        var initialize = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            result = new
            {
                protocolVersion,
                serverInfo = new { name = "askkey", version = "1.0.0" },
            },
        });
        var tools = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 2,
            result = new
            {
                tools = new[]
                {
                    new { name = "list_credentials" },
                    new { name = "run" },
                },
            },
        });
        Console.Write(initialize + "\n" + tools + "\n");
        _ = client;
        return 0;
    }
    return 64;
}

static int Claude(string[] arguments)
{
    var home = Environment.GetEnvironmentVariable("HOME") ?? "";
    var statePath = Path.Combine(home, "claude-state");
    var callsPath = Path.Combine(home, "claude-calls");
    File.AppendAllText(callsPath, string.Join(' ', arguments) + "\n");
    if (arguments.Length >= 1 && arguments[0] == "--version")
    {
        Console.Write(File.Exists(Path.Combine(home, "claude-version"))
            ? File.ReadAllText(Path.Combine(home, "claude-version"))
            : "2.1.282 (Claude Code)\n");
        return 0;
    }
    if (arguments.Length < 2 || arguments[0] != "mcp") return 64;
    var command = arguments[1];
    if (arguments.Length >= 3 && arguments[^1] == "--help")
    {
        Console.Write($"Usage: claude mcp {command}\n--scope <scope> user, project, local\n");
        return 0;
    }
    var state = File.Exists(statePath) ? File.ReadAllText(statePath) : "absent";
    switch (command)
    {
        case "get":
            if (File.Exists(Path.Combine(home, "claude-get-notice")))
            {
                Console.Write(File.ReadAllText(Path.Combine(home, "claude-get-notice")));
            }
            if (state == "absent")
            {
                Console.Error.Write("No MCP server named \"askkey\".\n");
                return 1;
            }
            if (state == "different")
            {
                Console.Write("askkey:\n  Scope: User config (available in all your projects)\n  Type: stdio\n  Command: other.exe\n  Args: mcp\n");
                return 0;
            }
            Console.Write("askkey:\n  Scope: User config (available in all your projects)\n  Type: stdio\n  Command: " +
                File.ReadAllText(Path.Combine(home, "claude-command")) +
                "\n  Args: mcp\n  Status: " +
                (File.Exists(Path.Combine(home, "claude-disconnected")) ? "✘ Failed to connect" : "✔ Connected") +
                "\n");
            return 0;
        case "add-json":
            File.WriteAllText(Path.Combine(home, "claude-added"), arguments[3]);
            if (File.Exists(Path.Combine(home, "claude-add-fail"))) return 1;
            File.WriteAllText(statePath, "matching");
            return 0;
        case "remove":
            if (File.Exists(Path.Combine(home, "claude-remove-fail"))) return 1;
            File.WriteAllText(statePath, "absent");
            return 0;
        default:
            return 64;
    }
}

static int Grok(string[] arguments)
{
    var home = Environment.GetEnvironmentVariable("GROK_HOME") ?? "";
    var config = Path.Combine(home, "config.toml");
    if (arguments.Length >= 2 && arguments[0] == "mcp" && arguments[1] == "add"
        && arguments.Contains("--help"))
    {
        Console.Write("Usage: grok mcp add [--scope <scope>] <name> -- <command>\n");
        return 0;
    }
    if (arguments.Length >= 2 && arguments[0] == "mcp" && arguments[1] == "list")
    {
        var text = File.Exists(config) ? File.ReadAllText(config) : "";
        var askKeySection = text.IndexOf("[mcp_servers.askkey]", StringComparison.Ordinal);
        if (askKeySection >= 0) text = text[askKeySection..];
        var command = Value(text, "command");
        var args = Value(text, "args");
        Console.Write(JsonSerializer.Serialize(new[]
        {
            new
            {
                name = "askkey",
                scope = "user",
                command,
                args = args.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(item => item.Trim('"', ' ', '[', ']'))
                    .Where(item => item.Length > 0)
                    .ToArray(),
            },
        }));
        return 0;
    }
    if (arguments.Length >= 2 && arguments[0] == "mcp" && arguments[1] == "doctor")
    {
        Console.Write(JsonSerializer.Serialize(new
        {
            servers = new[]
            {
                new { name = "askkey", transport = "stdio", healthy = true },
            },
        }));
        return 0;
    }
    return 64;
}

static string Value(string text, string key)
{
    foreach (var line in text.Split('\n'))
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith(key + " ", StringComparison.Ordinal)
            && !trimmed.StartsWith(key + "=", StringComparison.Ordinal)) continue;
        var equals = trimmed.IndexOf('=');
        if (equals < 0) continue;
        return trimmed[(equals + 1)..].Trim().Trim('"').Replace("\\\"", "\"").Replace("\\\\", "\\");
    }
    return "";
}