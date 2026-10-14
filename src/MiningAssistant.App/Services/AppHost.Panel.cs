using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using MiningAssistant.Core.Matching;
using MiningAssistant.Core.Presentation;
using MiningAssistant.Core.Roi;
using MiningAssistant.Vision.Capture;
using MiningAssistant.Vision.Ocr;
using MiningAssistant.Vision.Pipeline;

namespace MiningAssistant.App.Services;

public enum PanelState { Running, Paused, Problem }

/// <summary>One line of the control panel's health checklist.</summary>
public sealed record HealthItem(bool Ok, string Title, string Detail);

public sealed record PanelStatus(
    PanelState State,
    string Headline,
    IReadOnlyList<HealthItem> Health,
    string Live,
    BitmapSource? Preview,
    bool Scanning,
    /// <summary>Seconds left in the start countdown (0 = none).</summary>
    int Countdown,
    bool OverlayVisible);

/// <summary>Control-panel side of the host: live status, feature switches, reload / restart.</summary>
public sealed partial class AppHost
{
    /// <summary>Raised when the user asks for a full process restart.</summary>
    public event Action? RestartRequested;

    public bool IsScanning => _scanner is { Paused: false };
    public bool IsOverlayVisible => _overlayVisible;

    // ───────────────────────── feature switches ─────────────────────────

    public void SetScanning(bool on)
    {
        if (_scanner is null) return;
        _scanner.Paused = !on;
        if (on) _scanner.ForceScan();
        else if (_overlayVisible) _overlay.ShowIdle("⏸ scanning off");
        UpdateStatus();
    }

    // ───────────────────────── start with countdown ─────────────────────────

    private readonly System.Windows.Threading.DispatcherTimer _countdownTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private Windows.CountdownWindow? _countdownWindow;
    private int _countdown;

    /// <summary>Seconds left before scanning starts, or 0 when no countdown is running.</summary>
    public int Countdown => _countdown;

    /// <summary>
    /// The Start / Stop button. Starting runs a visible 5…1 countdown first, so the user has time to
    /// switch to the game window before the overlay begins reading the screen. Clicking again during
    /// the countdown cancels it; clicking while scanning stops.
    /// </summary>
    public void StartStop()
    {
        if (_scanner is null) return;
        if (_countdown > 0) { CancelCountdown(); UpdateStatus(); return; }
        if (IsScanning) { SetScanning(false); return; }

        var delay = Math.Clamp(Settings.StartDelaySeconds, 0, 30);
        if (delay == 0) { BeginScanning(); return; }

        _countdown = delay;
        _countdownTimer.Tick -= CountdownTick;
        _countdownTimer.Tick += CountdownTick;
        _countdownTimer.Start();
        ShowCountdown();
        Log($"Start requested: {delay} s countdown.");
    }

    private void CountdownTick(object? sender, EventArgs e)
    {
        if (--_countdown > 0) { ShowCountdown(); return; }
        CancelCountdown();
        BeginScanning();
    }

    private void ShowCountdown()
    {
        _countdownWindow ??= new Windows.CountdownWindow(Settings.Overlay.HideFromCapture);
        _countdownWindow.ShowNumber(_countdown, Monitors.Get(Settings.MonitorIndex));
        UpdateStatus();
    }

    private void CancelCountdown()
    {
        _countdownTimer.Stop();
        _countdown = 0;
        _countdownWindow?.Hide();
    }

    private void BeginScanning()
    {
        if (!_overlayVisible) ToggleOverlay();
        if (Settings.MinimizePanelOnStart && _settingsWindow.IsVisible) _settingsWindow.WindowState = System.Windows.WindowState.Minimized;
        SetScanning(true);
        Flash("▶ scanning started", 2.5);
        Log("Scanning started.");
    }

    public void SetOverlayVisible(bool on)
    {
        if (on != _overlayVisible) ToggleOverlay();
        Paths.SaveSettings(Settings);
        UpdateStatus();
    }

    // ───────────────────────── reload / restart ─────────────────────────

