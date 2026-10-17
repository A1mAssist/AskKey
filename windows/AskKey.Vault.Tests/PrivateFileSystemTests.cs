using System.Security.AccessControl;
using System.Security.Principal;
using AskKey.Vault;

namespace AskKey.Vault.Tests;

public sealed class PrivateFileSystemTests
{
    [Fact]
    public void PrivateDirectoryGrantsOnlyTheCurrentUserWithProtectedDacl()
    {
        using var root = new TestDirectory();
        var path = root.Combine("private");
        PrivateFileSystem.CreatePrivateDirectory(path);

        Assert.True(PrivateFileSystem.IsPrivateDirectory(path));
        var security = new DirectoryInfo(path).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        var sid = WindowsIdentity.GetCurrent().User!;
        var allow = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(rule => rule.AccessControlType == AccessControlType.Allow)
            .ToList();
        Assert.NotEmpty(allow);
        Assert.All(allow, rule => Assert.Equal(sid, rule.IdentityReference));
        Assert.All(allow, rule => Assert.True(rule.FileSystemRights.HasFlag(FileSystemRights.FullControl)));
        Assert.DoesNotContain(security.GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>(),
            rule => rule.AccessControlType == AccessControlType.Allow
                && !rule.IdentityReference.Equals(sid));
    }

    [Fact]
    public void PrivateFileGrantsOnlyTheCurrentUserWithProtectedDacl()
    {
        using var root = new TestDirectory();
        PrivateFileSystem.CreatePrivateDirectory(root.Path);
        var path = root.Combine("secret.bin");
        using (var stream = PrivateFileSystem.CreatePrivateFile(path, FileMode.CreateNew, FileAccess.Write,
                   FileShare.None))
        {
            stream.Write(new byte[] { 1, 2, 3 });
        }

        Assert.True(PrivateFileSystem.IsPrivateFile(path));
        var security = new FileInfo(path).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        var sid = WindowsIdentity.GetCurrent().User!;
        var allow = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(rule => rule.AccessControlType == AccessControlType.Allow)
            .ToList();
        Assert.NotEmpty(allow);
        Assert.All(allow, rule => Assert.Equal(sid, rule.IdentityReference));
    }

    [Fact]
    public void InheritedOnlyAclIsNotConsideredPrivate()
    {
        using var root = new TestDirectory();
        var path = root.Combine("inherited.txt");
        File.WriteAllText(path, "synthetic");
        Assert.False(PrivateFileSystem.IsPrivateFile(path));

        var directory = root.Combine("inherited-dir");
        Directory.CreateDirectory(directory);
        Assert.False(PrivateFileSystem.IsPrivateDirectory(directory));
    }

    [Fact]
    public void ApplyingFileAclRemovesForeignAccessAndInheritance()
    {
        using var root = new TestDirectory();
        var path = root.Combine("tightened.txt");
        File.WriteAllText(path, "synthetic");
        PrivateFileSystem.ApplyFile(path);

        var security = new FileInfo(path).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToList();
        Assert.All(rules, rule => Assert.Equal(WindowsIdentity.GetCurrent().User, rule.IdentityReference));
    }
}
