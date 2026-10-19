using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace AskKey.Guard;

/// <summary>
/// Per-machine state is readable only by the service account and administrators.
/// Per-user public keys are readable only by the service account.
/// </summary>
internal static class GuardAcl
{
    public static void SecureDirectory(string path, bool includeAdministrators)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists) throw new DirectoryNotFoundException(path);
        ThrowIfReparsePoint(info);
        VerifyOwner(info, includeAdministrators);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        if (includeAdministrators) AddAdministrators(security);
        info.SetAccessControl(security);
    }

    public static void SecureFile(string path, bool includeAdministrators)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException(path);
        ThrowIfReparsePoint(info);
        VerifyOwner(info, includeAdministrators);
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        if (includeAdministrators) AddAdministrators(security);
        info.SetAccessControl(security);
    }

    /// <summary>The per-machine pipe accepts local authenticated users; only the
    /// service creates instances or changes the pipe ACL.</summary>
    public static PipeSecurity CreateServicePipeSecurity()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var current = WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new PipeAccessRule(
            current, PipeAccessRights.FullControl, AccessControlType.Allow));
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        if (!current.Equals(administrators))
        {
            security.AddAccessRule(new PipeAccessRule(
                administrators, PipeAccessRights.FullControl, AccessControlType.Allow));
        }
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        return security;
    }

    /// <summary>
    /// The service runs as LocalService. A standard user can pre-create
    /// ProgramData\AskKey before the service starts, so ownership is checked
    /// before the service trusts or rewrites the directory ACL.
    /// </summary>
    public static void VerifyTrustedParent(string path, bool includeAdministrators)
    {
        var parent = Directory.GetParent(Path.GetFullPath(path));
        if (parent is null || !parent.Exists) return;
        ThrowIfReparsePoint(parent);
        VerifyOwner(parent, includeAdministrators);
    }

    private static void VerifyOwner(FileSystemInfo info, bool includeAdministrators)
    {
        FileSystemSecurity security = info switch
        {
            DirectoryInfo directory => directory.GetAccessControl(),
            FileInfo file => file.GetAccessControl(),
            _ => throw new UnauthorizedAccessException("guard path has no owner"),
        };
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null) throw new UnauthorizedAccessException("guard path has no owner");
        var current = WindowsIdentity.GetCurrent().User;
        if (current is not null && owner.Equals(current)) return;
        if (includeAdministrators
            && (owner.Equals(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null))
                || owner.Equals(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null))))
        {
            return;
        }
        throw new UnauthorizedAccessException("guard path is owned by another user");
    }

    private static void ThrowIfReparsePoint(FileSystemInfo info)
    {
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("guard path is a reparse point");
    }

    private static void AddAdministrators(FileSystemSecurity security)
    {
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var current = WindowsIdentity.GetCurrent().User;
        if (current is not null && current.Equals(administrators)) return;
        security.AddAccessRule(security is DirectorySecurity
            ? new FileSystemAccessRule(
                administrators,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow)
            : new FileSystemAccessRule(
                administrators, FileSystemRights.FullControl, AccessControlType.Allow));
    }
}
