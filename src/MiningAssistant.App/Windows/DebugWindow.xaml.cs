using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using MiningAssistant.App.Services;
using MiningAssistant.Core.Matching;
using MiningAssistant.Core.Presentation;
using MiningAssistant.Vision.Pipeline;

namespace MiningAssistant.App.Windows;

/// <summary>Shows every stage of the pipeline: ROI, OCR input, raw text, normalized numbers, candidates, timings.</summary>
public partial class DebugWindow : Window
{
    public event Action? ScanRequested;
    public event Action? SnapshotRequested;
    public event Action? AnalyzeRequested;

    private byte[]? _shownPng;
    private MiningAssistant.Core.Materials.ScanContext _context;

    public void SetContext(MiningAssistant.Core.Materials.ScanContext context) => _context = context;

    public DebugWindow()
    {
        InitializeComponent();
        Hint.Text = "Enable Debug Mode in Settings to see the OCR image.";
        Closing += (_, e) => { e.Cancel = true; Hide(); };
    }

    public void ShowFrame(ScanFrame f, OverlayView view, BitmapSource? roi, string ocrName, bool paused)
    {
        if (FreezeBox.IsChecked == true) return;
        if (roi is not null) RoiImage.Source = roi;
        if (f.Analysis?.ProcessedPng is { } png && !ReferenceEquals(png, _shownPng))
        {
            OcrImage.Source = Imaging.FromPng(png);
            _shownPng = png;
        }
        if (Hint.Text.StartsWith("Enable") && f.Analysis?.ProcessedPng is not null) Hint.Text = "";

        var sb = new StringBuilder();
        sb.AppendLine($"ENGINE        {ocrName}{(paused ? "   [PAUSED]" : "")}");
        sb.AppendLine($"TIME          {f.At.ToLocalTime():HH:mm:ss.fff}");
        if (f.Error is not null) sb.AppendLine($"ERROR         {f.Error}");
        sb.AppendLine($"SCREEN ROI    x={f.ScreenRect.X} y={f.ScreenRect.Y} {f.ScreenRect.Width}×{f.ScreenRect.Height}px");
        sb.AppendLine($"TIMING        capture {f.CaptureTime.TotalMilliseconds:F1} ms · total {f.TotalTime.TotalMilliseconds:F1} ms · OCR rate {f.OcrPerSecond:F1}/s" +
                      (f.Analysis?.Reused == true ? " · unchanged (OCR skipped)" : ""));
        sb.AppendLine();

        if (f.Analysis is { } a)
        {
            sb.AppendLine("OCR RAW:");
            foreach (var at in a.Attempts)
                sb.AppendLine($"  [{at.Variant}, {at.Elapsed.TotalMilliseconds:F0} ms] \"{at.Text.Replace("\n", " ⏎ ")}\"");
            sb.AppendLine("NORMALIZED:");
            if (a.Reading.AllNumbers.Count == 0) sb.AppendLine("  (no numbers)");
            foreach (var n in a.Reading.AllNumbers)
                sb.AppendLine($"  \"{n.Raw}\" → {n.Value}{(n.Substitutions > 0 ? $"  ({n.Substitutions} OCR substitution(s))" : "")}{(n.HasRsLabel ? "  [RS label]" : "")}");
            sb.AppendLine($"DETECTED RS:  {a.Reading.RS?.ToString() ?? "-"}");
            sb.AppendLine();
        }

        sb.AppendLine($"VOTES (last {f.VoteBuffer.Count}):  {string.Join("  ", f.VoteBuffer.Select(v => v?.ToString() ?? "·"))}");
        sb.AppendLine($"STABLE RS:    {f.State.StableRS?.ToString() ?? "-"}  ({f.State.Votes}/{f.State.WindowCount} votes){(f.State.IsHeld ? "  [held]" : "")}");

        if (f.State.Match is { } m)
        {
            var ctx = f.State.ContextInferred
                ? $"{f.State.Context}, inferred from {f.State.ContextEvidence}"
                : _context == Core.Materials.ScanContext.Auto ? "Auto (location not detected yet)" : $"{f.State.Context}, set manually";
            sb.AppendLine($"STATUS:       {m.Status.ToString().ToUpperInvariant()}   (context: {ctx})");
            sb.AppendLine("CANDIDATES:");
            if (m.Candidates.Count == 0) sb.AppendLine("  (none within tolerance)");
            foreach (var c in m.Candidates.Take(6))
                sb.AppendLine($"  {c.Label,-40} expected {c.ExpectedRS,6}  err {c.Error,2}  {c.Confidence,5:P0}  [{c.Material.Kind}{(c.Material.IsOre ? "/" + c.Material.Priority : "")}]");
        }
        sb.AppendLine();
        sb.AppendLine($"FINAL:        {Final(view, f.State.Match)}");

        Report.Text = sb.ToString();
    }

    private static string Final(OverlayView v, MatchResult? m) => v.Kind switch
    {
        OverlayKind.Idle => "-",
        OverlayKind.Match => $"{v.Headline} · {v.RocksLine}{(v.Note is null ? "" : " · " + v.Note)}{(v.ConfidenceLine is null ? "" : " · " + v.ConfidenceLine)}",
        OverlayKind.Ambiguous => "AMBIGUOUS: " + string.Join(" | ", v.Alternatives),
        OverlayKind.NotTarget => $"not a target ({m?.Best?.Material.Name})",
        _ => "UNKNOWN",
    };

    private void Scan_Click(object sender, RoutedEventArgs e) => ScanRequested?.Invoke();
    private void Snapshot_Click(object sender, RoutedEventArgs e) => SnapshotRequested?.Invoke();
    private void Analyze_Click(object sender, RoutedEventArgs e) => AnalyzeRequested?.Invoke();

    public void ShowMessage(string text) => Hint.Text = text;
}
