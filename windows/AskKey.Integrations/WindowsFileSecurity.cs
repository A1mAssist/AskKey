using System.Security.AccessControl;
using System.Security.Principal;

namespace AskKey.Integrations;

/// <summary>
/// Windows stand-ins for the POSIX mode checks the Swift port relied on.
/// 0600 means an ACL protected to the current user; anything else is 0644.
/// </summary>
internal static class WindowsFileSecurity
{
    public const int PrivateMode = 0x180; // 0o600
    public const int SharedMode = 0x1A4;  // 0o644

    public static int ModeForFile(string path) => IsPrivateFile(path) ? PrivateMode : SharedMode;

    public static bool IsPrivateFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return false;
        FileSecurity security;
        try { security = info.GetAccessControl(); }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException) { return false; }
        if (!security.AreAccessRulesProtected) return false;
        var current = Broker.Transport.BrokerAcl.CurrentUser;
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (!rule.IdentityReference.Equals(current)) return false;
        }
        return true;
    }

    public static void SecureFile(string path) => Broker.Transport.BrokerAcl.SecureFile(path);

    /// <summary>Applies the closest Windows equivalent: 0600 becomes a private ACL,
    /// 0644 restores inherited access. A temporary file is created private, so a
    /// shared target must explicitly unprotect its ACL or the mode check fails.</summary>
    public static void ApplyMode(string path, int mode)
    {
        if ((mode & 0x3F) != 0)
        {
            MakeShared(path);
            return;
        }
        if (Directory.Exists(path)) Broker.Transport.BrokerAcl.SecureDirectory(path);
        else SecureFile(path);
    }

    private static void MakeShared(string path)
    {
        if (Directory.Exists(path))
        {
            var info = new DirectoryInfo(path);
            var security = info.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
            info.SetAccessControl(security);
            return;
        }
        var file = new FileInfo(path);
        var fileSecurity = file.GetAccessControl();
        fileSecurity.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
        file.SetAccessControl(fileSecurity);
    }

    public static bool IsReparsePoint(string path)
    {
        var attributes = File.GetAttributes(path);
        return (attributes & FileAttributes.ReparsePoint) != 0;
    }

    public static bool IsLinkOrReparse(string path)
    {
        try { return IsReparsePoint(path); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}
