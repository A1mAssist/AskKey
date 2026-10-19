using AskKey.Broker.Protocol;
using AskKey.Broker.Transport;

namespace AskKey.Integrations;

public enum AgentClientKind
{
    ClaudeCode,
    Codex,
    Cursor,
    Grok,
}

public sealed record AgentConnectionStatus(
    AgentClientKind Client,
    bool Installed,
    bool McpConfigured,
    bool DiscoveryConfigured,
    bool Connected,
    string Message);

public sealed record AgentSetupTarget(string Path, bool Exists);

public sealed record AgentSetupPreview(
    AgentSetupTarget? McpConfiguration,
    AgentSetupTarget DiscoveryHook,
    IReadOnlyList<AgentSetupTarget> Backups);

/// <summary>
/// Applies the same safe client setup used by the macOS onboarding flow:
/// review a discovery-hook plan, apply it, connect the MCP server, then verify both.
/// </summary>
public sealed class AgentConnector
{
    private static readonly object ClaudeLock = new();
    private static readonly object CodexLock = new();
    private static readonly object CursorLock = new();
    private static readonly object GrokLock = new();

    private readonly string _home;
    private readonly string _support;
    private readonly string _helper;
    private readonly string _brokerPipeName;
    private readonly string _brokerPipeFile;
    private readonly string? _claudeExecutable;
    private readonly string? _grokExecutable;
    private readonly CodexHelperSigning _signing;

    public AgentConnector(
        string homeDirectory,
        string supportDirectory,
        string helperPath,
        string brokerPipeName,
        string brokerPipeFile,
        string? claudeExecutable = null,
        string? grokExecutable = null,
        CodexHelperSigning? signing = null)
    {
        _home = Path.GetFullPath(homeDirectory);
        _support = Path.GetFullPath(supportDirectory);
        _helper = helperPath;
        _brokerPipeName = brokerPipeName;
        _brokerPipeFile = brokerPipeFile;
        _claudeExecutable = claudeExecutable;
        _grokExecutable = grokExecutable
            ?? FindExecutable("grok", Path.Combine(_home, ".grok", "bin", "grok.exe"));
        _signing = signing ?? CodexHelperSigning.Executable;
    }

    public AgentConnectionStatus Check(AgentClientKind client)
    {
        try
        {
            return client switch
            {
                AgentClientKind.ClaudeCode => CheckClaude(),
                AgentClientKind.Codex => CheckCodex(),
                AgentClientKind.Cursor => CheckCursor(),
                _ => CheckGrok(),
            };
        }
        catch (Exception error)
        {
            return Failed(client, error);
        }
    }

    public AgentConnectionStatus Connect(AgentClientKind client)
    {
        lock (LockFor(client))
        {
            try
            {
                var discovery = DiscoveryFor(client);
                discovery.VerifyHelper();
                var preview = discovery.Preview();
                discovery.Apply(preview.Plan);

                try
                {
                    ConnectMcp(client);
                }
                catch (Exception error)
                {
                    try
                    {
                        if (preview.Changed) discovery.Restore(preview.Plan);
                    }
                    catch (Exception restore)
                    {
                        return Failed(client, new InvalidOperationException(
                            "The client connection failed and Ask Key could not restore the credential discovery hook.",
                            new AggregateException(error, restore)));
                    }
                    return Failed(client, error);
                }

                return Check(client);
            }
            catch (Exception error)
            {
                return Failed(client, error);
            }
        }
    }

    public AgentSetupPreview SetupPreview(AgentClientKind client)
    {
        AgentSetupTarget? mcp = null;
        var backups = new List<AgentSetupTarget>();
        switch (client)
        {
            case AgentClientKind.Codex:
                var codex = CodexAdapter();
                mcp = FileTarget(codex.ConfigPath);
                backups.Add(DirectoryTarget(codex.BackupDirectory));
                break;
            case AgentClientKind.Cursor:
                var cursor = CursorAdapter();
                mcp = FileTarget(cursor.UserConfigPath);
                backups.Add(DirectoryTarget(cursor.BackupDirectory));
                break;
            case AgentClientKind.Grok:
                var grok = GrokAdapter();
                mcp = FileTarget(grok.ConfigPath);
                backups.Add(DirectoryTarget(grok.BackupDirectory));
                break;
        }

        var discovery = DiscoveryFor(client);
        backups.Add(DirectoryTarget(discovery.BackupDirectory));
        return new(mcp, FileTarget(discovery.HooksPath), backups);
    }

    private static AgentSetupTarget FileTarget(string path) => new(path, File.Exists(path));

