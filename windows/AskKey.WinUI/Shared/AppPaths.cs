using AskKey.Broker.Runtime;
using AskKey.Broker.Transport;

namespace AskKeyApp;

internal static class AppPaths
{
    public static string HelperPath => ResolveHelperPath();

    public static string BrokerPipeFile => BrokerConfiguration.ResolvePipeNameFile();

    /// <summary>
    /// Gate the app must pass before it starts a Broker: the helper must sit in
    /// Helpers\ beside the host, and outside development the pair must live in an
    /// official install root. Returns the accepted helper path.
    /// </summary>
    public static string RequireOfficialHelperPath()
        => OfficialInstallTopology.ResolvedHelperPath(
            AppContext.BaseDirectory,
            HelperPath,
            Environment.GetEnvironmentVariable("ASKKEY_DEVELOPMENT") == "1");

    private static string ResolveHelperPath()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "Helpers", "askkey.exe");
        if (File.Exists(bundled)) return bundled;
        var besideApp = Path.Combine(AppContext.BaseDirectory, "askkey.exe");
        if (File.Exists(besideApp)) return besideApp;
        #if DEBUG
        // Repository-layout fallback: a Debug build never installs, so it may run the
        // freshly compiled helper. A Release build must not reach into the build tree.
        var development = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "AskKey.Helper", "bin", "Debug", "net10.0-windows", "askkey.exe"));
        if (File.Exists(development)) return development;
#endif
        return bundled;
    }
}
