using System.Diagnostics;
using System.Drawing;
using MiningAssistant.Core.Matching;
using MiningAssistant.Core.Materials;
using MiningAssistant.Core.Roi;
using MiningAssistant.Core.Settings;
using MiningAssistant.Core.Temporal;
using MiningAssistant.Vision.Capture;
using MiningAssistant.Vision.Imaging;
using MiningAssistant.Vision.Ocr;

namespace MiningAssistant.Vision.Pipeline;

public sealed record ScanFrame(
    DateTime At,
    FrameAnalysis? Analysis,
    SessionState State,
    IReadOnlyList<int?> VoteBuffer,
    PixelRect ScreenRect,
    TimeSpan CaptureTime,
    TimeSpan TotalTime,
    double OcrPerSecond,
    string? Error);

/// <summary>Everything the loop needs, swapped atomically when settings change.</summary>
public sealed record ScannerConfig(AppSettings Settings, RoiProfile Profile, MaterialDatabase Database, IOcrEngine Ocr);

/// <summary>
/// Background capture/OCR loop. Captures only the calibrated RS region at the configured interval,
/// skips OCR when the region did not change, and feeds the temporal voter.
/// Runs below normal priority to stay out of the game's way.
/// </summary>
public sealed class LiveScanner : IDisposable
{
    private readonly IScreenCapture _capture;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly object _roiLock = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private ScannerConfig _config;
    private ScanPipeline _pipeline;
    private ScanSession _session;
    private ulong _lastHash;
    private FrameAnalysis? _lastAnalysis;
    private Bitmap? _lastRoi;
    private readonly Queue<DateTime> _ocrTimes = new();

    public event Action<ScanFrame>? FrameProcessed;

    public bool Paused { get; set; }

    /// <summary>Optional local evidence of missed/unknown reads (see AppSettings.SaveMissedReads).</summary>
    public MissedReadRecorder? Recorder { get; set; }
    public ScannerConfig Config => _config;

    // Survives Reconfigure(): applying settings must not forget where the player is.
    private ContextInference _inference;

    public LiveScanner(ScannerConfig config, IScreenCapture? capture = null)
    {
        _capture = capture ?? new GdiScreenCapture();
        _inference = new ContextInference(TimeSpan.FromMinutes(config.Settings.ContextMemoryMinutes));
        (_config, _pipeline, _session) = Build(config, _inference);
    }

    private static (ScannerConfig, ScanPipeline, ScanSession) Build(ScannerConfig c, ContextInference inference)
    {
        var matcher = new SignatureMatcher(c.Database, c.Settings.Matcher);
        var pipeline = new ScanPipeline(c.Ocr, matcher, c.Settings.Ocr);
        var session = new ScanSession(matcher, c.Settings.VoteWindow, c.Settings.VoteMinAgreement,
            TimeSpan.FromSeconds(c.Settings.HoldSeconds), inference, c.Settings.VoteFastAgreement);
        return (c, pipeline, session);
    }

    public void Reconfigure(ScannerConfig config)
    {
        if (Math.Abs(_inference.Memory.TotalMinutes - config.Settings.ContextMemoryMinutes) > 0.01)
        {
            var fresh = new ContextInference(TimeSpan.FromMinutes(config.Settings.ContextMemoryMinutes));
            _inference = fresh;
        }
        var built = Build(config, _inference);
        lock (_roiLock)
        {
            (_config, _pipeline, _session) = built;
            _lastHash = 0;
            _lastAnalysis = null;
        }
        _wake.Release();
    }

