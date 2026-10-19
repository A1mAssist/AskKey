namespace AskKey.Guard;

/// <summary>Identity derived from the pipe client process token, never the request.</summary>
public sealed record GuardCaller(uint ProcessId, string Sid, string ImagePath);

public interface IGuardCallerVerifier
{
    /// <summary>Returns null when the caller is not allowed to use the guard.</summary>
    GuardCaller? Verify(uint processId);
}
