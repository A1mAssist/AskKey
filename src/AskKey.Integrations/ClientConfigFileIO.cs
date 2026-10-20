namespace AskKey.Integrations;

public enum ClientConfigFileFailure
{
    NotFound,
    Unsafe,
    TooLarge,
    WriteFailed,
    ExclusiveExists,
}

public sealed class ClientConfigFileException : Exception
{
    public ClientConfigFileException(ClientConfigFileFailure failure, string? detail = null)
        : base(detail ?? failure.ToString()) => Failure = failure;

    public ClientConfigFileFailure Failure { get; }
}

public readonly record struct RegularFile(byte[] Bytes, int Mode);

/// <summary>
/// Shared no-follow read and exclusive publish for client config files.
/// The Windows equivalent of O_NOFOLLOW is rejecting reparse points, and the
/// equivalent of a POSIX mode is a private ACL (see <see cref="WindowsFileSecurity"/>).
/// </summary>
public static class ClientConfigFileIO
{
    public static int? InspectRegularFile(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            if (WindowsFileSecurity.IsLinkOrReparse(path)) throw new ClientConfigFileException(ClientConfigFileFailure.Unsafe);
            return null;
        }
        if (WindowsFileSecurity.IsLinkOrReparse(path)) throw new ClientConfigFileException(ClientConfigFileFailure.Unsafe);
        if (Directory.Exists(path)) throw new ClientConfigFileException(ClientConfigFileFailure.Unsafe);
        return WindowsFileSecurity.ModeForFile(path);
    }

    public static RegularFile ReadRegularFile(string path, int? maximumBytes = null)
    {
        if (WindowsFileSecurity.IsLinkOrReparse(path)) throw new ClientConfigFileException(ClientConfigFileFailure.Unsafe);
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        }
        catch (FileNotFoundException) { throw new ClientConfigFileException(ClientConfigFileFailure.NotFound); }
        catch (DirectoryNotFoundException) { throw new ClientConfigFileException(ClientConfigFileFailure.NotFound); }
        catch (UnauthorizedAccessException) { throw new ClientConfigFileException(ClientConfigFileFailure.Unsafe); }
        catch (IOException) { throw new ClientConfigFileException(ClientConfigFileFailure.Unsafe); }

        using (stream)
        {
            // Re-check after opening: a directory, device, or link swapped in after
            // the first check must not be read as a config file.
            if (WindowsFileSecurity.IsLinkOrReparse(path) || Directory.Exists(path))
            {
                throw new ClientConfigFileException(ClientConfigFileFailure.Unsafe);
            }
            using var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            while (true)
            {
                int count;
                try { count = stream.Read(chunk, 0, chunk.Length); }
                catch (IOException) { throw new ClientConfigFileException(ClientConfigFileFailure.Unsafe); }
                if (count == 0) break;
                buffer.Write(chunk, 0, count);
                if (maximumBytes is { } limit && buffer.Length > limit)
                {
                    throw new ClientConfigFileException(ClientConfigFileFailure.TooLarge);
                }
            }
            var mode = WindowsFileSecurity.ModeForFile(path);
            return new RegularFile(buffer.ToArray(), mode);
        }
    }

    public static string WriteExclusiveTemporary(
        byte[] data, string directory, string prefix, Action<string>? didCreate = null)
    {
        global::AskKey.System.RuntimeOperationEvents.Publish(global::AskKey.System.RuntimeOperation.ConfigWrite);
        var temporary = Path.Combine(directory, prefix + Guid.NewGuid().ToString());
        FileStream stream;
        try
        {
            stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (IOException) { throw new ClientConfigFileException(ClientConfigFileFailure.WriteFailed); }
        catch (UnauthorizedAccessException) { throw new ClientConfigFileException(ClientConfigFileFailure.WriteFailed); }
        var open = true;
        try
        {
            try { WindowsFileSecurity.SecureFile(temporary); }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                throw new ClientConfigFileException(ClientConfigFileFailure.WriteFailed);
            }
            didCreate?.Invoke(temporary);
            stream.Write(data);
            stream.Flush(true);
            stream.Dispose();
            open = false;
            return temporary;
        }
        catch
        {
            if (open) stream.Dispose();
            try { File.Delete(temporary); } catch (IOException) { }
            throw;
        }
    }

    public static void PublishAtomically(
        byte[] data,
        string path,
        int mode,
        bool exclusive,
        string temporaryPrefix,
        Action<string>? didCreateTemporary = null)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new ClientConfigFileException(ClientConfigFileFailure.WriteFailed);
        var temporary = WriteExclusiveTemporary(data, directory, temporaryPrefix, didCreateTemporary);
        try
        {
            Rename(temporary, path, exclusive);
            WindowsFileSecurity.ApplyMode(path, mode);
        }
        catch
        {
            try { File.Delete(temporary); } catch (IOException) { }
            throw;
        }
    }

    public static void RenameExclusively(string source, string destination)
        => Rename(source, destination, exclusive: true);

    private static void Rename(string source, string destination, bool exclusive)
    {
        try
        {
            File.Move(source, destination, overwrite: !exclusive);
        }
        catch (IOException) when (exclusive && (File.Exists(destination) || Directory.Exists(destination)))
        {
            throw new ClientConfigFileException(ClientConfigFileFailure.ExclusiveExists);
        }
        catch (IOException) { throw new ClientConfigFileException(ClientConfigFileFailure.WriteFailed); }
        catch (UnauthorizedAccessException) { throw new ClientConfigFileException(ClientConfigFileFailure.WriteFailed); }
    }
}

