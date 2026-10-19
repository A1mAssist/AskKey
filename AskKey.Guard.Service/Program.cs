using System.ServiceProcess;
using AskKey.Guard;

namespace AskKey.Guard.Service;

internal static class Program
{
    private static int Main(string[] args)
    {
        var development = HasFlag(args, "--dev")
            || GuardProtocol.IsDevelopmentEnvironment;
        if (HasFlag(args, "--service"))
        {
            ServiceBase.Run(new GuardWindowsService(development));
            return 0;
        }
        if (HasFlag(args, "--console"))
        {
            return RunConsole(development);
        }

        Console.Error.WriteLine("usage: AskKey.Guard.Service.exe --service | --console [--dev]");
        return 2;
    }

    private static int RunConsole(bool development)
    {
        var pipeName = GuardProtocol.SelectPipeName(development);
        using var server = new GuardServer(
            GuardStateStore.ForCurrentEnvironment(development),
            new GuardCallerVerifier(development),
            pipeName);
        server.Start();
        Console.WriteLine(
            $"AskKey Guard listening on {pipeName} ({(development ? "development" : "production")}).");

        using var stopped = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stopped.Set();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => stopped.Set();
        stopped.Wait();
        return 0;
    }

    private static bool HasFlag(string[] args, string flag)
        => args.Any(value => string.Equals(value, flag, StringComparison.OrdinalIgnoreCase));
}
