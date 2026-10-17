namespace AskKey.Broker.Transport;

/// <summary>Holds the resolved pipe name in the runtime directory so clients can find
/// a server whose name carries a random, unguessable suffix.</summary>
public static class BrokerRuntimeDirectory
{
    public const string PipeNameFileName = "daemon.pipe";

    public static string PipeNameFile(string directory)
        => global::System.IO.Path.Combine(directory, PipeNameFileName);

    /// <summary>Creates the directory 0700-equivalent and returns it.</summary>
    public static string Prepare(string directory)
    {
        Directory.CreateDirectory(directory);
        BrokerAcl.SecureDirectory(directory);
        if (!BrokerAcl.IsPrivateDirectory(directory)) throw new IOException("runtime directory is not private");
        return directory;
    }

    public static void WritePipeName(string directory, string pipeName)
    {
        var path = PipeNameFile(directory);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, pipeName);
        File.Move(temporary, path, overwrite: true);
    }

    public static string? ReadPipeName(string directory)
    {
        try
        {
            var value = File.ReadAllText(PipeNameFile(directory)).Trim();
            return value.Length == 0 ? null : value;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void RemovePipeName(string directory)
    {
        try { File.Delete(PipeNameFile(directory)); }
        catch (IOException) { }
    }
}
