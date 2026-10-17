using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AskKey.Integrations;

public enum ClaudeCodeMCPState
{
    Absent,
    Matching,
    Different,
    ScopeConflict,
}

public sealed record ClaudeCodeMCPPlan(string Version, ClaudeCodeMCPState State);

public sealed record ClaudeCodeMCPConnectionStatus(
    bool Connected, string Reason, string Version, string HelperVersion);

public enum ClaudeCodeMCPError
{
    MissingExecutable,
    UnsupportedVersion,
    UnsupportedCLI,
    UnreadableConfiguration,
    ConflictingEntry,
    ConflictingScope,
    ConfigurationChanged,
    AddFailed,
    VerificationFailed,
    RollbackFailed,
    Cancelled,
    Timeout,
    OutputTooLarge,
    ProcessFailed,
}

public sealed class ClaudeCodeMCPException : Exception
{
    public ClaudeCodeMCPException(ClaudeCodeMCPError error, string? detail = null)
        : base(Describe(error, detail))
    {
        Error = error;
        Detail = detail;
    }

    public ClaudeCodeMCPError Error { get; }
    public string? Detail { get; }

    private static string Describe(ClaudeCodeMCPError error, string? detail) => error switch
    {
        ClaudeCodeMCPError.MissingExecutable => "The Claude Code executable was not found.",
        ClaudeCodeMCPError.UnsupportedVersion => "This Claude Code version is not supported.",
        ClaudeCodeMCPError.UnsupportedCLI => "This Claude Code CLI does not expose user-scoped MCP management.",
        ClaudeCodeMCPError.UnreadableConfiguration => "The Claude Code MCP configuration could not be read.",
        ClaudeCodeMCPError.ConflictingEntry => "Claude Code already has a different Ask Key MCP entry.",
        ClaudeCodeMCPError.ConflictingScope => "Ask Key is configured for a different Claude Code scope.",
        ClaudeCodeMCPError.ConfigurationChanged => "The Claude Code MCP configuration changed after review.",
        ClaudeCodeMCPError.AddFailed => "Claude Code could not add the Ask Key MCP server.",
        ClaudeCodeMCPError.VerificationFailed => $"Claude Code connection verification failed ({detail}).",
        ClaudeCodeMCPError.RollbackFailed => $"Claude Code rollback failed (original: {detail}).",
        ClaudeCodeMCPError.Cancelled => "The Claude Code operation was cancelled.",
        ClaudeCodeMCPError.Timeout => "The Claude Code command timed out.",
        ClaudeCodeMCPError.OutputTooLarge => "The Claude Code command produced too much output.",
        _ => "The Claude Code command failed.",
    };
}

/// <summary>
/// User-scope MCP configuration is owned by Claude's CLI, never by a file writer.
/// The Windows port keeps the same `mcp get/add-json/remove --scope user` contract
/// and preflight checks; only the executable name and search paths differ.
/// </summary>
public sealed class ClaudeCodeMCPAdapter
{
    public const string MinimumVersion = "2.1.0";

    private static readonly Regex VersionPattern =
        new(@"^\d+\.\d+\.\d+ \(Claude Code\)$", RegexOptions.CultureInvariant);

    public ClaudeCodeMCPAdapter(
        string homeDirectory,
        string workingDirectory,
        string helperPath,
        string brokerPipeName,
        string? claudeExecutable = null,
        CodexHelperSigning? signing = null,
        string? searchPath = null,
        string? brokerPipeFile = null)
    {
        HomeDirectory = Path.GetFullPath(homeDirectory);
        WorkingDirectory = Path.GetFullPath(workingDirectory);
        HelperPath = helperPath;
        BrokerPipeName = brokerPipeName;
        BrokerPipeFile = brokerPipeFile ?? brokerPipeName;
        Signing = signing ?? CodexHelperSigning.Executable;
        var paths = new List<string> { Path.Combine(HomeDirectory, ".local", "bin") };
        var rawSearch = searchPath ?? Environment.GetEnvironmentVariable("PATH") ?? "";
        paths.AddRange(rawSearch.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            paths.Add(Path.Combine(localAppData, "Programs", "claude"));
            paths.Add(Path.Combine(localAppData, "Programs", "Claude"));
        }
        if (!string.IsNullOrEmpty(appData)) paths.Add(Path.Combine(appData, "npm"));
        ExecutableSearchPath = string.Join(Path.PathSeparator, paths.Distinct(StringComparer.OrdinalIgnoreCase));
        ClaudeExecutable = claudeExecutable
            ?? paths.SelectMany(directory => new[] { ".exe", ".cmd", ".bat" }
                    .Select(extension => Path.Combine(directory, "claude" + extension)))
                .FirstOrDefault(IsRunnableCliFile)
            ?? Path.Combine(HomeDirectory, ".local", "bin", "claude.exe");
    }

