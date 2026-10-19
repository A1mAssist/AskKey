using AskKey.Broker.Transport;

namespace AskKeyApp;

internal static class AppPaths
{
    public static string HelperPath => ResolveHelperPath();

    public static string BrokerPipeFile => BrokerConfiguration.ResolvePipeNameFile();

    private static string ResolveHelperPath()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "Helpers", "askkey.exe");
        if (File.Exists(bundled)) return bundled;
        var besideApp = Path.Combine(AppContext.BaseDirectory, "askkey.exe");
        if (File.Exists(besideApp)) return besideApp;
        var development = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "AskKey.Helper", "bin", "Debug", "net10.0-windows", "askkey.exe"));
        return File.Exists(development) ? development : bundled;
    }
}
