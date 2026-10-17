using System.Text.Json;

namespace AskKey.Integrations.Tests;

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "askkey-integrations-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Combine(params string[] parts)
        => parts.Aggregate(Root, Path.Combine);

    public string Write(string relativePath, string contents)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    public string WriteBytes(string relativePath, byte[] contents)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, contents);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed class MapAuthenticodeVerifier : IAuthenticodeVerifier
{
    private readonly Dictionary<string, string?> _subjects = new(StringComparer.OrdinalIgnoreCase);

    public MapAuthenticodeVerifier Sign(string path, string subject)
    {
        _subjects[Path.GetFullPath(path)] = subject;
        return this;
    }

    public string? SignerSubject(string path)
        => _subjects.GetValueOrDefault(Path.GetFullPath(path));
}

internal static class TestFiles
{
    public static string WriteExecutable(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // A minimal PE-looking header is enough for the helper trust check; the
        // tests never execute this file.
        File.WriteAllBytes(path, new byte[] { (byte)'M', (byte)'Z', 0, 0, 0, 0 });
        return path;
    }

    /// <summary>Copies the built PE fixture into a role-specific name. The
    /// fixture dispatches on its file name, so no shell or script wrapper is used.</summary>
    public static string InstallFixture(string destination)
    {
        var sourceDirectory = Path.Combine(AppContext.BaseDirectory, "fixture");
        var source = Path.Combine(sourceDirectory, "AskKey.Integrations.TestFixture.exe");
        if (!File.Exists(source)) throw new InvalidOperationException($"missing fixture at {source}");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var name = Path.GetFileNameWithoutExtension(destination);
        var target = Path.GetDirectoryName(destination)!;
        // Keep the original assembly/deps names so the apphost resolves its
        // runtimeconfig and managed assembly, then add the role-specific copy.
        foreach (var file in Directory.GetFiles(sourceDirectory))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }
        File.Copy(source, destination, overwrite: true);
        return destination;
    }

    public static string CopyRealExecutable(string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(Path.Combine(Environment.SystemDirectory, "where.exe"), destination);
        return destination;
    }

    public static byte[] Json(object value) => JsonSerializer.SerializeToUtf8Bytes(value);
}