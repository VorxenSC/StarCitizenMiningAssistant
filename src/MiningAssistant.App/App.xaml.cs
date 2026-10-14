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
    private static bool _headless;
    private Mutex? _single;
    private EventWaitHandle? _showEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _headless = e.Args.Any(a => a.StartsWith("--render-", StringComparison.Ordinal) || a == "--selftest");
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => WriteCrash("Unhandled (domain)", ex.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, ex) =>
        {
            WriteCrash("Unhandled (UI)", ex.Exception);
            ex.Handled = true;
            if (_headless) return; // dev renders / self-test never show dialogs on the user's screen
            MessageBox.Show(ex.Exception.Message + "\n\nDetails: " + CrashLogPath, "Mining Assistant by Vorxen — error", MessageBoxButton.OK, MessageBoxImage.Error);
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

        // "--render-preview <png> [plate.png]": dev check of the unknown-signature pop-up, drawn off-screen.
        var preview = Array.IndexOf(e.Args, "--render-preview");
        if (preview >= 0 && preview + 1 < e.Args.Length)
        {
            var plate = preview + 2 < e.Args.Length && File.Exists(e.Args[preview + 2]) ? File.ReadAllBytes(e.Args[preview + 2]) : null;
            var w = new Windows.UnknownSignatureWindow(new[] { "Gold", "Quantainium", "Tungsten", "Aluminum" });
            w.SetSendOption(true, "Only these values and the plate image go to VorxenSC/StarCitizenMiningAssistant (private). Never the full screen.");
            w.RenderPreview(new Windows.UnknownItem(51000, DateTime.Now, plate), e.Args[preview + 1]);
            Shutdown(0);
            return;
        }

        // "--render-activation <png>" / "--render-requirements <png>" / "--render-panel <png>": dev checks of
        // those windows, drawn off-screen (nothing appears on the user's screen).
        foreach (var flag in new[] { "--render-activation", "--render-requirements", "--render-panel" })
        {
            var at = Array.IndexOf(e.Args, flag);
            if (at < 0 || at + 1 >= e.Args.Length) continue;
            Window w = flag switch
            {
                "--render-activation" => new Windows.ActivationWindow("Vorxen"),
                "--render-requirements" => new Windows.RequirementsWindow("Vorxen", new[]
                {
                    new Windows.DetectedCondition(true, "Game monitor #0: 1920×1080 — resolution OK"),
                    new Windows.DetectedCondition(true, "Text recognition: Windows OCR (en-US)"),
                    new Windows.DetectedCondition(false, "Text recognition missing — example warning"),
                }),
                _ => PreviewPanel(),
            };
            RenderOffscreen(w, e.Args[at + 1]);
            Shutdown(0);
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
            catch { MessageBox.Show("Mining Assistant by Vorxen is already running (see the tray icon).", "Mining Assistant by Vorxen"); }
            _single.Dispose();
            _single = null;
            Shutdown();
            return;
        }

        // License: ask for name + key on first use (or if the stored one is not valid).
        var (license, justActivated) = EnsureLicense();
        if (license is null)
        {
            ReleaseSingleInstance();
            Shutdown();
            return;
        }

        try
        {
            _host = new AppHost { LicensedTo = license.Name, JustActivated = justActivated };
            _host.RestartRequested += Restart;
            _host.Start();
            ListenForShowRequests();
        }
        catch (Exception ex)
        {
            WriteCrash("Startup failed", ex);
            MessageBox.Show("Mining Assistant by Vorxen could not start:\n\n" + ex.Message + "\n\nDetails were written to:\n" + CrashLogPath,
                "Mining Assistant by Vorxen", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static Window PreviewPanel()
    {
        var host = new AppHost { LicensedTo = "Vorxen" };
        var panel = host.CreatePreviewPanel();
        panel.Refresh(new PanelStatus(PanelState.Running, "RUNNING — waiting for an RS indicator",
            new[]
            {
                new HealthItem(true, "Text recognition (Windows OCR)", "Windows OCR (en-US)"),
                new HealthItem(true, "Signature database", "default: 26 ores + 10 other signals"),
                new HealthItem(true, "Resolution", "1920×1080 (1080p or higher is supported)"),
                new HealthItem(true, "Hotkeys", "listening · keys received: 2"),
                new HealthItem(false, "Search area is clear", "this window is on top of the search area — while it is in front, the app sees this panel instead of the game"),
            },
            new[]
            {
                new LiveRow("RS indicator", "not on screen", "(normal when nothing is scanned)"),
                new LiveRow("Read now", "—"),
                new LiveRow("Stable value", "—"),
                new LiveRow("Location", "not detected yet"),
                new LiveRow("Uptime", "00:00:20 · reloads 0"),
            },
            null, true, 0, true));
        return panel;
    }

    /// <summary>Shows the window far off-screen, renders it (caption bar included) to a PNG and closes it.</summary>
    private static void RenderOffscreen(Window w, string png)
    {
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -20000; w.Top = -20000; w.ShowActivated = false;
        w.MaxHeight = double.PositiveInfinity; // the whole page, not the scrolled view
        w.Show();
        w.UpdateLayout();
        var root = (FrameworkElement)System.Windows.Media.VisualTreeHelper.GetChild(w, 0);
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(root);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using (var fs = File.Create(png)) enc.Save(fs);
    }
    /// <summary>The stored activation if valid; otherwise shows the activation window. Null license = user exited.</summary>
    private static (Core.Licensing.License? license, bool justActivated) EnsureLicense()
    {
        var paths = new Core.Settings.AppPaths();
        Core.Licensing.License? stored = null;
        try
        {
            if (File.Exists(paths.LicenseFile))
                stored = Core.Json.Deserialize<Core.Licensing.License>(File.ReadAllText(paths.LicenseFile));
        }
        catch { /* unreadable → ask again */ }
        if (stored is { IsValid: true }) return (stored, false);

        var window = new Windows.ActivationWindow(stored?.Name);
        if (window.ShowDialog() != true || window.Result is null) return (null, false);
        Core.Json.Save(paths.LicenseFile, window.Result);
        return (window.Result, true);
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
