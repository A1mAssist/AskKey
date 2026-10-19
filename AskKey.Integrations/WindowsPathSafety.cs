using System.Security.AccessControl;
using System.Security.Principal;

namespace AskKey.Integrations;

/// <summary>
/// Windows equivalent of the Swift lstat walk: no reparse point anywhere on the
/// path, the leaf is the expected kind, and the leaf is owned by this user.
/// </summary>
internal static class WindowsPathSafety
{
    public static void Inspect(string path, CommandDiscoveryHookConfigurationError error, bool requirePrivateLeaf = false)
    {
        foreach (var prefix in Prefixes(path))
        {
            if (!File.Exists(prefix) && !Directory.Exists(prefix))
            {
                if (WindowsFileSecurity.IsLinkOrReparse(prefix)) throw new CommandDiscoveryHookConfigurationException(error);
                continue;
            }
            if (WindowsFileSecurity.IsLinkOrReparse(prefix))
            {
                throw new CommandDiscoveryHookConfigurationException(error);
            }
            var isLeaf = string.Equals(prefix, Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase);
            if (isLeaf && requirePrivateLeaf && !OwnedByCurrentUser(prefix))
            {
                throw new CommandDiscoveryHookConfigurationException(error);
            }
        }
    }

    public static void EnsureDirectory(string path, CommandDiscoveryHookConfigurationError error, bool requirePrivateLeaf = false)
    {
        var standardized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var missing = new List<string>();
        foreach (var prefix in Prefixes(standardized))
        {
            if (Directory.Exists(prefix))
            {
                if (WindowsFileSecurity.IsLinkOrReparse(prefix))
                {
                    throw new CommandDiscoveryHookConfigurationException(error);
                }
                var isLeaf = string.Equals(prefix, standardized, StringComparison.OrdinalIgnoreCase);
                if (isLeaf && requirePrivateLeaf && !OwnedByCurrentUser(prefix))
                {
                    throw new CommandDiscoveryHookConfigurationException(error);
                }
                continue;
            }
            if (File.Exists(prefix)) throw new CommandDiscoveryHookConfigurationException(error);
            if (WindowsFileSecurity.IsLinkOrReparse(prefix)) throw new CommandDiscoveryHookConfigurationException(error);
            missing.Add(prefix);
        }
        foreach (var directory in missing)
        {
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (IOException)
            {
                if (!Directory.Exists(directory)) throw new CommandDiscoveryHookConfigurationException(error);
            }
            catch (UnauthorizedAccessException)
            {
                throw new CommandDiscoveryHookConfigurationException(error);
            }
            if (!Directory.Exists(directory) || WindowsFileSecurity.IsLinkOrReparse(directory))
            {
                throw new CommandDiscoveryHookConfigurationException(error);
            }
            if (!OwnedByCurrentUser(directory))
            {
                throw new CommandDiscoveryHookConfigurationException(error);
            }
            WindowsFileSecurity.ApplyMode(directory, WindowsFileSecurity.PrivateMode);
        }
    }

    /// <summary>Root-to-leaf prefixes without resolving links, so a link above an
    /// existing child cannot hide behind a normalized path.</summary>
    private static IEnumerable<string> Prefixes(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        var current = root;
        if (root.Length > 0) yield return root;
        foreach (var component in full[root.Length..].Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = current.Length == 0 ? component : Path.Combine(current, component);
            yield return current;
        }
    }

    public static bool OwnedByCurrentUser(string path)
    {
        try
        {
            var identity = Broker.Transport.BrokerAcl.CurrentUser;
            FileSystemSecurity security = Directory.Exists(path)
                ? new DirectoryInfo(path).GetAccessControl()
                : new FileInfo(path).GetAccessControl();
            var owner = security.GetOwner(typeof(SecurityIdentifier));
            return owner is not null && owner.Equals(identity);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