    public void Start()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Factory.StartNew(() => RunAsync(_cts.Token), _cts.Token,
            TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    /// <summary>Forget previous votes and scan immediately (Quick Scan hotkey).</summary>
    public void ForceScan()
    {
        lock (_roiLock)
        {
            _session.Reset();
            _lastHash = 0;
        }
        _wake.Release();
    }

    /// <summary>Copy of the most recent captured ROI (for "Save Unknown Signature").</summary>
    public Bitmap? CloneLastRoi()
    {
        lock (_roiLock) return _lastRoi is null ? null : new Bitmap(_lastRoi);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
        Thread.CurrentThread.Name = "MiningAssistant.Scanner";

        while (!ct.IsCancellationRequested)
        {
            var sw = Stopwatch.StartNew();
            if (!Paused)
            {
                ScanFrame frame;
                try { frame = await ProcessOnceAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    frame = new ScanFrame(DateTime.UtcNow, null, new SessionState(null, null, 0, 0, false, false),
                        Array.Empty<int?>(), default, TimeSpan.Zero, sw.Elapsed, 0, ex.Message);
                }
                FrameProcessed?.Invoke(frame);
            }

            var wait = Math.Max(15, _config.Settings.OcrIntervalMs - (int)sw.ElapsedMilliseconds);
            try { await _wake.WaitAsync(wait, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<ScanFrame> ProcessOnceAsync(CancellationToken ct)
    {
        var total = Stopwatch.StartNew();
        var now = DateTime.UtcNow;
        ScannerConfig config;
        ScanPipeline pipeline;
        ScanSession session;
        lock (_roiLock) { config = _config; pipeline = _pipeline; session = _session; }

        var region = config.Profile.Get(RoiNames.RS);
        if (region is null)
            return new ScanFrame(now, null, session.Observe(null, now), session.VoteBuffer, default, TimeSpan.Zero,
                total.Elapsed, 0, "No RS region calibrated — open Calibration (Ctrl+Shift+C).");

        var monitor = Monitors.Get(config.Settings.MonitorIndex);
        var rect = monitor.ToScreen(region.ToPixels(monitor.Width, monitor.Height));

        var capSw = Stopwatch.StartNew();
        var roi = _capture.Capture(rect);
        var captureTime = capSw.Elapsed;

        var hash = ImageOps.SampleHash(roi);
        FrameAnalysis analysis;
        if (config.Settings.SkipUnchangedFrames && hash == _lastHash && _lastAnalysis is not null)
        {
            analysis = _lastAnalysis with { Reused = true };
            roi.Dispose();
        }
        else
        {
            analysis = config.Settings.Ocr.AutoLocate
                ? await pipeline.AnalyzeSearchAsync(roi, monitor.Height, config.Settings.DebugMode, ct).ConfigureAwait(false)
                : await pipeline.AnalyzeRoiAsync(roi, config.Settings.DebugMode, ct).ConfigureAwait(false);
            _lastHash = hash;
            _lastAnalysis = analysis;
            if (!analysis.NoText) _ocrTimes.Enqueue(now);
            lock (_roiLock)
            {
                _lastRoi?.Dispose();
                _lastRoi = roi;
            }
            // Indicator visible but nothing readable: keep the evidence (just the plate when located).
            if (config.Settings.SaveMissedReads && !analysis.NoText && analysis.Reading.RS is null && Recorder is not null)
            {
                if (analysis.Plate is { } plate)
                {
                    var b = plate.Bounds;
                    using var crop = ImageOps.Crop(roi, new PixelRect(Math.Max(0, b.X - 16), Math.Max(0, b.Y - 16), b.Width + 32, b.Height + 32));
                    Recorder.Record("missed", crop, analysis.RawText, now);
                }
                else Recorder.Record("missed", roi, analysis.RawText, now);
            }
        }

        while (_ocrTimes.Count > 0 && now - _ocrTimes.Peek() > TimeSpan.FromSeconds(5)) _ocrTimes.Dequeue();
        var state = session.Observe(analysis.Reading.RS, now, analysis.Reading.Chosen?.Substitutions ?? 0);

        if (config.Settings.SaveMissedReads && state is { IsNew: true, Match.Status: MatchStatus.Unknown } && Recorder is not null)
        {
            using var last = CloneLastRoi();
            if (last is not null && analysis.Plate is { } p)
            {
                var b = p.Bounds;
                using var crop = ImageOps.Crop(last, new PixelRect(Math.Max(0, b.X - 16), Math.Max(0, b.Y - 16), b.Width + 32, b.Height + 32));
                Recorder.Record($"unknown_rs{state.StableRS}", crop, analysis.RawText, now);
            }
            else if (last is not null) Recorder.Record($"unknown_rs{state.StableRS}", last, analysis.RawText, now);
        }
        return new ScanFrame(now, analysis, state, session.VoteBuffer, rect, captureTime, total.Elapsed,
            _ocrTimes.Count / 5.0, null);
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        try { _loop?.Wait(1000); } catch { /* shutting down */ }
        _cts?.Dispose();
        lock (_roiLock)
        {
            _lastRoi?.Dispose();
            _lastRoi = null;
        }
    }
}
