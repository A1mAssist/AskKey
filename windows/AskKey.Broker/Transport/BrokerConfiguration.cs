using AskKey.Broker.Protocol;

namespace AskKey.Broker.Transport;

/// <summary>
/// Runtime pipe location. Order: explicit debug isolation, then (Debug builds only)
/// <c>ASKKEY_BROKER_PIPE</c>, then <c>%LOCALAPPDATA%\AskKey\dev</c> or <c>...\AskKey</c>.
/// </summary>
public static class BrokerConfiguration
{
    public const string PipeOverrideVariable = "ASKKEY_BROKER_PIPE";

    /// <summary>Compatibility projection for read-only display; never used to open a pipe.</summary>
    public static string PipeNameFile => TryResolvePipeNameFile() ?? InvalidDebugPipeNameFile();

    public static string InvalidDebugPipeNameFile()
        => global::System.IO.Path.Combine(
            global::System.IO.Path.GetTempPath(), "askkey-invalid-debug-directory", "daemon.pipe");

    public static string? TryResolvePipeNameFile()
    {
        try
        {
            return ResolvePipeNameFile();
        }
        catch (DebugRunDirectoryException)
        {
            return null;
        }
    }

    public static string ResolvePipeNameFile()
    {
        if (DebugRunDirectory.Resolve() is { } root)
        {
            return BrokerRuntimeDirectory.PipeNameFile(root);
        }
        if (DebugRunDirectory.IsDebugBuild
            && Environment.GetEnvironmentVariable(PipeOverrideVariable) is { Length: > 0 } overridden)
        {
            if (!global::System.IO.Path.IsPathFullyQualified(overridden))
            {
                throw new BrokerSocketException(BrokerSocketError.PathTooLong, detail: PipeOverrideVariable);
            }
            return global::System.IO.Path.GetFullPath(overridden);
        }
        var subdirectory = DebugRunDirectory.IsDebugBuild ? "dev" : string.Empty;
        var directory = global::System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AskKey",
            subdirectory);
        return BrokerRuntimeDirectory.PipeNameFile(directory);
    }

    /// <summary>Reads the server-written pipe name; the client never guesses it.</summary>
    public static string ResolvePipeName()
    {
        var path = ResolvePipeNameFile();
        var directory = global::System.IO.Path.GetDirectoryName(path)!;
        var name = BrokerRuntimeDirectory.ReadPipeName(directory);
        if (name is null) throw new BrokerSocketException(BrokerSocketError.NotRunning);
        return name;
    }
}
