using System.ServiceProcess;
using AskKey.Guard;

namespace AskKey.Guard.Service;

internal sealed class GuardWindowsService : ServiceBase
{
    private readonly bool _development;
    private GuardServer? _server;

    public GuardWindowsService(bool development)
    {
        _development = development;
        ServiceName = GuardProtocol.ServiceName;
        CanStop = true;
        AutoLog = true;
    }

    protected override void OnStart(string[] args)
    {
        _server = new GuardServer(
            GuardStateStore.ForCurrentEnvironment(_development),
            new GuardCallerVerifier(_development),
            GuardProtocol.SelectPipeName(_development));
        _server.Start();
    }

    protected override void OnStop()
    {
        _server?.Dispose();
        _server = null;
    }

    protected override void OnShutdown()
    {
        _server?.Dispose();
        _server = null;
        base.OnShutdown();
    }
}