    private static AgentSetupTarget DirectoryTarget(string path) => new(path, Directory.Exists(path));

    private AgentConnectionStatus CheckClaude()
    {
        var adapter = ClaudeAdapter();
        if (!File.Exists(adapter.ClaudeExecutable))
        {
            return new(AgentClientKind.ClaudeCode, false, false, false, false,
                "Claude Code is not installed, or its CLI is not on PATH.");
        }
        var plan = adapter.Plan();
        var configured = plan.State == ClaudeCodeMCPState.Matching;
        var connected = configured && adapter.Verify(plan.Version).Connected;
        var discovery = DiscoveryFor(AgentClientKind.ClaudeCode).HasExpected();
        return Result(AgentClientKind.ClaudeCode, true, configured, discovery, connected);
    }

    private AgentConnectionStatus CheckCodex()
    {
        var adapter = CodexAdapter();
        var configured = adapter.HasConfiguration();
        var connected = adapter.Status() == CodexConnectionStatus.Connected;
        var discovery = DiscoveryFor(AgentClientKind.Codex).HasExpected();
        return Result(AgentClientKind.Codex, true, configured, discovery, connected);
    }

    private AgentConnectionStatus CheckCursor()
    {
        var adapter = CursorAdapter();
        var configured = adapter.HasConfiguration();
        var connected = adapter.Status().Connected;
        var discovery = DiscoveryFor(AgentClientKind.Cursor).HasExpected();
        return Result(AgentClientKind.Cursor, true, configured, discovery, connected);
    }

    private AgentConnectionStatus CheckGrok()
    {
        var adapter = GrokAdapter();
        var installed = File.Exists(adapter.GrokExecutable);
        var configured = adapter.HasConfiguration();
        var connected = adapter.Status().Connected;
        var discovery = DiscoveryFor(AgentClientKind.Grok).HasExpected();
        return Result(AgentClientKind.Grok, installed, configured, discovery, connected);
    }

    private void ConnectMcp(AgentClientKind client)
    {
        switch (client)
        {
            case AgentClientKind.ClaudeCode:
                _ = ClaudeAdapter().Connect();
                break;
            case AgentClientKind.Codex:
                _ = CodexAdapter().Apply();
                break;
            case AgentClientKind.Cursor:
                var cursor = CursorAdapter();
                _ = cursor.Apply();
                if (!cursor.Verify().Connected)
                {
                    cursor.Rollback();
                    throw new CursorMCPException(CursorMCPError.ReadbackFailed);
                }
                break;
            case AgentClientKind.Grok:
                _ = GrokAdapter().Connect();
                break;
        }
    }

    private ClaudeCodeMCPAdapter ClaudeAdapter() => new(
        _home,
        _home,
        _helper,
        _brokerPipeName,
        _claudeExecutable,
        _signing,
        brokerPipeFile: _brokerPipeFile);

    private CodexUserMCPAdapter CodexAdapter() => new(
        Path.Combine(_home, ".codex", "config.toml"),
        _helper,
        Path.Combine(_support, "client-backups", "codex"),
        _brokerPipeName,
        CodexMCPCommand.Missing,
        _signing,
        requiresCredentialDiscovery: true,
        brokerPipeFile: _brokerPipeFile);

    private CursorUserMCPAdapter CursorAdapter() => new(
        _home,
        Path.Combine(_support, "client-backups", "cursor"),
        _helper,
        _brokerPipeName,
        _signing,
        brokerPipeFile: _brokerPipeFile);