    public string HomeDirectory { get; }
    public string WorkingDirectory { get; }
    public string HelperPath { get; }
    public string BrokerPipeName { get; }
    public string BrokerPipeFile { get; }
    public CodexHelperSigning Signing { get; }
    public string ClaudeExecutable { get; }
    public string ExecutableSearchPath { get; }
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(12);
    public TimeSpan TerminationGrace { get; set; } = TimeSpan.FromMilliseconds(200);

    public ClaudeCodeMCPPlan Plan()
    {
        var version = Preflight();
        return new ClaudeCodeMCPPlan(version, InspectEntry().State);
    }

    public ClaudeCodeMCPConnectionStatus Connect()
    {
        var plan = Plan();
        switch (plan.State)
        {
            case ClaudeCodeMCPState.Different:
                throw new ClaudeCodeMCPException(ClaudeCodeMCPError.ConflictingEntry);
            case ClaudeCodeMCPState.ScopeConflict:
                throw new ClaudeCodeMCPException(ClaudeCodeMCPError.ConflictingScope);
            case ClaudeCodeMCPState.Matching:
                return Verify(plan.Version);
        }
        // Recheck immediately before the write; a preview is never write authority.
        if (InspectEntry().State != ClaudeCodeMCPState.Absent)
        {
            throw new ClaudeCodeMCPException(ClaudeCodeMCPError.ConfigurationChanged);
        }
        var json = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["type"] = "stdio",
            ["command"] = HelperPath,
            ["args"] = new[] { "mcp" },
        });
        var added = RunClaude(new[]
        {
            "mcp", "add-json", "askkey", Encoding.UTF8.GetString(json), "--scope", "user",
        });
        if (added.Status != 0) throw new ClaudeCodeMCPException(ClaudeCodeMCPError.AddFailed);
        try
        {
            var status = Verify(plan.Version);
            if (!status.Connected)
            {
                throw new ClaudeCodeMCPException(ClaudeCodeMCPError.VerificationFailed, status.Reason);
            }
            return status;
        }
        catch (Exception failure)
        {
            try
            {
                Rollback();
            }
            catch (Exception cleanup)
            {
                throw new ClaudeCodeMCPException(
                    ClaudeCodeMCPError.RollbackFailed,
                    $"{FailureReason(failure)}; cleanup: {FailureReason(cleanup)}");
            }
            throw;
        }
    }

    public ClaudeCodeMCPConnectionStatus Verify() => Verify(Preflight());

    internal string FailureReason(Exception error)
        => error is ClaudeCodeMCPException typed ? typed.Error.ToString() : "process_failed";
    internal sealed record Entry(ClaudeCodeMCPState State, bool Connected = false);

    internal Entry InspectEntry(bool cleanup = false)
    {
        var response = RunClaude(new[] { "mcp", "get", "askkey" }, cleanup);
        var output = response.StdoutText.Trim();
        var diagnostic = response.StderrText.Trim();
        var combinedLines = (output + "\n" + diagnostic).Split('\n');
        if (response.Status == 1
            && combinedLines.Any(line => line.TrimStart().StartsWith(
                "No MCP server named \"askkey\".", StringComparison.Ordinal)))
        {
            // get is authoritative for this name. list would health-check every
            // unrelated server and connector, whose labels are not identifiers.
            return new Entry(ClaudeCodeMCPState.Absent);
        }
        var outputLines = output.Split('\n');
        var entryLine = Array.FindIndex(outputLines, line => line.TrimEnd('\r') == "askkey:");
        if (response.Status != 0 || entryLine < 0)
        {
            throw new ClaudeCodeMCPException(ClaudeCodeMCPError.UnreadableConfiguration);
        }
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in outputLines.Skip(entryLine + 1))
        {
            var trimmed = raw.Trim();
            if (trimmed.StartsWith("To remove this server, run: claude mcp remove ", StringComparison.Ordinal))
            {
                continue;
            }
            if (trimmed.Length == 0) continue;
            if (Regex.IsMatch(trimmed, "^[A-Za-z_][A-Za-z0-9_]*="))
            {
                return new Entry(ClaudeCodeMCPState.Different);
            }
            var colon = trimmed.IndexOf(':');
            if (colon < 0)
            {
                // An unlabelled value can be an environment assignment.
                return new Entry(ClaudeCodeMCPState.Different);
            }
            var key = trimmed[..colon];
            var value = trimmed[(colon + 1)..].Trim();
            if (key == "Environment") return new Entry(ClaudeCodeMCPState.Different);
            if (key is not ("Scope" or "Status" or "Type" or "Command" or "Args")) continue;
            if (!fields.TryAdd(key, value)) return new Entry(ClaudeCodeMCPState.Different);
        }
        if (!fields.TryGetValue("Scope", out var scope) || scope.Length == 0)
        {
            throw new ClaudeCodeMCPException(ClaudeCodeMCPError.UnreadableConfiguration);
        }
        if (scope != "User config (available in all your projects)")
        {
            return new Entry(ClaudeCodeMCPState.ScopeConflict);
        }
        if (fields.GetValueOrDefault("Type") != "stdio"
            || fields.GetValueOrDefault("Command") != HelperPath
            || fields.GetValueOrDefault("Args") != "mcp")
        {
            return new Entry(ClaudeCodeMCPState.Different);
        }
        return new Entry(
            ClaudeCodeMCPState.Matching,
            fields.GetValueOrDefault("Status") == "✔ Connected");
    }

    internal void Rollback()
    {
        var current = InspectEntry(cleanup: true);
        if (current.State == ClaudeCodeMCPState.Absent) return;
        if (current.State != ClaudeCodeMCPState.Matching)
        {
            throw new ClaudeCodeMCPException(ClaudeCodeMCPError.ConfigurationChanged);
        }
        var removed = RunClaude(new[] { "mcp", "remove", "askkey", "--scope", "user" }, cleanup: true);
        if (removed.Status != 0 || InspectEntry(cleanup: true).State != ClaudeCodeMCPState.Absent)
        {
            throw new ClaudeCodeMCPException(ClaudeCodeMCPError.VerificationFailed, "rollback_absence");
        }
    }

    internal Dictionary<string, string> ProcessEnvironment() => new(StringComparer.OrdinalIgnoreCase)
    {
        // Do not inherit tokens, Claude configuration overrides, shell startup
        // variables, or loader injection from the app's environment.
        // .NET needs SystemRoot and the shared runtime on PATH to start the
        // apphost; the Swift port inherited these from the user session.
        ["HOME"] = HomeDirectory,
        ["USERPROFILE"] = HomeDirectory,
        ["PATH"] = ExecutableSearchPath,
        ["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        ["WINDIR"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        ["LANG"] = "en_US.UTF-8",
        ["LC_ALL"] = "en_US.UTF-8",
        ["NO_COLOR"] = "1",
        ["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1",
        ["DISABLE_AUTOUPDATER"] = "1",
        ["ASKKEY_BROKER_PIPE"] = BrokerPipeFile,
    };

    internal RunResult RunClaude(IReadOnlyList<string> arguments, bool cleanup = false)
        => Run(ClaudeExecutable, arguments, null, cleanup);

    internal RunResult Run(
        string executable, IReadOnlyList<string> arguments, byte[]? input = null, bool cleanup = false)
    {
        try
        {
            var result = IntegrationProcess.RunCaptured(
                executable, arguments, ProcessEnvironment(), WorkingDirectory, input,
                CommandTimeout, TerminationGrace, 1_048_576, cleanup ? null : () => false);
            if (result.TimedOut) throw new ClaudeCodeMCPException(ClaudeCodeMCPError.Timeout);
            return result;
        }
        catch (global::AskKey.System.RestrictedProcessException exception)
        {
            throw new ClaudeCodeMCPException(exception.Kind switch
            {
                global::AskKey.System.RestrictedProcessFailureKind.Cancelled => ClaudeCodeMCPError.Cancelled,
                global::AskKey.System.RestrictedProcessFailureKind.OutputTooLarge => ClaudeCodeMCPError.OutputTooLarge,
                _ => ClaudeCodeMCPError.ProcessFailed,
            });
        }
    }

    internal string Preflight()
    {
        if (!IsRunnableCliFile(ClaudeExecutable))
        {
            throw new ClaudeCodeMCPException(ClaudeCodeMCPError.MissingExecutable);
        }
        var reported = RunClaude(new[] { "--version" });
        var text = reported.StdoutText.Trim();
        if (reported.Status != 0
            || !VersionPattern.IsMatch(text)
            || text.Split(' ')[0] is not { } version
            || CompareNumeric(version, MinimumVersion) < 0)
        {
            throw new ClaudeCodeMCPException(ClaudeCodeMCPError.UnsupportedVersion);
        }
        foreach (var command in new[] { "add-json", "remove", "get", "list" })
        {
            var help = RunClaude(new[] { "mcp", command, "--help" });
            var output = help.StdoutText;
            if (help.Status != 0 || !output.Contains("Usage:", StringComparison.Ordinal)
                || !output.Contains(command, StringComparison.Ordinal))
            {
                throw new ClaudeCodeMCPException(ClaudeCodeMCPError.UnsupportedCLI);
            }
            if (command is "add-json" or "remove"
                && (!output.Contains("--scope", StringComparison.Ordinal)
                    || !output.Contains("user", StringComparison.Ordinal)))
            {
                throw new ClaudeCodeMCPException(ClaudeCodeMCPError.UnsupportedCLI);
            }
        }
        return version;
    }

    private static bool IsRunnableCliFile(string path)
    {
        try
        {
            if (!File.Exists(path) || WindowsFileSecurity.IsLinkOrReparse(path)) return false;
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) return false;
            return Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".cmd" or ".bat";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static int CompareNumeric(string left, string right)
    {
        var a = left.Split('.');
        var b = right.Split('.');
        for (var index = 0; index < 3; index++)
        {
            var av = index < a.Length && int.TryParse(a[index], out var x) ? x : 0;
            var bv = index < b.Length && int.TryParse(b[index], out var y) ? y : 0;
            if (av != bv) return av.CompareTo(bv);
        }
        return 0;
    }

    internal ClaudeCodeMCPConnectionStatus Verify(string version)
    {
        ClaudeCodeMCPConnectionStatus Status(string reason, string helperVersion = "")
            => new(reason == "ok", reason, version, helperVersion);

        var entry = InspectEntry();
        if (entry.State != ClaudeCodeMCPState.Matching) return Status("configuration_mismatch");
        if (!entry.Connected) return Status("client_disconnected");
        if (!Signing.IsTrusted(HelperPath)) return Status("helper_signature");
        var helper = Run(
            HelperPath, new[] { "mcp" }, MCPHelperContract.RequestPayload(MCPHelperContract.Identity.ClaudeClient));
        MCPHelperContract.Inspection inspected;
        try
        {
            if (helper.Status != 0)
            {
                return Status("helper_contract");
            }
            inspected = MCPHelperContract.Inspect(helper.StdoutText, MCPHelperContract.Identity.ClaudeClient);
        }
        catch (MCPHelperContract.MCPHelperContractException)
        {
            return Status("helper_contract");
        }
        var health = Run(HelperPath, new[] { "health" });
        if (health.Status != 0 || !BrokerHealthIsOk(health.StdoutText))
        {
            return Status("broker_unhealthy", inspected.Version);
        }
        return Status("ok", inspected.Version);
    }

    private static bool BrokerHealthIsOk(string text)
    {
        try
        {
            var response = Broker.Protocol.BrokerJson.DecodeResponse(Encoding.UTF8.GetBytes(text));
            return response?.ResponseKind == Broker.Protocol.BrokerResponse.Kind.Success
                && response.Payload?.PayloadKind == Broker.Protocol.BrokerPayload.Kind.Health
                && response.Payload.Health?.Status == "ok"
                && response.Payload.Health.Version == Broker.Protocol.BrokerProtocolVersion.Current;
        }
        catch
        {
            return false;
        }
    }
}