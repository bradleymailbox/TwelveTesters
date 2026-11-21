using System.Windows;
using System.Windows.Threading;
using TwelveTestersTray.Services;

namespace TwelveTestersTray;

public partial class App : System.Windows.Application
{
    // Keep this value in sync with the installer's AppMutex when item 25 is implemented.
    public const string MutexName = "bDevStudios.TwelveTestersTray.SingleInstance";
    private const string ShowEventName = "bDevStudios.TwelveTestersTray.Show";

    private Mutex? mutex;
    private EventWaitHandle? showEvent;
    private RegisteredWaitHandle? showRegistration;
    private TrayContext? tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        mutex = new Mutex(true, MutexName, out var isFirst);
        showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!isFirst)
        {
            showEvent.Set();
            Shutdown();
            return;
        }

        base.OnStartup(e);

        var settings = Settings.Load();
        ThemeManager.Apply(settings.Theme);
        try { StartupRegistration.RepairExisting(); }
        catch (Exception ex) { Log.Write("Startup registration repair failed: " + ex.Message); }

        var window = new MainWindow();
        MainWindow = window;
        tray = new TrayContext();
        window.Attach(tray);

        showRegistration = ThreadPool.RegisterWaitForSingleObject(showEvent,
            (_, _) =>
            {
                Dispatcher.BeginInvoke(() => window.ShowFromTray());
            }, null, Timeout.Infinite, false);

        if (!e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase)))
            window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        showRegistration?.Unregister(null);
        tray?.Stop();
        showEvent?.Dispose();
        if (mutex is not null)
        {
            try { mutex.ReleaseMutex(); } catch (ApplicationException) { /* second process */ }
            mutex.Dispose();
        }
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Write("Unhandled UI error: " + e.Exception);
        System.Windows.MessageBox.Show(e.Exception.Message, "Twelve Testers Tray", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Write("Unobserved task error: " + e.Exception);
        e.SetObserved();
    }
}