    private GrokCLIAdapter GrokAdapter() => new(
        Path.Combine(_home, ".grok"),
        Path.Combine(_support, "grok-home"),
        _helper,
        _grokExecutable ?? Path.Combine(_home, ".grok", "bin", "grok.exe"),
        Path.Combine(_support, "client-backups", "grok"),
        _brokerPipeName,
        _signing,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BrokerConfiguration.PipeOverrideVariable] = _brokerPipeFile,
        },
        brokerPipeFile: _brokerPipeFile);

    private DiscoveryActions DiscoveryFor(AgentClientKind client)
    {
        if (client == AgentClientKind.Codex)
        {
            var adapter = CodexAdapter();
            var hooks = new CodexDiscoveryHookConfiguration(
                Path.Combine(_home, ".codex", "hooks.json"),
                Path.Combine(_support, "client-backups", "codex-discovery"),
                _helper);
            return new(
                adapter.VerifyCommandDiscoveryHelper,
                () =>
                {
                    var plan = hooks.Preview();
                    return (plan, plan.Changed);
                },
                plan => hooks.Apply((CodexDiscoveryHookPlan)plan),
                plan => hooks.Restore((CodexDiscoveryHookPlan)plan),
                hooks.HasExpectedHook,
                hooks.HooksPath,
                hooks.BackupDirectory);
        }

        var discoveryClient = client switch
        {
            AgentClientKind.ClaudeCode => CommandDiscoveryClient.Claude,
            AgentClientKind.Cursor => CommandDiscoveryClient.Cursor,
            _ => CommandDiscoveryClient.Grok,
        };
        var configuration = discoveryClient.Configuration(
            _home,
            _helper,
            Path.Combine(_support, "client-backups", $"{Id(client)}-discovery"));
        return new(
            () => discoveryClient.VerifyHelper(_helper, _home, _signing.IsTrusted),
            () =>
            {
                var plan = configuration.Preview();
                return (plan, plan.Changed);
            },
            plan => configuration.Apply((CommandDiscoveryHookPlan)plan),
            plan => configuration.Restore((CommandDiscoveryHookPlan)plan),
            configuration.HasExpectedHook,
            configuration.HooksPath,
            configuration.BackupDirectory);
    }

    private static AgentConnectionStatus Result(
        AgentClientKind client,
        bool installed,
        bool configured,
        bool discovery,
        bool connected)
    {
        var verified = connected && discovery;
        var message = verified
            ? "Connected and verified."
            : !installed
                ? "The client is not installed, or its CLI is not on PATH."
                : configured && !discovery
                    ? "The MCP server is configured, but the credential discovery hook is missing."
                    : configured
                        ? "The client configuration was found, but the connection is not verified."
                        : "The client is not connected to Ask Key.";
        return new(client, installed, configured, discovery, verified, message);
    }

    private AgentConnectionStatus Failed(AgentClientKind client, Exception error)
    {
        var installed = client switch
        {
            AgentClientKind.ClaudeCode => File.Exists(ClaudeAdapter().ClaudeExecutable),
            AgentClientKind.Grok => File.Exists(GrokAdapter().GrokExecutable),
            _ => true,
        };
        return new(client, installed, false, false, false, Describe(error));
    }

    private static string Describe(Exception error)
    {
        if (MentionsBroker(error)) return "The Ask Key Broker is not running. Start Ask Key and try again.";
        return error switch
        {
            ClaudeCodeMCPException { Error: ClaudeCodeMCPError.MissingExecutable } =>
                "Claude Code is not installed, or its CLI is not on PATH.",
            ClaudeCodeMCPException { Error: ClaudeCodeMCPError.UnsupportedVersion or ClaudeCodeMCPError.UnsupportedCLI } =>
                "This Claude Code version does not support automatic setup. Update Claude Code and try again.",
            ClaudeCodeMCPException claude => claude.Message,
            CodexUserMCPException { Error: CodexUserMCPError.UnknownCodexVersion } =>
                "This Codex version is not verified for automatic setup. Update Codex or use manual configuration.",
            CodexUserMCPException codex => codex.Message,
            CursorMCPException cursor => cursor.Message,
            GrokCLIAdapterException grok => grok.Message,
            CommandDiscoveryHookConfigurationException hook => hook.Message,
            CodexDiscoveryHookConfigurationException hook => hook.Message,
            _ => error.Message.Length > 0
                ? error.Message
                : "The client configuration could not be updated or verified.",
        };
    }

    private static bool MentionsBroker(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is BrokerSocketException) return true;
            if (current.Message.Contains("broker", StringComparison.OrdinalIgnoreCase)
                && (current.Message.Contains("not running", StringComparison.OrdinalIgnoreCase)
                    || current.Message.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
                    || current.Message.Contains("unhealthy", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        return false;
    }

    private static object LockFor(AgentClientKind client) => client switch
    {
        AgentClientKind.ClaudeCode => ClaudeLock,
        AgentClientKind.Codex => CodexLock,
        AgentClientKind.Cursor => CursorLock,
        _ => GrokLock,
    };

    private static string Id(AgentClientKind client) => client switch
    {
        AgentClientKind.ClaudeCode => "claude",
        AgentClientKind.Codex => "codex",
        AgentClientKind.Cursor => "cursor",
        _ => "grok",
    };

    private static string? FindExecutable(string name, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in new[] { ".exe", ".cmd", ".bat" })
            {
                var candidate = Path.Combine(directory.Trim(), name + extension);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private sealed record DiscoveryActions(
        Action VerifyHelper,
        Func<(object Plan, bool Changed)> Preview,
        Action<object> Apply,
        Action<object> Restore,
        Func<bool> HasExpected,
        string HooksPath,
        string BackupDirectory);
}
