using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Text.RegularExpressions;
using MiningAssistant.Core;
using MiningAssistant.Core.Matching;
using MiningAssistant.Core.Materials;
using MiningAssistant.Core.Roi;
using MiningAssistant.Core.Settings;
using MiningAssistant.Vision.Capture;
using MiningAssistant.Vision.Ocr;
using MiningAssistant.Vision.Pipeline;

Monitors.EnablePerMonitorDpiAwareness();
Console.OutputEncoding = System.Text.Encoding.UTF8;

var paths = new AppPaths();
var cliArgs = args.ToList();
string? Opt(string name)
{
    var i = cliArgs.IndexOf(name);
    if (i < 0 || i + 1 >= cliArgs.Count) return null;
    var v = cliArgs[i + 1];
    cliArgs.RemoveRange(i, 2);
    return v;
}
bool Flag(string name) => cliArgs.Remove(name);

var version = Opt("--version") ?? "default";
var profilePath = Opt("--profile");
var upscaleOpt = Opt("--upscale");
var contextOpt = Opt("--context");
var preprocessOpt = Opt("--preprocess");
var dumpDir = Opt("--dump");
if (Opt("--white-threshold") is { } wt)
    MiningAssistant.Vision.Imaging.ImageOps.WhiteTextThreshold = double.Parse(wt, System.Globalization.CultureInfo.InvariantCulture);
var whole = Flag("--whole");
var verbose = Flag("-v");

if (cliArgs.Count == 0) return Usage();
var db = MaterialDatabaseLoader.LoadForVersion(paths.DataRoot, version);
var settings = new AppSettings { GameVersion = version };
if (upscaleOpt is not null) settings.Ocr.Upscale = int.Parse(upscaleOpt);
if (contextOpt is not null) settings.Matcher.Context = Enum.Parse<ScanContext>(contextOpt, true);
if (preprocessOpt is not null) settings.Ocr.Preprocess = Enum.Parse<PreprocessMode>(preprocessOpt, true);
var matcher = new SignatureMatcher(db, settings.Matcher);

