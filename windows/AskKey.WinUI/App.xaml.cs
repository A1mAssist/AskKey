using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace AskKeyApp;

public partial class App : Application
{
    private AppHost? _host;
    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private EventWaitHandle? _showEvent;
    private static int _handlingUnhandledException;

    public static new App Current => (App)Application.Current;
    public static Window? CurrentWindow { get; internal set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        var instanceSuffix = Environment.GetEnvironmentVariable("ASKKEY_DEVELOPMENT") == "1" ? ".dev" : "";
        var mutexName = @"Local\AskKey.SingleInstance" + instanceSuffix;
        var showEventName = @"Local\AskKey.Show" + instanceSuffix;
        _instanceMutex = new Mutex(initiallyOwned: true, mutexName, out var created);
        _ownsMutex = created;
        if (!created)
        {
            try { EventWaitHandle.OpenExisting(showEventName).Set(); }
            catch { }
            Environment.Exit(0);
            return;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, showEventName);
        _host = new AppHost(dispatcher);
        Loc.Initialize(_host.Preferences.LanguageMode);
        ThemeManager.Apply(_host.Preferences.AppearanceMode);
        _host.Start();

        var waiter = new Thread(() =>
        {
            try
            {
                while (_showEvent.WaitOne())
                {
                    dispatcher.TryEnqueue(() => _host?.ShowMainWindow());
                }
            }
            catch (ObjectDisposedException)
            {
            }
        })
        { IsBackground = true, Name = "AskKey instance activation" };
        waiter.Start();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();
    }

    internal void Shutdown()
    {
        _host?.Dispose();
        _host = null;
        CurrentWindow = null;
        Dispose();
        Exit();
    }

    private void Dispose()
    {
        if (_ownsMutex)
        {
            try { _instanceMutex?.ReleaseMutex(); } catch (ApplicationException) { }
            _ownsMutex = false;
        }
        _instanceMutex?.Dispose();
        _instanceMutex = null;
        _showEvent?.Dispose();
        _showEvent = null;
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        if (Interlocked.Exchange(ref _handlingUnhandledException, 1) == 1)
        {
            System.Diagnostics.Debug.WriteLine(e.Exception);
            return;
        }
        try
        {
            Dialogs.ShowError(e.Exception.Message);
            e.Handled = true;
        }
        finally
        {
            Interlocked.Exchange(ref _handlingUnhandledException, 0);
        }
    }
}
