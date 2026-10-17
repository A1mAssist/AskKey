namespace AskKey.Integrations;

public enum CodexUserMCPError
{
    UnsafeConfigFile,
    IllegalConfig,
    UnknownCodexVersion,
    ConnectionFailed,
    RollbackFailed,
}

public sealed class CodexUserMCPException : Exception
{
    public CodexUserMCPException(CodexUserMCPError error, string? reason = null)
        : base(Describe(error, reason))
    {
        Error = error;
        Reason = reason;
    }

    public CodexUserMCPError Error { get; }

    /// <summary>Only set for <see cref="CodexUserMCPError.ConnectionFailed"/>.</summary>
    public string? Reason { get; }

    public static CodexUserMCPException ConnectionFailed(string reason)
        => new(CodexUserMCPError.ConnectionFailed, reason);

    private static string Describe(CodexUserMCPError error, string? reason) => error switch
    {
        CodexUserMCPError.UnsafeConfigFile => "The Codex config file is not a safe regular file.",
        CodexUserMCPError.IllegalConfig => "The Codex config file is not valid TOML.",
        CodexUserMCPError.UnknownCodexVersion => "The Codex version is unknown and cannot be configured.",
        CodexUserMCPError.ConnectionFailed => $"Ask Key could not verify the Codex connection ({reason}).",
        _ => "Ask Key could not restore the original Codex config. The managed backup was kept for recovery.",
    };
}
