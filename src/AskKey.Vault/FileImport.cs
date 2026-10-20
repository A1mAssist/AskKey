using System.Security.Cryptography;

namespace AskKey.Vault;

public static class FileImport
{
    public const int MaxByteCount = 5 * 1024 * 1024;

    public sealed class FrozenFile
    {
        public string OriginalFilename { get; }
        public byte[] Bytes { get; }
        public int ByteSize => Bytes.Length;
        public byte[] ContentDigest { get; }

        public FrozenFile(string originalFilename, byte[] bytes)
        {
            if (originalFilename.Length == 0 || System.Text.Encoding.UTF8.GetByteCount(originalFilename) > 255)
            {
                throw VaultException.File(FileCredentialIssue.NotFound);
            }
            if (bytes.Length > MaxByteCount) throw VaultException.File(FileCredentialIssue.TooLarge);
            OriginalFilename = originalFilename;
            Bytes = bytes;
            ContentDigest = SHA256.HashData(bytes);
        }

        internal FrozenFile(string originalFilename, byte[] bytes, byte[] contentDigest)
        {
            OriginalFilename = originalFilename;
            Bytes = bytes;
            ContentDigest = contentDigest;
        }
    }

    public enum FreezeCheckpoint
    {
        AfterPathCheck,
        AfterOpen,
    }

    public static FrozenFile Freeze(string path) => Freeze(path, _ => { });

    internal static FrozenFile Freeze(string path, Action<FreezeCheckpoint> checkpoint)
    {
        var info = new FileInfo(path);
        info.Refresh();
        if (!info.Exists)
        {
            if (Directory.Exists(path)) throw VaultException.File(FileCredentialIssue.Directory);
            throw VaultException.File(FileCredentialIssue.NotFound);
        }
        if (info.LinkTarget is not null) throw VaultException.File(FileCredentialIssue.SymbolicLink);
        if ((info.Attributes & FileAttributes.Directory) != 0) throw VaultException.File(FileCredentialIssue.Directory);
        if (info.Length > MaxByteCount) throw VaultException.File(FileCredentialIssue.TooLarge);

        var expected = Identify(path);
        checkpoint(FreezeCheckpoint.AfterPathCheck);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            65_536, FileOptions.SequentialScan);
        var opened = Identify(path);
        if (opened != expected) throw VaultException.File(FileCredentialIssue.ReplacedDuringRead);
        checkpoint(FreezeCheckpoint.AfterOpen);

        using var buffer = new MemoryStream();
        var chunk = new byte[65_536];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxByteCount) throw VaultException.File(FileCredentialIssue.TooLarge);
        }
        var after = Identify(path);
        if (after != expected || buffer.Length != expected.Size)
        {
            throw VaultException.File(FileCredentialIssue.ReplacedDuringRead);
        }
        return new FrozenFile(Path.GetFileName(path), buffer.ToArray());
    }

    private readonly record struct Identity(long Size, DateTime LastWriteUtc, DateTime CreationUtc, FileAttributes Attributes);

    private static Identity Identify(string path)
    {
        var info = new FileInfo(path);
        info.Refresh();
        if (!info.Exists) throw VaultException.File(FileCredentialIssue.NotFound);
        if (info.LinkTarget is not null) throw VaultException.File(FileCredentialIssue.SymbolicLink);
        if ((info.Attributes & FileAttributes.Directory) != 0) throw VaultException.File(FileCredentialIssue.Directory);
        return new Identity(info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc, info.Attributes);
    }
}

