using System.Security.AccessControl;
using System.Security.Principal;

namespace AskKey.Broker.Transport;

/// <summary>
/// Windows ACL helpers. Both the runtime directory and each named pipe are owned
/// exclusively by the current user with inheritance disabled, the counterpart of
/// Swift's 0700 directory and 0600 socket.
/// </summary>
public static class BrokerAcl
{
    public static SecurityIdentifier CurrentUser
        => WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("current user SID unavailable");

    public static void SecureDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists) throw new DirectoryNotFoundException(path);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            CurrentUser, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit
                | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    /// <summary>True when the directory grants only the current user and inherits nothing.</summary>
    public static bool IsPrivateDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists) return false;
        var security = info.GetAccessControl();
        if (!security.AreAccessRulesProtected) return false;
        var currentUser = CurrentUser;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (!rule.IdentityReference.Equals(currentUser)) return false;
        }
        return true;
    }

    /// <summary>0600-equivalent file ACL: current user only, inheritance removed.</summary>
    public static void SecureFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException(path);
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            CurrentUser, FileSystemRights.FullControl, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    public static global::System.IO.Pipes.PipeSecurity PrivatePipeSecurity()
    {
        var security = new global::System.IO.Pipes.PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new global::System.IO.Pipes.PipeAccessRule(
            CurrentUser, global::System.IO.Pipes.PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }
}