switch (cliArgs[0].ToLowerInvariant())
{
    case "match":
        foreach (var a in cliArgs.Skip(1))
            PrintMatch(int.TryParse(a, out var rs) ? matcher.Match(rs) : null, a);
        return 0;

    case "ocr-langs":
        Console.WriteLine("Installed Windows OCR languages: " + string.Join(", ", WindowsOcrEngine.AvailableLanguages));
        return 0;

    case "analyze":
    {
        if (cliArgs.Count < 2) return Usage();
        var pipeline = CreatePipeline();
        if (pipeline is null) return 2;
        foreach (var file in ExpandImages(cliArgs[1]))
        {
            var profile = whole ? null : ResolveProfile(file);
            using var bmp = new Bitmap(file);
            var r = await pipeline.AnalyzeScreenshotAsync(bmp, profile, dumpDir is not null);
            if (dumpDir is not null && r.ProcessedPng is { } png)
            {
                Directory.CreateDirectory(dumpDir);
                File.WriteAllBytes(Path.Combine(dumpDir, Path.GetFileNameWithoutExtension(file) + ".ocr.png"), png);
            }
            Console.WriteLine($"── {Path.GetFileName(file)}  [{(profile is null ? "whole image" : "ROI")}; {r.OcrTime.TotalMilliseconds:F0} ms]");
            Console.WriteLine("OCR RAW:     " + r.RawText);
            Console.WriteLine("NUMBERS:     " + string.Join(", ", r.Reading.AllNumbers.Select(n => $"{n.Raw}→{n.Value}{(n.Substitutions > 0 ? $" ({n.Substitutions} subs)" : "")}")));
            Console.WriteLine("DETECTED RS: " + (r.Reading.RS?.ToString() ?? "-"));
            PrintMatch(r.Match, r.Reading.RS?.ToString() ?? "-");
        }
        return 0;
    }

    case "regress":
    {
        var folder = cliArgs.Count > 1 ? cliArgs[1] : Path.Combine(FindRepoRoot() ?? ".", "test_images");
        var pipeline = CreatePipeline();
        if (pipeline is null) return 2;
        int pass = 0, fail = 0, skipped = 0;
        foreach (var file in ExpandImages(folder))
        {
            var m = Regex.Match(Path.GetFileName(file), @"rs[-_]?(\d{3,6})", RegexOptions.IgnoreCase);
            if (!m.Success) { skipped++; continue; }
            var expected = int.Parse(m.Groups[1].Value);
            var profile = whole ? null : ResolveProfile(file);
            using var bmp = new Bitmap(file);
            var r = await pipeline.AnalyzeScreenshotAsync(bmp, profile, false);
            var ok = r.Reading.RS == expected;
            if (ok) pass++; else fail++;
            var label = r.Match?.Best is { } b ? $"{b.Material.Name} ×{b.RockCount} {r.Match.Status}" : r.Match?.Status.ToString() ?? "no RS";
            if (!ok || verbose)
                Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {Path.GetRelativePath(folder, file),-50} expected {expected,6}  got {r.Reading.RS?.ToString() ?? "-",6}  {label}" +
                                  (ok ? "" : $"\n      raw: {r.RawText}"));
        }
        Console.WriteLine($"\n{pass} passed, {fail} failed, {skipped} skipped (no rsNNNN in file name). Mode: {(whole ? "whole image" : "ROI when profile.roi.json present")}");
        return fail == 0 ? 0 : 1;
    }

    case "live":
    {
        // Same capture path as the overlay: grabs only the RS region of the game monitor.
        var frames = cliArgs.Count > 1 && int.TryParse(cliArgs[1], out var nf) ? nf : 10;
        var pipeline = CreatePipeline();
        if (pipeline is null) return 2;
        var profile = profilePath is not null ? Json.Deserialize<RoiProfile>(File.ReadAllText(profilePath)) : paths.LoadProfile("default");
        var region = profile.Get(RoiNames.RS);
        if (region is null) { Console.WriteLine("No RS region in profile."); return 2; }
        var monitor = Monitors.Get(int.TryParse(Opt("--monitor"), out var mi) ? mi : 0);
        var rect = monitor.ToScreen(region.ToPixels(monitor.Width, monitor.Height));
        Console.WriteLine($"Monitor {monitor}; RS ROI {rect.Width}×{rect.Height} at ({rect.X},{rect.Y})");
        var capture = new GdiScreenCapture();
        for (var f = 0; f < frames; f++)
        {
            using var roi = capture.Capture(rect);
            var r = await pipeline.AnalyzeRoiAsync(roi, dumpDir is not null);
            if (dumpDir is not null)
            {
                Directory.CreateDirectory(dumpDir);
                var stamp = $"{DateTime.Now:HHmmss_fff}";
                roi.Save(Path.Combine(dumpDir, $"live_{stamp}_rs{r.Reading.RS?.ToString() ?? "none"}.png"), ImageFormat.Png);
                if (r.ProcessedPng is { } png) File.WriteAllBytes(Path.Combine(dumpDir, $"live_{stamp}.ocr.png"), png);
            }
            var label = r.Match?.Best is { } b ? $"{b.Material.Name} ×{b.RockCount} {r.Match.Status}" : r.Match?.Status.ToString() ?? "-";
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  RS {r.Reading.RS?.ToString() ?? "-",6}  {label,-28} {r.OcrTime.TotalMilliseconds,4:F0} ms  raw: {r.RawText.Replace("\n", " | ")}");
            await Task.Delay(500);
        }
        return 0;
    }

    case "synth":
    {
        var outDir = cliArgs.Count > 1 ? cliArgs[1] : Path.Combine(FindRepoRoot() ?? ".", "test_images", "synthetic");
        Synth.Generate(outDir, db);
        return 0;
    }

    default:
        return Usage();
}

ScanPipeline? CreatePipeline()
{
    var ocr = WindowsOcrEngine.TryCreate(settings.Ocr.Language, out var info);
    Console.WriteLine(info);
    return ocr is null ? null : new ScanPipeline(ocr, matcher, settings.Ocr);
}

RoiProfile? ResolveProfile(string imageFile)
{
    var p = profilePath ?? Path.Combine(Path.GetDirectoryName(imageFile)!, "profile.roi.json");
    return File.Exists(p) ? Json.Deserialize<RoiProfile>(File.ReadAllText(p)) : null;
}

static IEnumerable<string> ExpandImages(string pathOrDir) =>
    Directory.Exists(pathOrDir)
        ? Directory.EnumerateFiles(pathOrDir, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f)
        : new[] { pathOrDir };

static string? FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MiningAssistant.sln"))) dir = dir.Parent;
    return dir?.FullName;
}

static void PrintMatch(MatchResult? r, string input)
{
    if (r is null) { Console.WriteLine($"{input}: not a number"); return; }
    Console.WriteLine($"RS {r.ScannedRS}: {r.Status.ToString().ToUpperInvariant()}");
    foreach (var c in r.Candidates.Take(5))
        Console.WriteLine($"   {c.Label,-40} expected {c.ExpectedRS,6}  err {c.Error,2}  confidence {c.Confidence,4:P0}  [{c.Material.Kind}{(c.Material.IsOre ? "/" + c.Material.Priority : "")}]");
}

