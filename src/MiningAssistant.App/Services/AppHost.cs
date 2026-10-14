using System.Diagnostics;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Threading;
using MiningAssistant.App.Windows;
using MiningAssistant.Core.Matching;
using MiningAssistant.Core.Materials;
using MiningAssistant.Core.Presentation;
using MiningAssistant.Core.Roi;
using MiningAssistant.Core.Settings;
using MiningAssistant.Core.Temporal;
using MiningAssistant.Vision.Capture;
using MiningAssistant.Vision.Ocr;
using MiningAssistant.Vision.Pipeline;
using WinForms = System.Windows.Forms;

namespace MiningAssistant.App.Services;

/// <summary>Wires scanner, overlay, windows, hotkeys and tray together. All UI work runs on the dispatcher.</summary>
public sealed partial class AppHost : IDisposable
{
    public AppPaths Paths { get; } = new();

    /// <summary>Name the license key was made for (shown as "Welcome …" in the control panel).</summary>
    public string LicensedTo { get; init; } = "";

    /// <summary>Just activated with a key: show the "Before you start" conditions.</summary>
    public bool JustActivated { get; init; }
    public AppSettings Settings { get; private set; } = new();

    private RoiProfile _profile = new();
    private MaterialDatabase _db = new();
    private IOcrEngine? _ocr;
    private string _ocrInfo = "";
    private LiveScanner? _scanner;
    private OverlayWindow _overlay = null!;
    private SettingsWindow _settingsWindow = null!;
    private DebugWindow _debugWindow = null!;
    private HotkeyManager _hotkeys = null!;
    private WinForms.NotifyIcon _tray = null!;
    private readonly DispatcherTimer _topmostTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    private ScanFrame? _lastFrame;
    private int _renderPending;
    private string? _flash;
    private DateTime _flashUntil;
    private bool _overlayVisible = true;
    private DateTime _startedAt = DateTime.Now;
    private int _reloads;

    private Dispatcher Ui => Application.Current.Dispatcher;

