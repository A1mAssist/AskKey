using AskKey.Broker.Transport;

namespace AskKey.Broker.Tests;

public sealed class DebugRunDirectoryReleaseTests
{
    [Fact]
    public void ReleaseBuildIgnoresDebugRunDirectoryAndUsesProductionPipeDirectory()
    {
        var originalDebug = DebugRunDirectory.IsDebugBuild;
        var originalDebugDirectory = Environment.GetEnvironmentVariable(DebugRunDirectory.EnvironmentVariable);
        var originalPipe = Environment.GetEnvironmentVariable(BrokerConfiguration.PipeOverrideVariable);
        var root = ProtocolTestSupport.NewDirectory();
        BrokerAcl.SecureDirectory(root);
        try
        {
#if DEBUG
            Assert.True(DebugRunDirectory.IsDebugBuild);
#else
            Assert.False(DebugRunDirectory.IsDebugBuild);
#endif
            Environment.SetEnvironmentVariable(DebugRunDirectory.EnvironmentVariable, root);
            Environment.SetEnvironmentVariable(BrokerConfiguration.PipeOverrideVariable, null);
            DebugRunDirectory.IsDebugBuild = false;

            Assert.Null(DebugRunDirectory.Resolve());
            var pipeFile = BrokerConfiguration.ResolvePipeNameFile();
            Assert.Equal(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AskKey"),
                Path.GetDirectoryName(pipeFile));

            // The adapters write this into each client config so a helper spawned by an
            // agent CLI finds the Broker; Release builds must keep honouring it.
            var overridden = Path.Combine(root, "client.pipe");
            Environment.SetEnvironmentVariable(BrokerConfiguration.PipeOverrideVariable, overridden);
            Assert.Equal(overridden, BrokerConfiguration.ResolvePipeNameFile());
        }
        finally
        {
            DebugRunDirectory.IsDebugBuild = originalDebug;
            Environment.SetEnvironmentVariable(DebugRunDirectory.EnvironmentVariable, originalDebugDirectory);
            Environment.SetEnvironmentVariable(BrokerConfiguration.PipeOverrideVariable, originalPipe);
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
