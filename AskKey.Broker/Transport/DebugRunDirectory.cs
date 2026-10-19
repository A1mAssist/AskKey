namespace AskKey.Broker.Transport;

public enum DebugRunDirectoryError
{
    InvalidDirectory,
}

public sealed class DebugRunDirectoryException : Exception
{
    public DebugRunDirectoryException()
        : base("The isolated Debug run directory must be an existing, privately owned directory outside the real Ask Key data directory.")
    {
    }
}

/// <summary>
/// Debug-only isolation. An explicitly invalid directory throws; it must never
/// degrade to the real namespace. Release builds ignore the variable entirely.
/// </summary>
public static class DebugRunDirectory
{
    public const string EnvironmentVariable = "ASKKEY_DEBUG_RUN_DIRECTORY";

    public static string? Resolve()
        => IsDebugBuild ? Resolve(Environment.GetEnvironmentVariables(), RealDataDirectories()) : null;

    /// <summary>Overridden by tests so the check does not depend on the real profile.</summary>
#if DEBUG
    internal static bool IsDebugBuild { get; set; } = true;
#else
    internal static bool IsDebugBuild { get; set; }
#endif

    internal static string? Resolve(
        global::System.Collections.IDictionary environment,
        IReadOnlyList<string> protectedDirectories)
    {
        if (environment[EnvironmentVariable] is not string raw) return null;
        if (raw.Length == 0 || raw.Contains('\0')) throw new DebugRunDirectoryException();
        if (!global::System.IO.Path.IsPathFullyQualified(raw)) throw new DebugRunDirectoryException();

        var root = global::System.IO.Path.GetFullPath(raw);
        var rootInfo = new DirectoryInfo(root);
        if (!rootInfo.Exists) throw new DebugRunDirectoryException();

        foreach (var protectedPath in protectedDirectories)
        {
            var full = global::System.IO.Path.TrimEndingDirectorySeparator(
                global::System.IO.Path.GetFullPath(protectedPath));
            if (string.Equals(root, full, StringComparison.OrdinalIgnoreCase)
                || root.StartsWith(full + global::System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(root + global::System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new DebugRunDirectoryException();
            }
        }

        // Every component must be a real directory: a junction or symlink could
        // redirect the run directory into the real namespace after validation.
        for (var candidate = rootInfo; candidate is not null; candidate = candidate.Parent)
        {
            if (!candidate.Exists) throw new DebugRunDirectoryException();
            if (candidate.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new DebugRunDirectoryException();
            if (candidate == rootInfo && !BrokerAcl.IsPrivateDirectory(root)) throw new DebugRunDirectoryException();
        }
        return root;
    }

    /// <summary>Stable 16-byte hex namespace, matching the Swift SHA-256 prefix.</summary>
    public static string Namespace(string directory)
    {
        var normalized = global::System.IO.Path.TrimEndingDirectorySeparator(
            global::System.IO.Path.GetFullPath(directory));
        var digest = global::System.Security.Cryptography.SHA256.HashData(
            global::System.Text.Encoding.UTF8.GetBytes(normalized));
        return global::System.Convert.ToHexStringLower(digest.AsSpan(0, 16));
    }

    internal static IReadOnlyList<string> RealDataDirectories()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new[]
        {
            global::System.IO.Path.Combine(local, "AskKey"),
            global::System.IO.Path.Combine(local, "AskKey", "lifecycle"),
        };
    }
}
