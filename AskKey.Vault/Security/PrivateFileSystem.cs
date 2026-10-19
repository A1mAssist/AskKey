using System.Security.AccessControl;
using System.Security.Principal;

namespace AskKey.Vault;

/// <summary>
/// Replaces POSIX 0700/0600 with an explicit DACL: inheritance disabled and
/// FullControl granted only to the current user SID. Never relies on inherited ACLs.
/// </summary>
public static class PrivateFileSystem
{
    public static SecurityIdentifier CurrentUserSid
        => WindowsIdentity.GetCurrent().User
           ?? throw new VaultException(VaultErrorCode.VaultStorageUnavailable,
               "The current Windows user could not be resolved for private storage.");

    public static void CreatePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        ApplyDirectory(path);
    }

    public static void ApplyDirectory(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(CurrentUserSid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    public static void ApplyFile(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(CurrentUserSid, FileSystemRights.FullControl,
            AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    /// <summary>True when the ACL is protected and grants only the current SID.</summary>
    public static bool IsPrivateFile(string path)
    {
        if (!File.Exists(path)) return false;
        var security = new FileInfo(path).GetAccessControl();
        return IsPrivate(security);
    }

    public static bool IsPrivateDirectory(string path)
    {
        if (!Directory.Exists(path)) return false;
        var security = new DirectoryInfo(path).GetAccessControl();
        return IsPrivate(security);
    }

    private static bool IsPrivate(FileSystemSecurity security)
    {
        if (!security.AreAccessRulesProtected) return false;
        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier));
        var sawAllow = false;
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (!rule.IdentityReference.Equals(CurrentUserSid)) return false;
            sawAllow = true;
        }
        return sawAllow;
    }

    /// <summary>Creates a file with an owner-only DACL before any content is written.</summary>
    public static FileStream CreatePrivateFile(string path, FileMode mode, FileAccess access, FileShare share)
    {
        var options = new FileStreamOptions
        {
            Mode = mode,
            Access = access,
            Share = share,
            Options = FileOptions.WriteThrough,
        };
        var stream = new FileStream(path, options);
        ApplyFile(path);
        return stream;
    }
}