    public void Start()
    {
        Settings = Paths.LoadSettings();
        _profile = Paths.LoadProfile(Settings.ActiveProfile);
        _overlayVisible = Settings.Overlay.Visible;
        InitLearning();
        LoadDatabase();
        _ocr = WindowsOcrEngine.TryCreate(Settings.Ocr.Language, out _ocrInfo);
        Log($"Start. {_ocrInfo}. DB {_db.SourcePath} ({_db.Materials.Count} materials).");

        _overlay = new OverlayWindow();
        _overlay.Moved += OnOverlayMoved;
        _overlay.Show();
        _overlay.ShowIdle(Settings.AutoStart ? "⛏ starting" : "⏸ click START in the control panel");
        ApplyOverlayPlacement();
        if (!_overlayVisible) _overlay.Hide();

        _debugWindow = new DebugWindow();
        _debugWindow.ScanRequested += QuickScan;
        _debugWindow.SnapshotRequested += TakeDebugSnapshot;
        _debugWindow.AnalyzeRequested += AnalyzeScreenshot;
        _debugWindow.SetContext(Settings.Matcher.Context);

        _settingsWindow = new SettingsWindow(this);
        _hotkeys = new HotkeyManager();
        RegisterHotkeys();
        CreateTray();

        _topmostTimer.Interval = TimeSpan.FromMilliseconds(700);
        _topmostTimer.Tick += (_, _) =>
        {
            if (_overlayVisible) _overlay.KeepOnTop();
            if (_settingsWindow.IsVisible) UpdateStatus(); // live status / diagnostics
        };
        _topmostTimer.Start();

        if (_ocr is not null)
        {
            _scanner = new LiveScanner(BuildConfig()) { Recorder = new MissedReadRecorder(Paths.MissedReadsRoot) };
            _scanner.FrameProcessed += OnFrame;
            _scanner.Paused = !Settings.AutoStart; // otherwise wait for Start (with countdown)
            _scanner.Start();
        }
        else
        {
            MessageBox.Show(_ocrInfo + "\n\nSee README → 'Configure OCR'.", "Mining Assistant by Vorxen", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // Always open the control panel (unless turned off): starting silently in the tray looks like
        // "nothing happened", especially on a new PC.
        if (Settings.ShowPanelOnStartup || !_profile.HasRegion(RoiNames.RS) || _ocr is null) _settingsWindow.Show();
        UpdateStatus();

        // Conditions for the app to work: right after activation, and once for existing users.
        if (JustActivated || !Settings.RequirementsAcknowledged) ShowRequirements();
    }

    private void LoadDatabase()
    {
        try
        {
            _db = MaterialDatabaseLoader.LoadForVersion(Paths.DataRoot, Settings.GameVersion);
            _learned.MergeInto(_db); // signatures the user named in the pop-up
            var problems = _db.Validate();
            if (problems.Count > 0)
                MessageBox.Show("materials.json problems:\n\n" + string.Join("\n", problems), "Mining Assistant by Vorxen", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            Log("DB load failed: " + ex);
            MessageBox.Show($"Could not load materials.json for '{Settings.GameVersion}':\n{ex.Message}", "Mining Assistant by Vorxen", MessageBoxButton.OK, MessageBoxImage.Error);
            _db = new MaterialDatabase();
        }
    }

    private ScannerConfig BuildConfig() => new(Settings, _profile, _db, _ocr!);

    // ───────────────────────── frames → UI ─────────────────────────

    private void OnFrame(ScanFrame frame)
    {
        _lastFrame = frame;
        // Coalesce: never queue more than one render on the UI thread.
        if (Interlocked.Exchange(ref _renderPending, 1) == 1) return;
        Ui.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Interlocked.Exchange(ref _renderPending, 0);
            Render(_lastFrame!);
        });
    }

    private void Render(ScanFrame frame)
    {
        var view = OverlayPresenter.Build(frame.State, Settings);

        if (_overlayVisible && !_overlay.MoveMode)
            _overlay.ShowView(view, StatusLine(frame));

        if (frame.State.IsNew)
        {
            OnNewDetection(frame.State, view);
            MaybeAskUnknown(frame);
        }

        if (_debugWindow.IsVisible)
        {
            using var roi = _scanner?.CloneLastRoi();
            _debugWindow.ShowFrame(frame, view, roi is null ? null : Imaging.FromBitmap(roi), _ocrInfo, _scanner?.Paused == true);
        }
    }

    private string? StatusLine(ScanFrame? frame)
    {
        if (_flash is not null && DateTime.Now < _flashUntil) return _flash;
        if (_scanner?.Paused == true) return "⏸ OCR paused (Ctrl+Shift+P)";
        if (frame?.Error is { } err) return "⚠ " + err;
        if (_ocr is null) return "⚠ Windows OCR unavailable";
        return Settings.DebugMode ? $"debug · {frame?.TotalTime.TotalMilliseconds:F0} ms" : null;
    }

    private void OnNewDetection(SessionState state, OverlayView view)
    {
        // User-chosen signature numbers: own two-tone beep, independent of the ore "Sound alerts" switch.
        if (state.StableRS is int rs && Settings.AlertRsValues.Count > 0 &&
            RsAlert.Match(rs, Settings.AlertRsValues, Settings.AlertRsMultiples, Settings.Matcher.MaxAbsoluteError,
                          Settings.Matcher.MaxDepositCount) is int hit)
        {
            PlayRsAlertTone();
            Flash(rs == hit ? $"🔔 RS {rs:N0}" : $"🔔 RS {rs:N0}  (×{Math.Round(rs / (double)hit)} of {hit:N0})", 4);
            Log($"RS alert: {rs} matched {hit}");
            return;
        }

        if (!Settings.SoundAlerts || state.Match is not { } m) return;
        var notify = view.IsTarget || (m.IsMatch && m.Best!.Material.Notify && view.Kind == OverlayKind.Match);
        if (notify) SystemSounds.Asterisk.Play();
    }

    /// <summary>Rising two-tone beep (plays through the default audio device; does not block the UI).</summary>
    public static void PlayRsAlertTone() => Task.Run(() =>
    {
        try
        {
            Console.Beep(880, 140);
            Console.Beep(1320, 220);
        }
        catch { SystemSounds.Exclamation.Play(); }
    });

    /// <summary>What each alert number is, according to the database (for the control panel hint).</summary>
    public string DescribeAlertValues(IEnumerable<int> values)
    {
        var matcher = new SignatureMatcher(_db, Settings.Matcher);
        return string.Join("   ", values.Select(v =>
        {
            var m = matcher.Match(v);
            return $"{v:N0} = " + (m.Best is { } b && m.IsMatch ? b.Label : m.Status == MatchStatus.Ambiguous ? "ambiguous" : "unknown signature");
        }));
    }

    private void Flash(string text, double seconds = 3)
    {
        _flash = text;
        _flashUntil = DateTime.Now.AddSeconds(seconds);
        if (_overlayVisible && (_lastFrame is null || _lastFrame.State.Match is null)) _overlay.ShowIdle(text);
        _debugWindow.ShowMessage(text);
    }

    private void UpdateStatus() => _settingsWindow.Refresh(BuildPanelStatus());

    // ───────────────────────── actions ─────────────────────────

    public void ApplySettings()
    {
        Paths.SaveSettings(Settings);
        LoadDatabase();
        _scanner?.Reconfigure(BuildConfig());
        _debugWindow.SetContext(Settings.Matcher.Context);
        RegisterHotkeys();
        ApplyOverlayPlacement();
        UpdateStatus();
        Flash("✓ settings saved");
    }

    public void PreviewOverlay() => ApplyOverlayPlacement();

    private void ApplyOverlayPlacement()
    {
        var (x, y, center) = OverlayPosition();
        _overlay.ApplySettings(Settings.Overlay, x, y, center);
    }

    /// <summary>
    /// Physical anchor of the overlay: next to the RS indicator (top-left), or the manual position
    /// (top-center when CenterHorizontally, else top-left).
    /// </summary>
    private (int x, int y, bool center) OverlayPosition()
    {
        var o = Settings.Overlay;
        var mon = Monitors.Get(Settings.MonitorIndex);
        if (o.Placement == OverlayPlacement.NextToSignal && _profile.Get(RoiNames.RS) is { } rs)
        {
            var r = rs.ToPixels(mon.Width, mon.Height);
            var k = mon.Height / 1440.0; // offsets are specified at 1440p
            return (mon.Left + r.X + r.Width / 2 + (int)(o.AnchorOffsetX * k),
                    mon.Top + r.Y + r.Height / 2 + (int)(o.AnchorOffsetY * k), false);
        }
        return (mon.Left + (int)(Math.Clamp(o.XPercent, 0, 1) * mon.Width),
                mon.Top + (int)(Math.Clamp(o.YPercent, 0, 0.98) * mon.Height), o.CenterHorizontally);
    }

    public void ToggleOverlay()
    {
        _overlayVisible = !_overlayVisible;
        Settings.Overlay.Visible = _overlayVisible;
        if (_overlayVisible)
        {
            _overlay.Show();
            ApplyOverlayPlacement();
        }
        else _overlay.Hide();
    }

    public void TogglePause()
    {
        if (_scanner is null) return;
        _scanner.Paused = !_scanner.Paused;
        if (_overlayVisible && _scanner.Paused) _overlay.ShowIdle("⏸ OCR paused");
        UpdateStatus();
    }

    /// <summary>Toggles drag mode. Free the mouse from the game (e.g. Alt+Tab) to drag the card.</summary>
    public void ToggleMoveOverlay()
    {
        if (!_overlayVisible) ToggleOverlay();
        _overlay.SetMoveMode(!_overlay.MoveMode); // never takes focus from the game
    }

    /// <summary>Dragged position becomes the new fixed (manual) placement, as a fraction of the monitor.</summary>
    private void OnOverlayMoved(int x, int y, int width)
    {
        var mon = Monitors.Get(Settings.MonitorIndex);
        var o = Settings.Overlay;
        o.Placement = OverlayPlacement.Manual;
        var anchorX = o.CenterHorizontally ? x + width / 2 : x;
        o.XPercent = Math.Clamp((anchorX - mon.Left) / (double)mon.Width, 0, 1);
        o.YPercent = Math.Clamp((y - mon.Top) / (double)mon.Height, 0, 0.98);
        Paths.SaveSettings(Settings);
        ApplyOverlayPlacement();
        _settingsWindow.Load(Settings);
        Flash("✓ overlay position saved");
        Log($"Overlay moved to {o.XPercent:P1}, {o.YPercent:P1}");
    }

    /// <summary>Auto → Space → Surface → Auto. Context rules out signals that cannot appear there.</summary>
    public void CycleContext()
    {
        Settings.Matcher.Context = Settings.Matcher.Context switch
        {
            ScanContext.Auto => ScanContext.Space,
            ScanContext.Space => ScanContext.Surface,
            _ => ScanContext.Auto,
        };
        Paths.SaveSettings(Settings);
        _scanner?.Reconfigure(BuildConfig());
        _debugWindow.SetContext(Settings.Matcher.Context);
        Flash(Settings.Matcher.Context == ScanContext.Auto
            ? "◎ context: AUTO (detected from signals)"
            : $"◎ context: {Settings.Matcher.Context.ToString().ToUpperInvariant()} (manual)");
        UpdateStatus();
    }

    public void QuickScan()
    {
        if (_scanner is null) return;
        _scanner.Paused = false;
        _scanner.ForceScan();
        Flash("⛏ scanning…", 1.5);
        UpdateStatus();
    }

    public void StartCalibration()
    {
        var monitor = Monitors.Get(Settings.MonitorIndex);
        var wasPaused = _scanner?.Paused ?? true;
        if (_scanner is not null) _scanner.Paused = true;
        try
        {
            using var shot = new GdiScreenCapture().Capture(new PixelRect(monitor.Left, monitor.Top, monitor.Width, monitor.Height));
            var working = new RoiProfile
            {
                Name = _profile.Name,
                Regions = new Dictionary<string, NormalizedRect>(_profile.Regions, StringComparer.OrdinalIgnoreCase),
            };
            var win = new CalibrationWindow(monitor, shot, working);
            if (win.ShowDialog() == true)
            {
                _profile = win.Result;
                Paths.SaveProfile(_profile);
                _scanner?.Reconfigure(BuildConfig());
                ApplyOverlayPlacement(); // the overlay follows the RS region
                Flash(_profile.HasRegion(RoiNames.RS) ? "✓ calibration saved" : "⚠ RS region not set");
                Log($"Calibration saved: {string.Join(", ", _profile.Regions.Select(r => $"{r.Key}={r.Value}"))}");
            }
        }
        finally
        {
            if (_scanner is not null) _scanner.Paused = wasPaused;
            UpdateStatus();
        }
    }

    private int _snapshotsBusy;

    /// <summary>
    /// "]" hotkey / panel button: saves a full debug snapshot of this exact moment (whole game monitor,
    /// search area, plate, every OCR input and a report) into debug_snapshots/ for later analysis.
    /// The screen is grabbed immediately; the analysis runs in the background.
    /// </summary>
    public async void TakeDebugSnapshot()
    {
        if (_ocr is null) { Flash("⚠ snapshot needs Windows OCR"); return; }
        if (Interlocked.Exchange(ref _snapshotsBusy, 1) == 1) return; // one at a time

        try
        {
            var mon = Monitors.Get(Settings.MonitorIndex);
            var region = _profile.Get(RoiNames.RS) ?? new NormalizedRect(0.30, 0.12, 0.40, 0.46);
            var area = Settings.Ocr.SearchArea(region).ToPixels(mon.Width, mon.Height);
            using var screen = new GdiScreenCapture().Capture(new PixelRect(mon.Left, mon.Top, mon.Width, mon.Height));
            SystemSounds.Asterisk.Play();
            Flash("📸 saving snapshot…", 2);

            var view = _lastFrame is { } f ? OverlayPresenter.Build(f.State, Settings) : OverlayView.Idle;
            var overlay = view.Kind == OverlayKind.Idle ? null
                : $"{view.Headline} · {view.RsLine} · {view.RocksLine}" + (view.Alternatives.Count > 0 ? " · " + string.Join(" | ", view.Alternatives) : "");
            var ctx = new SnapshotContext(mon, area, Settings, Settings.GameVersion, BuildPanelStatus().Live, overlay, null);
            var matcher = new SignatureMatcher(_db, Settings.Matcher);
            var pipeline = new ScanPipeline(_ocr, matcher, Settings.Ocr);
            using var copy = new System.Drawing.Bitmap(screen);

            var dir = await Task.Run(() => new DebugSnapshotWriter(Paths.DebugSnapshotsRoot).SaveAsync(copy, ctx, pipeline, matcher));
            Flash($"📸 snapshot saved: {Path.GetFileName(dir)}", 3);
            Log($"Debug snapshot saved: {dir}");
        }
        catch (Exception ex)
        {
            Log("Debug snapshot failed: " + ex);
            Flash("⚠ snapshot failed (see config/app.log)");
        }
        finally { Interlocked.Exchange(ref _snapshotsBusy, 0); }
    }

    public void OpenSnapshotsFolder() => OpenFolder(Paths.DebugSnapshotsRoot);

    public async void AnalyzeScreenshot()
    {
        if (_ocr is null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Analyze screenshot",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp",
            InitialDirectory = Directory.Exists(Path.Combine(Paths.Root, "test_images")) ? Path.Combine(Paths.Root, "test_images") : null,
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            using var bmp = new System.Drawing.Bitmap(dlg.FileName);
            var pipeline = new ScanPipeline(_ocr, new SignatureMatcher(_db, Settings.Matcher), Settings.Ocr);
            var analysis = await pipeline.AnalyzeScreenshotAsync(bmp, _profile, keepDebugImage: true);
            var rs = analysis.Reading.RS;
            var state = new SessionState(rs, analysis.Match, 1, 1, false, false);
            var frame = new ScanFrame(DateTime.UtcNow, analysis, state, new int?[] { rs }, default, TimeSpan.Zero, analysis.OcrTime, 0,
                _profile.HasRegion(RoiNames.RS) ? null : "No RS region — whole image was OCR'd");
            ShowDebug();
            _debugWindow.ShowFrame(frame, OverlayPresenter.Build(state, Settings), null, _ocrInfo, _scanner?.Paused == true);
            _debugWindow.ShowMessage($"Analyzed {Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Analyze screenshot", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public void ShowDebug()
    {
        _debugWindow.Show();
        _debugWindow.Activate();
    }

    public void ShowSettings()
    {
        _settingsWindow.Load(Settings);
        UpdateStatus();
        _settingsWindow.Show();
        _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    public void OpenDataFolder() =>
        Process.Start(new ProcessStartInfo { FileName = Paths.DataRoot, UseShellExecute = true });

    public void Exit()
    {
        Paths.SaveSettings(Settings);
        Dispose();
        Application.Current.Shutdown();
    }

    // ───────────────────────── hotkeys / tray ─────────────────────────

    private void RegisterHotkeys()
    {
        _hotkeys.UnregisterAll();
        _hotkeys.Enabled = Settings.HotkeysEnabled;
        var h = Settings.Hotkeys;
        _hotkeys.Register(h.ToggleOverlay, ToggleOverlay);
        _hotkeys.Register(h.QuickScan, QuickScan);
        _hotkeys.Register(h.Calibration, StartCalibration);
        _hotkeys.Register(h.PauseOcr, TogglePause);
        _hotkeys.Register(h.DebugSnapshot, TakeDebugSnapshot);
        _hotkeys.Register(h.DebugWindow, ShowDebug);
        _hotkeys.Register(h.CycleContext, CycleContext);
        _hotkeys.Register(h.MoveOverlay, ToggleMoveOverlay);
        if (_hotkeys.Failures.Count > 0)
            Log("Hotkey problems: " + string.Join("; ", _hotkeys.Failures));
    }

    /// <summary>The app icon at tray size (falls back to the generic Windows icon if it cannot be read).</summary>
    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (res is not null)
            {
                using var stream = res.Stream;
                var size = WinForms.SystemInformation.SmallIconSize;
                return new System.Drawing.Icon(stream, size);
            }
        }
        catch { /* fall through */ }
        return System.Drawing.SystemIcons.Application;
    }

    private void CreateTray()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open control panel", null, (_, _) => ShowSettings());
        menu.Items.Add("Reload", null, (_, _) => Reload());
        menu.Items.Add("Calibrate search area", null, (_, _) => StartCalibration());
        menu.Items.Add("Show / hide overlay", null, (_, _) => ToggleOverlay());
        menu.Items.Add("Move overlay (drag, then lock)", null, (_, _) => ToggleMoveOverlay());
        menu.Items.Add("Scanning on / off", null, (_, _) => TogglePause());
        menu.Items.Add("Cycle scan context (Auto/Space/Surface)", null, (_, _) => CycleContext());
        menu.Items.Add("Debug snapshot (save this moment)", null, (_, _) => TakeDebugSnapshot());
        menu.Items.Add("Debug window", null, (_, _) => ShowDebug());
        menu.Items.Add("Analyze screenshot…", null, (_, _) => AnalyzeScreenshot());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Exit());

        _tray = new WinForms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "Star Citizen Mining Assistant by Vorxen",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowSettings();
    }

    public void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(Paths.ConfigRoot);
            File.AppendAllText(Path.Combine(Paths.ConfigRoot, "app.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch { /* logging must never crash the overlay */ }
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _topmostTimer.Stop();
        _scanner?.Dispose();
        _hotkeys?.Dispose();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
    }
}
