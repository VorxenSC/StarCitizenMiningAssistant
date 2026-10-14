using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using MiningAssistant.App.Services;

namespace MiningAssistant.App;

public partial class App : Application
{
    private const string MutexName = "StarCitizenMiningAssistant.SingleInstance";
    private const string ShowEventName = "StarCitizenMiningAssistant.ShowPanel";

    private AppHost? _host;
    private Mutex? _single;
    private EventWaitHandle? _showEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (_, ex) => WriteCrash("Unhandled (domain)", ex.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, ex) =>
        {
            WriteCrash("Unhandled (UI)", ex.Exception);
            MessageBox.Show(ex.Exception.Message + "\n\nDetails: " + CrashLogPath, "Mining Assistant — error", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };

        // "--selftest": check this PC headlessly, write config/selftest.txt and exit (no window).
        if (e.Args.Contains("--selftest"))
        {
            int code;
            try { code = SelfTest.Run(); }
            catch (Exception ex) { WriteCrash("Self-test failed", ex); code = 2; }
            Shutdown(code);
            return;
        }

        // "--restart": started by a running instance that is closing; wait for it to let go.
        var restarting = e.Args.Contains("--restart");
        _single = new Mutex(false, MutexName);
        bool owned;
        try { owned = _single.WaitOne(restarting ? TimeSpan.FromSeconds(8) : TimeSpan.Zero); }
        catch (AbandonedMutexException) { owned = true; }

        if (!owned)
        {
            // Already running: ask that instance to show its control panel instead of doing nothing.
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); }
            catch { MessageBox.Show("Mining Assistant is already running (see the tray icon).", "Mining Assistant"); }
            _single.Dispose();
            _single = null;
            Shutdown();
            return;
        }

        try
        {
            _host = new AppHost();
            _host.RestartRequested += Restart;
            _host.Start();
            ListenForShowRequests();
        }
        catch (Exception ex)
        {
            WriteCrash("Startup failed", ex);
            MessageBox.Show("Mining Assistant could not start:\n\n" + ex.Message + "\n\nDetails were written to:\n" + CrashLogPath,
                "Mining Assistant", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>A second launch of the exe signals this event; show the control panel.</summary>
    private void ListenForShowRequests()
    {
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var thread = new Thread(() =>
        {
            while (true)
            {
                try { _showEvent.WaitOne(); }
                catch (ObjectDisposedException) { return; }
                Dispatcher.BeginInvoke(() => _host?.ShowSettings());
            }
        }) { IsBackground = true, Name = "MiningAssistant.ShowPanel" };
        thread.Start();
    }

    private void Restart()
    {
        var exe = Environment.ProcessPath;
        _host?.Dispose();
        _host = null;
        ReleaseSingleInstance();
        if (exe is not null) Process.Start(new ProcessStartInfo(exe, "--restart") { UseShellExecute = false });
        Shutdown();
    }

    private void ReleaseSingleInstance()
    {
        try { _single?.ReleaseMutex(); } catch (ApplicationException) { /* not owned */ }
        _single?.Dispose();
        _single = null;
    }

    private static string CrashLogPath
    {
        get
        {
            try
            {
                var dir = Path.Combine(AppContext.BaseDirectory, "config");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "crash.log");
            }
            catch { return Path.Combine(Path.GetTempPath(), "MiningAssistant-crash.log"); }
        }
    }

    private static void WriteCrash(string kind, Exception? ex)
    {
        try { File.AppendAllText(CrashLogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {kind}: {ex}{Environment.NewLine}{Environment.NewLine}"); }
        catch { /* nothing else we can do */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        _showEvent?.Dispose();
        ReleaseSingleInstance();
        base.OnExit(e);
    }
}
