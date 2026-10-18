using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AskKey.Vault;

/// <summary>SQLite only opens a private, verified copy during preflight.</summary>
internal static class CurrentLibrarySnapshot
{
    private const uint GenericRead = 0x80000000;
    private const uint ShareAll = 1 | 2 | 4;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint BackupSemantics = 0x02000000;

    private sealed record ObservedFile(uint Volume, ulong Identity, byte[] Bytes);

    internal static bool RegularFileExists(string path)
    {
        using var handle = OpenRegularFile(path);
        return handle is not null;
    }

    internal static T WithCopy<T>(string source, Func<string, T> body)
    {
        var directory = Path.Combine(Path.GetTempPath(), "AskKeyCurrentLibrary-" + Guid.NewGuid().ToString("N"));
        PrivateFileSystem.CreatePrivateDirectory(directory);
        try
        {
            var before = Contents(source);
            if (!before.ContainsKey("")) throw new VaultBootstrapException(VaultBootstrapError.MissingDatabase);
            var destination = Path.Combine(directory, "credentials-v2.db");
            foreach (var (suffix, file) in before)
            {
                using var output = PrivateFileSystem.CreatePrivateFile(destination + suffix,
                    FileMode.CreateNew, FileAccess.Write, FileShare.None);
                output.Write(file.Bytes);
            }
            Validate(source, before);
            var result = body(destination);
            Validate(source, before);
            return result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new VaultBootstrapException(VaultBootstrapError.InvalidState, error);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    private static Dictionary<string, ObservedFile> Contents(string source)
    {
        // Even a cold journal is unexpected for a current WAL library. Preserve it.
        if (RegularFileExists(source + "-journal")) throw InvalidState();
        var hasShm = RegularFileExists(source + "-shm");
        var files = new Dictionary<string, ObservedFile>(StringComparer.Ordinal);
        foreach (var suffix in new[] { "", "-wal" })
        {
            using var handle = OpenRegularFile(source + suffix);
            if (handle is null) continue;
            var before = Information(handle);
            using var stream = new FileStream(handle, FileAccess.Read);
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            var after = Information(handle);
            if (before != after || bytes.Length != before.Size) throw InvalidState();
            files.Add(suffix, new ObservedFile(before.Volume, before.Identity, bytes.ToArray()));
        }
        if (!files.ContainsKey("") && (files.Count != 0 || hasShm)) throw InvalidState();
        return files;
    }

    private static void Validate(string source, Dictionary<string, ObservedFile> before)
    {
        var after = Contents(source);
        if (before.Count != after.Count) throw InvalidState();
        foreach (var (suffix, expected) in before)
        {
            if (!after.TryGetValue(suffix, out var actual)
                || expected.Volume != actual.Volume || expected.Identity != actual.Identity
                || !expected.Bytes.AsSpan().SequenceEqual(actual.Bytes)) throw InvalidState();
        }
    }

    private static SafeFileHandle? OpenRegularFile(string path)
    {
        RejectReparseDirectories(path);
        var handle = CreateFileW(path, GenericRead, ShareAll, IntPtr.Zero, OpenExisting,
            OpenReparsePoint | BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error is 2 or 3) return null;
            throw InvalidState();
        }
        try
        {
            _ = Information(handle);
            RejectReparseDirectories(path);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void RejectReparseDirectories(string path)
    {
        for (var directory = Path.GetDirectoryName(Path.GetFullPath(path)); directory is not null;
             directory = Path.GetDirectoryName(directory))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(directory); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0
                || (attributes & FileAttributes.Directory) == 0) throw InvalidState();
        }
    }

    private readonly record struct FileObservation(uint Volume, ulong Identity, long Size, long Written, long Changed);

    private static FileObservation Information(SafeFileHandle handle)
    {
        if (GetFileType(handle) != 1
            || !GetFileInformationByHandle(handle, out var info)
            || !GetFileInformationByHandleEx(handle, 0, out var basic, (uint)Marshal.SizeOf<FileBasicInfo>())
            || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw InvalidState();
        }
        return new FileObservation(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow,
            ((long)info.FileSizeHigh << 32) | info.FileSizeLow, basic.LastWriteTime, basic.ChangeTime);
    }

    private static VaultBootstrapException InvalidState() => new(VaultBootstrapError.InvalidState);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
        public uint Attributes;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public FileAttributes Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind,
        out FileBasicInfo info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle handle);
}