    /// <summary>
    /// Full refresh without closing the app: re-reads settings.json, the ROI profile and materials.json
    /// from disk, recreates the OCR engine and the scanner, re-registers hotkeys and re-places the overlay.
    /// </summary>
    public void Reload()
    {
        _reloads++;
        CancelCountdown();
        var wasScanning = IsScanning;
        Log("Reload requested.");
        try
        {
            if (_scanner is not null)
            {
                _scanner.FrameProcessed -= OnFrame;
                _scanner.Dispose();
                _scanner = null;
            }
            _lastFrame = null;

            Settings = Paths.LoadSettings();
            _profile = Paths.LoadProfile(Settings.ActiveProfile);
            _learned.Reload();
            LoadDatabase();
            _ocr = WindowsOcrEngine.TryCreate(Settings.Ocr.Language, out _ocrInfo);

            if (_ocr is not null)
            {
                _scanner = new LiveScanner(BuildConfig()) { Recorder = new MissedReadRecorder(Paths.MissedReadsRoot) };
                _scanner.FrameProcessed += OnFrame;
                _scanner.Paused = !wasScanning;
                _scanner.Start();
            }

            _overlayVisible = Settings.Overlay.Visible;
            if (_overlayVisible) _overlay.Show(); else _overlay.Hide();
            _overlay.ShowIdle("⟳ reloaded");
            ApplyOverlayPlacement();
            _debugWindow.SetContext(Settings.Matcher.Context);
            RegisterHotkeys();
            _settingsWindow.Load(Settings);
            Flash("⟳ reloaded");
            Log($"Reloaded. {_ocrInfo}. DB {_db.SourcePath} ({_db.Materials.Count} entries).");
        }
        catch (Exception ex)
        {
            Log("Reload failed: " + ex);
            System.Windows.MessageBox.Show("Reload failed:\n\n" + ex.Message, "Mining Assistant by Vorxen",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
        UpdateStatus();
    }

    /// <summary>Closes and starts the app again (clean process).</summary>
    public void RestartApp()
    {
        Paths.SaveSettings(Settings);
        Log("Restart requested.");
        RestartRequested?.Invoke();
    }

    public void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    public void OpenConfigFolder() => OpenFolder(Paths.ConfigRoot);

    /// <summary>Runs the headless self-test and opens its report.</summary>
    public async void RunSelfTest()
    {
        await Task.Run(SelfTest.Run);
        var report = Path.Combine(Paths.ConfigRoot, "selftest.txt");
        if (File.Exists(report)) Process.Start(new ProcessStartInfo { FileName = report, UseShellExecute = true });
    }
    public void OpenDiagnosticsFolder() => OpenFolder(Path.Combine(Paths.Root, "diagnostics"));

    // ───────────────────────── live status ─────────────────────────

    private PanelStatus BuildPanelStatus()
    {
        var frame = _lastFrame;
        var mon = Monitors.Get(Settings.MonitorIndex);
        var region = _profile.Get(RoiNames.RS);
        var paused = _scanner?.Paused == true;
        var age = frame is null ? (TimeSpan?)null : DateTime.UtcNow - frame.At;
        var capturing = !paused && age is { TotalSeconds: < 3 } && frame!.Error is null;
        var plate = frame?.Analysis?.Plate;

        var health = new List<HealthItem>
        {
            new(_ocr is not null, "Text recognition (Windows OCR)",
                _ocr is not null ? _ocrInfo : _ocrInfo + " — install the English OCR pack (see README)."),
            new(_db.Ores.Any(), "Signature database",
                _db.Ores.Any()
                    ? $"{Settings.GameVersion}: {_db.Ores.Count()} ores + {_db.Enabled.Count() - _db.Ores.Count()} other signals"
                    : $"materials.json not loaded from {Paths.DataRoot}"),
            new(region is not null, "Search area",
                region is null
                    ? "not set — click “Calibrate search area”"
                    : $"{mon.Width}×{mon.Height} monitor #{mon.Index}, area {Describe(region.ToPixels(mon.Width, mon.Height))}" +
                      (Settings.Ocr.AutoLocate ? " · auto-locating the amber RS plate" : " · fixed area (auto-locate off)")),
            new(mon.Height >= 1080, "Resolution",
                mon.Height >= 1080
                    ? $"{mon.Width}×{mon.Height} (1080p or higher is supported)"
                    : $"{mon.Width}×{mon.Height} is below the 1080p minimum — the RS digits are too small to read reliably"),
            new(capturing || paused, "Screen capture",
                paused ? "stopped — click START"
                : capturing ? $"running · {frame!.TotalTime.TotalMilliseconds:F0} ms per frame, every {Settings.OcrIntervalMs} ms"
                : frame?.Error ?? (_scanner is null ? "scanner not running" : "no frames yet")),
            new(_hotkeys.RawInputActive && Settings.HotkeysEnabled, "Hotkeys",
                !Settings.HotkeysEnabled ? "turned off"
                : !_hotkeys.RawInputActive ? "could not register keyboard listener"
                : $"listening · keys received: {_hotkeys.KeysReceived}" +
                  (_hotkeys.LastHotkeyAt is { } hk ? $" · last hotkey {hk:HH:mm:ss}" : "") +
                  (_hotkeys.KeysReceived == 0 ? " (press any key; if it stays 0 inside the game, the game is running as administrator)" : "")),
        };

        // Our own windows sit in the middle of the screen by default — exactly where the RS plate appears.
        if (region is not null)
        {
            var area = mon.ToScreen(Settings.Ocr.SearchArea(region).ToPixels(mon.Width, mon.Height));
            var covering = new System.Windows.Window?[] { _settingsWindow, _debugWindow, _unknownWindow }
                .OfType<System.Windows.Window>()
                .Where(w => w.IsVisible && w.WindowState != System.Windows.WindowState.Minimized)
                .Any(w =>
                {
                    var (x, y, bw, bh) = Interop.Win32.Bounds(w);
                    return x < area.X + area.Width && area.X < x + bw && y < area.Y + area.Height && area.Y < y + bh;
                });
            health.Add(new(!covering, "Search area is clear",
                covering
                    ? "this window is on top of the search area — while it is in front, the app sees this panel instead of the game (it minimizes itself when you click START)"
                    : "no app window is covering it"));
        }
        health.Add(ContributionHealth());

        // What the scanner sees right now.
        var live = new System.Text.StringBuilder();
        if (paused) live.AppendLine(_countdown > 0 ? $"Starting in {_countdown}…" : "Scanning is OFF — click START.");
        else if (frame?.Analysis is null) live.AppendLine(frame?.Error ?? "Waiting for the first frame…");
        else
        {
            var a = frame.Analysis;
            live.AppendLine(plate is not null
                ? $"RS indicator: FOUND  (icon {plate.Icon.Width}×{plate.Icon.Height} px at {plate.Icon.X},{plate.Icon.Y})"
                : Settings.Ocr.AutoLocate
                    ? "RS indicator: not on screen  (normal when nothing is scanned)"
                    : a.NoText ? "RS area: empty" : "RS area: has text");
            live.AppendLine($"Read now:     {a.Reading.RS?.ToString("N0") ?? "—"}" +
                            (a.Attempts.Count > 0 ? $"   (OCR: “{a.Attempts[^1].Text.Replace("\n", " ")}”)" : ""));
            var s = frame.State;
            live.AppendLine($"Stable value: {s.StableRS?.ToString("N0") ?? "—"}" + (s.IsHeld ? "  (last seen)" : ""));
            if (s.Match is { } m)
                live.AppendLine("Result:       " + (m.Best is { } b
                    ? $"{b.Label}  [{m.Status}]"
                    : m.Status == MatchStatus.Unknown ? "UNKNOWN signature" : m.Status.ToString()));
            var ctx = OverlayPresenter.ContextLabel(s);
            live.AppendLine($"Location:     {ctx ?? "not detected yet"}");
        }
        live.Append($"Uptime {DateTime.Now - _startedAt:hh\\:mm\\:ss} · reloads {_reloads}");

        var state = _ocr is null || region is null || _scanner is null ? PanelState.Problem
            : paused ? PanelState.Paused
            : capturing ? PanelState.Running
            : age is null && DateTime.Now - _startedAt < TimeSpan.FromSeconds(5) ? PanelState.Running
            : PanelState.Problem;
        var headline = state switch
        {
            PanelState.Running => plate is not null ? "RUNNING — reading the RS indicator" : "RUNNING — waiting for an RS indicator",
            PanelState.Paused => _countdown > 0
                ? $"STARTING IN {_countdown}…  switch to the Star Citizen window now"
                : "STOPPED — click START (a 5 s countdown gives you time to switch to the game)",
            _ => "NOT WORKING — " + (health.FirstOrDefault(h => !h.Ok)?.Title ?? "see below"),
        };

        return new PanelStatus(state, headline, health, live.ToString(), BuildPreview(plate), !paused && _scanner is not null, _countdown, _overlayVisible);
    }

    private static string Describe(PixelRect r) => $"{r.Width}×{r.Height} px at ({r.X},{r.Y})";

    /// <summary>Thumbnail of what the scanner captured, with the located plate outlined.</summary>
    private BitmapSource? BuildPreview(Vision.Imaging.Plate? plate)
    {
        using var roi = _scanner?.CloneLastRoi();
        if (roi is null) return null;

        const int maxW = 560;
        var k = Math.Min(1.0, maxW / (double)roi.Width);
        using var thumb = new System.Drawing.Bitmap(Math.Max(1, (int)(roi.Width * k)), Math.Max(1, (int)(roi.Height * k)));
        using (var g = System.Drawing.Graphics.FromImage(thumb))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            g.DrawImage(roi, 0, 0, thumb.Width, thumb.Height);
            if (plate is not null)
            {
                var b = plate.Bounds;
                using var pen = new System.Drawing.Pen(System.Drawing.Color.Lime, 2);
                g.DrawRectangle(pen, (float)(b.X * k) - 4, (float)(b.Y * k) - 4, (float)(b.Width * k) + 8, (float)(b.Height * k) + 8);
            }
        }
        return Imaging.FromBitmap(thumb);
    }
}
