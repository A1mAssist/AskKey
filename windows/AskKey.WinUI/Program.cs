using Microsoft.UI.Dispatching;
using Velopack;

namespace AskKeyApp;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        // Velopack may restart the app with its parent directory as the working directory.
        // WinUI and .NET native probing must stay anchored to the deployed app directory.
        Environment.CurrentDirectory = AppContext.BaseDirectory;

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            new App();
        });
    }
}
