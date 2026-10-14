using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using MiningAssistant.Core.Matching;
using MiningAssistant.Core.Roi;
using MiningAssistant.Core.Settings;
using MiningAssistant.Vision.Capture;
using MiningAssistant.Vision.Imaging;

namespace MiningAssistant.Vision.Pipeline;

/// <summary>Everything known about the moment the user pressed the snapshot key.</summary>
public sealed record SnapshotContext(
    MonitorInfo Monitor,
    PixelRect SearchArea,       // monitor-relative pixels
    AppSettings Settings,
    string GameVersion,
    string? LiveStatus,         // what the control panel showed
    string? OverlayShowing,     // what the overlay card showed
    string? Note);

/// <summary>
/// Writes a self-contained debug snapshot folder (debug_snapshots/yyyyMMdd_HHmmss/) so a specific
/// in-game situation can be analysed later, step by step:
///   1_screen.png            full game monitor
///   2_search_area.png       what the scanner searches
///   2_search_area_marked    same, with every candidate and the chosen plate outlined
///   3_plate.png             the located RS plate (if any)
///   4_ocr_NN_variant.png    every image sent to OCR, with what it read (in report.txt)
///   report.txt              locator trace, OCR results, final reading and match, settings
/// Local only.
/// </summary>
public sealed class DebugSnapshotWriter
{
    private readonly string _root;

    public DebugSnapshotWriter(string root) => _root = root;

    public async Task<string> SaveAsync(Bitmap screen, SnapshotContext ctx, ScanPipeline pipeline, SignatureMatcher matcher, CancellationToken ct = default)
    {
        var stamp = DateTime.Now;
        var dir = Path.Combine(_root, stamp.ToString("yyyyMMdd_HHmmss_fff"));
        Directory.CreateDirectory(dir);
        var r = new StringBuilder();
        r.AppendLine($"Mining Assistant debug snapshot — {stamp:yyyy-MM-dd HH:mm:ss.fff}");
        if (!string.IsNullOrWhiteSpace(ctx.Note)) r.AppendLine($"Note: {ctx.Note}");
        r.AppendLine($"Monitor: {ctx.Monitor}");
        r.AppendLine($"Search area: {ctx.SearchArea.Width}×{ctx.SearchArea.Height} px at ({ctx.SearchArea.X},{ctx.SearchArea.Y})");
        r.AppendLine($"DB version: {ctx.GameVersion} · context setting: {ctx.Settings.Matcher.Context} · auto-locate: {ctx.Settings.Ocr.AutoLocate}");
        r.AppendLine();

        screen.Save(Path.Combine(dir, "1_screen.png"), ImageFormat.Png);

        using var search = ImageOps.Crop(screen, ctx.SearchArea);
        search.Save(Path.Combine(dir, "2_search_area.png"), ImageFormat.Png);

        // Locator, with the reason for every icon-shaped candidate.
        var trace = new List<string>();
        var plates = PlateLocator.Find(search, new PlateLocatorOptions { ScreenHeight = ctx.Monitor.Height, Trace = trace.Add });
        r.AppendLine("── PLATE LOCATOR ──");
        r.AppendLine(plates.Count == 0 ? "No RS plate found." : $"{plates.Count} plate(s) found; using the first.");
        foreach (var p in plates)
            r.AppendLine($"  plate: icon {p.Icon.Width}×{p.Icon.Height} at ({p.Icon.X},{p.Icon.Y}), digits {p.Digits.Width}×{p.Digits.Height} at ({p.Digits.X},{p.Digits.Y})");
        r.AppendLine("Candidates (icon-shaped blobs) and why they were accepted / rejected:");
        foreach (var t in trace) r.AppendLine("  " + t);
        if (trace.Count == 0) r.AppendLine("  (none: no amber pin-shaped blob in the search area)");
        r.AppendLine();

        using (var marked = new Bitmap(search))
        using (var g = Graphics.FromImage(marked))
        using (var pen = new Pen(Color.Lime, 2))
        using (var digitsPen = new Pen(Color.Red, 2))
        {
            foreach (var p in plates)
            {
                g.DrawRectangle(pen, p.Icon.X - 2, p.Icon.Y - 2, p.Icon.Width + 3, p.Icon.Height + 3);
                g.DrawRectangle(digitsPen, p.Digits.X - 2, p.Digits.Y - 2, p.Digits.Width + 3, p.Digits.Height + 3);
            }
            marked.Save(Path.Combine(dir, "2_search_area_marked.png"), ImageFormat.Png);
        }

        if (plates.Count > 0)
        {
            var plate = plates[0];
            var b = plate.Bounds;
            using (var crop = ImageOps.Crop(search, new PixelRect(Math.Max(0, b.X - 16), Math.Max(0, b.Y - 16), b.Width + 32, b.Height + 32)))
                crop.Save(Path.Combine(dir, "3_plate.png"), ImageFormat.Png);

            r.AppendLine("── OCR ON THE PLATE DIGITS (every attempt) ──");
            var reads = await pipeline.ReadAllPlateVariantsAsync(search, plate, ct).ConfigureAwait(false);
            for (var i = 0; i < reads.Count; i++)
            {
                var rd = reads[i];
                var file = $"4_ocr_{i + 1:00}_{rd.Variant}_{rd.Zoom:F1}.png";
                rd.Image.Save(Path.Combine(dir, file), ImageFormat.Png);
                rd.Image.Dispose();
                r.AppendLine($"  {file,-34} read “{rd.Text.Replace("\n", " ⏎ ")}”  → {rd.Value?.ToString() ?? "no number"}");
            }
            r.AppendLine();
        }

        // What the live pipeline concludes for this exact frame.
        var final = await pipeline.AnalyzeSearchAsync(search, ctx.Monitor.Height, false, ct).ConfigureAwait(false);
        r.AppendLine("── RESULT FOR THIS FRAME ──");
        r.AppendLine($"Read: {final.Reading.RS?.ToString("N0") ?? "—"}   (attempts: {final.Attempts.Count}, {final.OcrTime.TotalMilliseconds:F0} ms)");
        if (final.Reading.RS is int rs)
        {
            var m = matcher.Match(rs, final.Reading.Chosen?.Substitutions ?? 0);
            r.AppendLine($"Status: {m.Status}");
            foreach (var c in m.Candidates.Take(5)) r.AppendLine($"  {c.Describe()}");
        }
        r.AppendLine();

        r.AppendLine("── APP STATE WHEN THE KEY WAS PRESSED ──");
        r.AppendLine($"Overlay: {ctx.OverlayShowing ?? "(nothing)"}");
        if (ctx.LiveStatus is not null) r.AppendLine(ctx.LiveStatus.TrimEnd());
        r.AppendLine();
        r.AppendLine("── SETTINGS ──");
        r.AppendLine(Core.Json.Serialize(ctx.Settings));

        await File.WriteAllTextAsync(Path.Combine(dir, "report.txt"), r.ToString(), new UTF8Encoding(true), ct).ConfigureAwait(false);
        return dir;
    }
}
