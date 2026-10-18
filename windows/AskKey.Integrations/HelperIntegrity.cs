using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AskKey.Integrations;

/// <summary>
/// Verifies the helper payload against the manifest embedded in the host.
/// The manifest covers every file in the helper directory; a missing, changed,
/// or extra file fails the check. The host assembly is the trust root: this
/// detects helper replacement, not a replaced host.
/// </summary>
public sealed class HelperIntegrity
{
    public const string ResourceName = "AskKey.HelperIntegrity.manifest";

    private static readonly Lazy<HelperIntegrity> Embedded = new(LoadEmbedded);
    private readonly Dictionary<string, string> _files;

    public static HelperIntegrity FromAssembly() => Embedded.Value;

    public HelperIntegrity(string manifestJson)
    {
        _files = Parse(manifestJson);
    }

    public bool IsTrusted(string helperPath)
    {
        if (_files.Count == 0 || !HelperTrust.IsExecutableRegularFile(helperPath)) return false;

        try
        {
            var root = Path.GetDirectoryName(Path.GetFullPath(helperPath));
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return false;
            if (WindowsFileSecurity.IsLinkOrReparse(root)) return false;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false,
                ReturnSpecialDirectories = false,
            };
            foreach (var file in Directory.EnumerateFiles(root, "*", options))
            {
                if (WindowsFileSecurity.IsLinkOrReparse(file)) return false;
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (!_files.TryGetValue(relative, out var expected)) return false;
                if (!seen.Add(relative)) return false;

                using var stream = new FileStream(
                    file, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                    FileOptions.SequentialScan);
                var actual = Convert.ToHexString(SHA256.HashData(stream));
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return seen.Count == _files.Count;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static Dictionary<string, string> Parse(string manifestJson)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var document = JsonDocument.Parse(manifestJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var value)
                || value != 1
                || !root.TryGetProperty("files", out var entries)
                || entries.ValueKind != JsonValueKind.Object)
            {
                return files;
            }

            foreach (var entry in entries.EnumerateObject())
            {
                var relative = Normalize(entry.Name);
                if (relative is null) return new();
                var hash = entry.Value.ValueKind == JsonValueKind.String
                    ? entry.Value.GetString()
                    : null;
                if (hash is null || hash.Length != 64 || !IsHex(hash))
                {
                    return new();
                }
                if (!files.TryAdd(relative, hash.ToLowerInvariant()))
                {
                    return new();
                }
            }
        }
        catch (JsonException)
        {
            return new();
        }
        return files;
    }

    private static string? Normalize(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (normalized.Length == 0 || Path.IsPathRooted(normalized)) return null;
        foreach (var segment in normalized.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == ".." || segment.Contains(':')) return null;
        }
        return normalized;
    }

    private static bool IsHex(string value)
    {
        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character)) return false;
        }
        return true;
    }

    private static HelperIntegrity LoadEmbedded()
    {
        try
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(HelperIntegrity).Assembly;
            using var stream = assembly.GetManifestResourceStream(ResourceName);
            if (stream is null) return new HelperIntegrity("{}");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return new HelperIntegrity(reader.ReadToEnd());
        }
        catch (Exception exception) when (exception is IOException or ArgumentException)
        {
            return new HelperIntegrity("{}");
        }
    }
}
