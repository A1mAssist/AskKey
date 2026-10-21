using AskKey.Broker.Approval;

namespace AskKey.Broker.Protocol;

public sealed class BrokerRequest
{
    public int Version { get; init; }
    public required string Method { get; init; }
    public string? RequestId { get; init; }
    public string? Capability { get; init; }
    public BrokerTextRunRequest? TextRun { get; init; }
    public BrokerFileWriteRequest? FileWrite { get; init; }
    public string? OperationId { get; init; }
    public AgentTextWriteRequest? TextWrite { get; init; }
    /// <summary>Named-pipe stand-in for SCM_RIGHTS: the random token whose four runtime pipes
    /// the client created. Only read for <c>runtime.run</c>; the JSON method payload is unchanged.</summary>
    public string? RuntimeChannel { get; init; }
}

/// <summary>Wire shape of a runtime invocation. Caller declarations are self-reported labels,
/// never authorization inputs.</summary>
public sealed class BrokerTextRunRequest : IEquatable<BrokerTextRunRequest>
{
    public string OperationId { get; }
    public IReadOnlyList<string> Command { get; private set; }
    public IReadOnlyList<string> CredentialNames { get; }
    public string? WorkingDirectory { get; }
    public IReadOnlyDictionary<string, string> InheritedEnvironment { get; }
    public string? CallerName { get; }
    public string? CallerPurpose { get; }

    public BrokerTextRunRequest(
        string operationId,
        IReadOnlyList<string> command,
        IReadOnlyList<string> credentialNames,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? inheritedEnvironment = null,
        string? callerName = null,
        string? callerPurpose = null)
    {
        OperationId = operationId;
        Command = command;
        CredentialNames = credentialNames;
        WorkingDirectory = workingDirectory;
        InheritedEnvironment = inheritedEnvironment ?? new Dictionary<string, string>();
        CallerName = SanitizeDeclaration(callerName);
        CallerPurpose = SanitizeDeclaration(callerPurpose);
    }

    /// <summary>
    /// Rewrites the target executable to the absolute path the Broker will spawn, so
    /// the approved display and the running process name the same file. Returns false
    /// when the executable cannot be resolved; the caller must fail before approval.
    /// </summary>
    public bool BindExecutable(Func<string, string?> resolve)
    {
        if (Command.Count == 0) return false;
        var executable = resolve(Command[0]);
        if (executable is not { Length: > 0 } || !Path.IsPathFullyQualified(executable)) return false;
        if (string.Equals(executable, Command[0], StringComparison.Ordinal)) return true;
        var bound = new string[Command.Count];
        for (var index = 0; index < Command.Count; index++) bound[index] = Command[index];
        bound[0] = executable;
        Command = bound;
        return true;
    }

    public static string? SanitizeDeclaration(string? raw)
    {
        if (raw is null) return null;
        var trimmed = raw.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    public static bool ValidDeclaration(string? raw)
    {
        var value = SanitizeDeclaration(raw);
        if (value is null) return true;
        return BrokerWire.Utf8Length(value) <= BrokerLimits.MaximumFieldBytes
            && value.All(c => !char.IsControl(c));
    }

    public bool DeclarationsAreValid => ValidDeclaration(CallerName) && ValidDeclaration(CallerPurpose);

    public static IReadOnlyDictionary<string, string> FilteredInheritedEnvironment(
        IReadOnlyDictionary<string, string> environment)
    {
        var filtered = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in environment)
        {
            if (InheritedEnvironmentKey(key)) filtered[key] = value;
        }
        return filtered;
    }

    private static readonly HashSet<string> InheritedEnvironmentKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "HOME", "PATH", "TMPDIR", "LANG", "TERM", "SHELL", "USER", "LOGNAME",
        // Windows process baseline: the Swift list has no Windows equivalents.
        // ProgramData is machine-wide (C:\ProgramData) and carries no credential, but
        // Win32-OpenSSH resolves its crypto-config there and exits 255 without it.
        "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "TEMP", "TMP", "SystemRoot", "ComSpec", "PATHEXT",
        "ProgramData",
    };

    // Windows environment names are case-insensitive; PowerShell exposes Path, not PATH.
    private static bool InheritedEnvironmentKey(string key)
        => InheritedEnvironmentKeys.Contains(key) || key.StartsWith("LC_", StringComparison.OrdinalIgnoreCase);

    public bool Equals(BrokerTextRunRequest? other)
        => other is not null
            && OperationId == other.OperationId
            && Command.SequenceEqual(other.Command, StringComparer.Ordinal)
            && CredentialNames.SequenceEqual(other.CredentialNames, StringComparer.Ordinal)
            && WorkingDirectory == other.WorkingDirectory
            && SameEnvironment(InheritedEnvironment, other.InheritedEnvironment)
            && CallerName == other.CallerName
            && CallerPurpose == other.CallerPurpose;

    private static bool SameEnvironment(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right)
    {
        if (left.Count != right.Count) return false;
        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var other) || other != value) return false;
        }
        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as BrokerTextRunRequest);

    public override int GetHashCode() => HashCode.Combine(OperationId, Command.Count, CredentialNames.Count, CallerName);
}

public sealed class BrokerTextCredential
{
    public string EnvironmentVariable { get; }
    public string Value { get; }

    public BrokerTextCredential(string environmentVariable, string value)
    {
        EnvironmentVariable = environmentVariable;
        Value = value;
    }
}

public sealed class BrokerTextRunResult : IEquatable<BrokerTextRunResult>
{
    public enum Kind
    {
        Exited,
        ApprovalRequired,
        OutcomeUnknown,
    }

    public Kind ResultKind { get; }
    public int ExitCode { get; }
    public string? OperationId { get; }
    public IReadOnlyList<BrokerApprovalTicket> Tickets { get; }

    private BrokerTextRunResult(Kind kind, int exitCode, string? operationId, IReadOnlyList<BrokerApprovalTicket> tickets)
    {
        ResultKind = kind;
        ExitCode = exitCode;
        OperationId = operationId;
        Tickets = tickets;
    }

    public static BrokerTextRunResult Exited(int exitCode) => new(Kind.Exited, exitCode, null, Array.Empty<BrokerApprovalTicket>());

    public static BrokerTextRunResult ApprovalRequired(string operationId, IReadOnlyList<BrokerApprovalTicket> tickets)
        => new(Kind.ApprovalRequired, 0, operationId, tickets);

    public static BrokerTextRunResult OutcomeUnknown { get; } = new(Kind.OutcomeUnknown, 0, null, Array.Empty<BrokerApprovalTicket>());

    public bool Equals(BrokerTextRunResult? other)
        => other is not null && ResultKind == other.ResultKind
            && ExitCode == other.ExitCode && OperationId == other.OperationId
            && Tickets.SequenceEqual(other.Tickets);

    public override bool Equals(object? obj) => Equals(obj as BrokerTextRunResult);

    public override int GetHashCode() => HashCode.Combine(ResultKind, ExitCode, OperationId);
}