static int Usage()
{
    Console.WriteLine("""
        mining-cli — offline tools for Star Citizen Mining Assistant

          match <rs> [rs...]            Explain RS values (materials × rock count, confidence)
          analyze <image|folder>        OCR screenshot(s) and identify the RS
          regress [folder]              Regression test: files named *rs<NNNN>*.png must read that RS
          synth [folder]                Generate synthetic HUD-like test images (+ profile.roi.json)
          live [frames]                 Capture the RS region from the game monitor and OCR it (--dump saves crops)
          ocr-langs                     List installed Windows OCR languages

        Options:
          --version <v>    materials database folder under data/ (default: "default")
          --context <c>    Auto | Space | Surface (rules out signals that cannot appear there)
          --profile <file> ROI profile to crop screenshots (default: profile.roi.json next to the image)
          --whole          ignore profiles; OCR the whole image
          --upscale <n>    ROI upscale factor before OCR (default 3)
          --preprocess <m> Auto | Grayscale | Binarize
          --dump <dir>     analyze: save the preprocessed image sent to OCR
          -v               verbose (regress: also print passing files)
        """);
    return 1;
}

/// <summary>Generates HUD-like frames: dark noisy background, colored digits, distractor text.</summary>
static class Synth
{
    public static void Generate(string outDir, MaterialDatabase db)
    {
        Directory.CreateDirectory(outDir);
        var rng = new Random(1234);
        const int W = 1720, H = 720;
        // RS text lives in a fixed box so a single ROI profile applies to all frames.
        var roi = new Rectangle(1180, 300, 240, 60);
        var cases = new (string name, int rs)[]
        {
            ("gold-x1", 3585), ("gold-x2", 7170), ("gold-x3", 10755), ("gold-x4", 14340), ("gold-x5", 17925),
            ("tungsten-x1", 3870), ("tungsten-x2", 7740), ("tungsten-x3", 11610),
            ("bexalite-x1", 3600), ("laranite-x1", 3825), ("quantainium-x1", 3170), ("ice-x1", 4300),
            ("agricium-x2", 7770), ("hephaestanite-x3", 12540), ("unknown", 5000),
        };
        var fonts = new[] { "Segoe UI Semibold", "Consolas", "Bahnschrift" };
        var colors = new[] { Color.FromArgb(120, 220, 255), Color.FromArgb(255, 190, 90), Color.FromArgb(200, 255, 220) };

        var i = 0;
        foreach (var (name, rs) in cases)
        {
            var fontName = fonts[i % fonts.Length];
            var color = colors[i % colors.Length];
            var grouped = i % 4 == 3; // some frames show "14,340" style grouping
            using var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                using var bg = new LinearGradientBrush(new Rectangle(0, 0, W, H), Color.FromArgb(8, 14, 22), Color.FromArgb(30, 38, 52), 35f);
                g.FillRectangle(bg, 0, 0, W, H);
                for (var n = 0; n < 4000; n++)
                {
                    var c = rng.Next(20, 70);
                    bmp.SetPixel(rng.Next(W), rng.Next(H), Color.FromArgb(c, c + 5, c + 12));
                }
                using var hud = new Pen(Color.FromArgb(90, color), 2);
                g.DrawEllipse(hud, 560, 110, 600, 500);
                using var small = new Font(fontName, 14, FontStyle.Regular, GraphicsUnit.Pixel);
                using var dim = new SolidBrush(Color.FromArgb(150, color));
                g.DrawString($"DIST {rng.Next(800, 4000)}m", small, dim, 300, 560);
                g.DrawString("SCANNING", small, dim, 820, 640);

                using var font = new Font(fontName, 22 + i % 3 * 4, FontStyle.Bold, GraphicsUnit.Pixel);
                using var brush = new SolidBrush(color);
                var text = grouped ? rs.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) : rs.ToString();
                g.DrawString(text, font, brush, roi.X + 12 + rng.Next(0, 30), roi.Y + 10 + rng.Next(0, 8));
                g.DrawString("◆", small, brush, roi.X - 18, roi.Y + 20);
            }
            bmp.Save(Path.Combine(outDir, $"{name}__rs{rs}.png"), ImageFormat.Png);
            i++;
        }

        var profile = new RoiProfile
        {
            Name = "synthetic",
            CalibratedWidth = W,
            CalibratedHeight = H,
            CalibratedAt = DateTime.UtcNow,
            Regions = { [RoiNames.RS] = NormalizedRect.FromPixels(new PixelRect(roi.X, roi.Y, roi.Width, roi.Height), W, H) },
        };
        File.WriteAllText(Path.Combine(outDir, "profile.roi.json"), Json.Serialize(profile));
        Console.WriteLine($"Wrote {cases.Length} images + profile.roi.json to {outDir}");
    }
}
