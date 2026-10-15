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

        _headless = e.Args.Any(a => a.StartsWith("--render-", StringComparison.Ordinal) || a is "--selftest" or "--apply-update" or "--update-check");
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => WriteCrash("Unhandled (domain)", ex.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, ex) =>
        {
            WriteCrash("Unhandled (UI)", ex.Exception);
            ex.Handled = true;
            if (_headless) return; // dev renders / self-test never show dialogs on the user's screen
            MessageBox.Show(ex.Exception.Message + "\n\nDetails: " + CrashLogPath, "Mining Assistant by Vorxen — error", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        // "--apply-update <app folder>": this is the NEW version, started from update\staged by the old one.
        // Wait for the old app to exit, copy this version over it (with rollback) and start it. No window.
        var apply = Array.IndexOf(e.Args, "--apply-update");
        if (apply >= 0 && apply + 1 < e.Args.Length)
        {
            var target = e.Args[apply + 1];
            string result;
            try { result = Updater.Apply(target, startAfter: !e.Args.Contains("--no-start")); }
            catch (Exception ex) { result = "update failed: " + ex.Message; }
            try { File.AppendAllText(Path.Combine(target, "config", "app.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} Update: {result}{Environment.NewLine}"); }
            catch { /* the folder may be read-only; nothing else to do */ }
            Shutdown(0);
            return;
        }

        // "--update-check <report.txt>": diagnostics — check GitHub, download + verify + stage if newer, write a report. No window.
        var check = Array.IndexOf(e.Args, "--update-check");
        if (check >= 0 && check + 1 < e.Args.Length)
        {
            var report = new System.Text.StringBuilder($"current v{Updater.CurrentVersion}{Environment.NewLine}");
            var code = 0;
            try
            {
                var repo = new Core.Settings.AppPaths().LoadSettings().Updates.Repository;
                var m = Task.Run(() => Updater.CheckAsync(repo)).GetAwaiter().GetResult(); // off the UI thread: no deadlock
                report.AppendLine(m is null ? "up to date" : $"newer: v{m.Version} ({m.Size:N0} bytes), signature OK");
                if (m is not null)
                {
                    Task.Run(() => Updater.StageAsync(repo, m)).GetAwaiter().GetResult();
                    report.AppendLine(Updater.Staged() is { } s ? $"staged v{s.Version}: size, SHA-256 and exe version verified" : "staging failed");
                }
            }
            catch (Exception ex) { report.AppendLine("error: " + ex.Message); code = 1; }
            File.WriteAllText(e.Args[check + 1], report.ToString());
            Shutdown(code);
            return;
        }

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
                "--render-activation" => new Windows.ActivationWindow("Vorxen", Services.MachineIdentity.Code),
                "--render-requirements" => new Windows.RequirementsWindow("Vorxen", new[]
                {
                    new Windows.DetectedCondition(true, "Game monitor #0: 1920×1080 — resolution OK"),
                    new Windows.DetectedCondition(true, "Text recognition: Windows OCR (en-US)"),
                    new Windows.DetectedCondition(false, "Text recognition missing — example warning"),
                }),
                _ => PreviewPanel(),
            };
            // optional width after the file name, e.g. --render-panel panel.png 806
            if (at + 2 < e.Args.Length && double.TryParse(e.Args[at + 2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var width))
                w.Width = width;
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

        // A verified update is waiting (downloaded earlier, user agreed to automatic updates): install it now,
        // before anything opens. The new version takes over from here and starts the updated app.
        if (!e.Args.Contains("--updated") && !e.Args.Contains("--update-failed") && Updater.Staged() is not null)
        {
            var settings = new Core.Settings.AppPaths().LoadSettings();
            if (settings.Updates.Enabled == true || restarting)
            {
                ReleaseSingleInstance();
                if (Updater.LaunchApply()) { Shutdown(); return; }
                _single = new Mutex(false, MutexName);
                try { _single.WaitOne(TimeSpan.FromSeconds(5)); } catch (AbandonedMutexException) { }
            }
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
            var outcome = e.Args.FirstOrDefault(a => a is "--updated" or "--update-failed");
            _host = new AppHost { LicensedTo = license.Name, JustActivated = justActivated, UpdateOutcome = outcome };
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
                new HealthItem(true, "Learned signatures", "2 on this PC · 1 not shared yet — Advanced settings → Share on Discord / GitHub"),
                new HealthItem(true, "Search area", "clear when you click START (this panel minimizes itself)"),
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
        var pc = Services.MachineIdentity.Code;
        if (stored is not null && stored.IsValidOn(pc)) return (stored, false);

        // A stored activation that does not fit this PC: copied from another computer, Windows was
        // reinstalled, or it is an old name-only key (before v0.1.3).
        var window = new Windows.ActivationWindow(stored?.Name, pc,
            stored is null ? null : "The saved activation is not valid on this PC. Send your name and the PC code below to Vorxen to get a key for this PC.");
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
