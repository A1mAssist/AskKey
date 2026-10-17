using System.Windows;
using System.Windows.Threading;

namespace AskKeyApp;

public partial class App : Application
{
    private AppHost? _host;
    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private EventWaitHandle? _showEvent;
    private static int _handlingUnhandledException;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
#if DEBUG
        Environment.SetEnvironmentVariable("ASKKEY_DEVELOPMENT", "1");
#endif
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => AppearanceManager.ApplyToWindow((Window)sender)));

        _instanceMutex = new Mutex(initiallyOwned: true, @"Local\AskKey.SingleInstance", out var created);
        _ownsMutex = created;
        if (!created)
        {
            try
            {
                EventWaitHandle.OpenExisting(@"Local\AskKey.Show").Set();
            }
            catch (Exception)
            {
            }
            Shutdown();
            return;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AskKey.Show");
        _host = new AppHost(Dispatcher);
        Loc.Initialize(_host.Preferences.LanguageMode);
        AppearanceManager.Apply(_host.Preferences.AppearanceMode);
        _host.Start();

        var waiter = new Thread(() =>
        {
            while (_showEvent.WaitOne())
            {
                Dispatcher.BeginInvoke(() => _host?.ShowMainWindow());
            }
        })
        { IsBackground = true, Name = "AskKey instance activation" };
        waiter.Start();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        // A failing layout can re-enter this handler while MessageBox renders.
        // Show at most one dialog; nested failures are diagnostic-only.
        if (Interlocked.Exchange(ref _handlingUnhandledException, 1) == 1)
        {
            System.Diagnostics.Debug.WriteLine(e.Exception);
            return;
        }
        try
        {
            MessageBox.Show(
                e.Exception.Message,
                Loc.T("Ask Key"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            Interlocked.Exchange(ref _handlingUnhandledException, 0);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        if (_ownsMutex)
        {
            try { _instanceMutex?.ReleaseMutex(); } catch (ApplicationException) { }
        }
        _instanceMutex?.Dispose();
        _showEvent?.Dispose();
        base.OnExit(e);
    }
}

