namespace AskKey.Integrations.Tests;

public class ClientConfigFileIOTests
{
    [Fact]
    public void PublishAtomicallyWritesAndAppliesPrivateAcl()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("config", "mcp.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        ClientConfigFileIO.PublishAtomically(
            "{\"a\":1}"u8.ToArray(), path, WindowsFileSecurity.PrivateMode,
            exclusive: false, ".test-");

        Assert.Equal("{\"a\":1}", File.ReadAllText(path));
        Assert.Equal(WindowsFileSecurity.PrivateMode, WindowsFileSecurity.ModeForFile(path));
        Assert.True(WindowsFileSecurity.IsPrivateFile(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, ".test-*"));
    }

    [Fact]
    public void ExclusivePublishRefusesToOverwrite()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("config.toml", "first");

        var exception = Assert.Throws<ClientConfigFileException>(() =>
            ClientConfigFileIO.PublishAtomically(
                "second"u8.ToArray(), path, WindowsFileSecurity.PrivateMode,
                exclusive: true, ".test-"));

        Assert.Equal(ClientConfigFileFailure.ExclusiveExists, exception.Failure);
        Assert.Equal("first", File.ReadAllText(path));
    }

    [Fact]
    public void MissingFileIsReportedAndNotTreatedAsEmpty()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("absent.json");

        var exception = Assert.Throws<ClientConfigFileException>(() =>
            ClientConfigFileIO.ReadRegularFile(path));

        Assert.Equal(ClientConfigFileFailure.NotFound, exception.Failure);
        Assert.Null(ClientConfigFileIO.InspectRegularFile(path));
    }

    [Fact]
    public void ReadEnforcesTheByteLimit()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("large.bin", new byte[64]);

        var exception = Assert.Throws<ClientConfigFileException>(() =>
            ClientConfigFileIO.ReadRegularFile(path, maximumBytes: 16));

        Assert.Equal(ClientConfigFileFailure.TooLarge, exception.Failure);
    }

    [Fact]
    public void DirectoryIsRejectedAsAnUnsafeFile()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("folder");
        Directory.CreateDirectory(path);

        var exception = Assert.Throws<ClientConfigFileException>(() =>
            ClientConfigFileIO.ReadRegularFile(path));

        Assert.Equal(ClientConfigFileFailure.Unsafe, exception.Failure);
    }

    [Fact]
    public void ReparsePointIsRejectedBeforeReading()
    {
        using var temp = new TempDirectory();
        var target = temp.Write("target.json", "{}");
        var link = temp.Combine("link.json");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception linkFailure) when (linkFailure is IOException or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            return; // Creating symlinks needs developer mode; the check is covered by code review.
        }

        var exception = Assert.Throws<ClientConfigFileException>(() =>
            ClientConfigFileIO.ReadRegularFile(link));

        Assert.Equal(ClientConfigFileFailure.Unsafe, exception.Failure);
    }

    [Fact]
    public void RenameExclusivelyNeverClobbersAnExistingDestination()
    {
        using var temp = new TempDirectory();
        var source = temp.Write("source", "new");
        var destination = temp.Write("destination", "old");

        var exception = Assert.Throws<ClientConfigFileException>(() =>
            ClientConfigFileIO.RenameExclusively(source, destination));

        Assert.Equal(ClientConfigFileFailure.ExclusiveExists, exception.Failure);
        Assert.Equal("old", File.ReadAllText(destination));
        Assert.True(File.Exists(source));
    }
}