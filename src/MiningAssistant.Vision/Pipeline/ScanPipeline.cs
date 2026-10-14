using System.Diagnostics;
using System.Drawing;
using MiningAssistant.Core.Matching;
using MiningAssistant.Core.Ocr;
using MiningAssistant.Core.Roi;
using MiningAssistant.Core.Settings;
using MiningAssistant.Vision.Imaging;
using MiningAssistant.Vision.Ocr;

namespace MiningAssistant.Vision.Pipeline;

public sealed record OcrAttempt(Variant Variant, string Text, TimeSpan Elapsed);

public sealed record FrameAnalysis(
    IReadOnlyList<OcrAttempt> Attempts,
    SignatureReading Reading,
    MatchResult? Match,
    TimeSpan OcrTime,
    /// <summary>PNG of the image fed to OCR in the chosen attempt; only when requested (debug).</summary>
    byte[]? ProcessedPng,
    /// <summary>True when OCR was skipped because the ROI pixels did not change.</summary>
    bool Reused = false,
    /// <summary>True when OCR was skipped because the ROI holds no HUD text at all (no RS indicator).</summary>
    bool NoText = false,
    /// <summary>The RS plate found by the locator (coordinates inside the analyzed image), when auto-locate is on.</summary>
    Plate? Plate = null)
{
    public string RawText => string.Join("\n", Attempts.Select(a => $"[{a.Variant}] {a.Text.Replace("\n", " ⏎ ")}"));
}

/// <summary>ROI image → preprocessing → OCR → normalization → RS choice → material match.</summary>
public sealed class ScanPipeline
{
    private readonly IOcrEngine _ocr;
    private readonly SignatureExtractor _extractor;
    private readonly SignatureMatcher _matcher;
    private readonly OcrSettings _settings;

    public ScanPipeline(IOcrEngine ocr, SignatureMatcher matcher, OcrSettings settings)
    {
        _ocr = ocr;
        _matcher = matcher;
        _settings = settings;
        _extractor = new SignatureExtractor(new OcrNumberNormalizer(settings.Normalizer), matcher);
    }

    public string OcrName => _ocr.Name;

    private IEnumerable<(Variant variant, int scale)> Attempts(int scale)
    {
        var variants = _settings.Preprocess switch
        {
            PreprocessMode.Grayscale => new[] { Variant.Grayscale },
            PreprocessMode.Binarize => new[] { Variant.Binarized },
            PreprocessMode.WhiteText => new[] { Variant.WhiteText },
            PreprocessMode.Amber => new[] { Variant.Amber },
            _ => new[] { Variant.WhiteText, Variant.Amber, Variant.Whiteness, Variant.Grayscale, Variant.Binarized },
        };
        foreach (var v in variants) yield return (v, scale);
        // Last resort only reached when nothing matched or agreed: a different scale often fixes
        // glyphs the OCR merged or split at the primary scale.
        if (_settings.Preprocess == PreprocessMode.Auto && scale > 2) yield return (Variant.Grayscale, scale - 1);
    }

    public Task<FrameAnalysis> AnalyzeRoiAsync(Bitmap roi, bool keepDebugImage, CancellationToken ct = default)
    {
        // No RS indicator on screen (just space/terrain): skip OCR entirely. Measured on real 1440p
        // captures: indicator contrast 180–227 vs empty regions 3–24.
        if (_settings.SkipEmptyRoi && !ImageOps.HasText(roi))
            return Task.FromResult(new FrameAnalysis(Array.Empty<OcrAttempt>(),
                new SignatureReading(null, Array.Empty<NumberToken>()), null, TimeSpan.Zero, null, NoText: true));
        return AnalyzeAsync(roi, _settings.Upscale, keepDebugImage, ct);
    }

    private static readonly SignatureReading Empty = new(null, Array.Empty<NumberToken>());

    // Tried in order until one gives a clean, simply-explained value (usually the first, in a few ms).
    // The extra zoom levels only run on hard frames: small HUDs (1080p and below) are sensitive to how
    // the few pixels of each digit land on the OCR's grid.
    public static readonly IReadOnlyList<(Variant variant, double zoom)> PlatePlan = new (Variant, double)[]
    {
        (Variant.PlateGreen, 1.0), (Variant.PlateGreenSoft, 1.0), (Variant.PlateGreen, 0.8), (Variant.PlateGreen, 1.2),
        (Variant.PlateWhite, 1.0), (Variant.PlateGreenSoft, 0.8), (Variant.PlateWhite, 0.8), (Variant.PlateBright, 1.0),
    };

    /// <summary>One OCR attempt on a located plate, kept for diagnostics (caller disposes <see cref="Image"/>).</summary>
    public sealed record PlateRead(Variant Variant, double Zoom, string Text, int? Value, Bitmap Image);

    /// <summary>Runs every attempt of <see cref="PlatePlan"/> (no early stop) — for debug snapshots.</summary>
    public async Task<IReadOnlyList<PlateRead>> ReadAllPlateVariantsAsync(Bitmap search, Plate plate, CancellationToken ct = default)
    {
        var reads = new List<PlateRead>();
        foreach (var (variant, zoom) in PlatePlan)
        {
            var img = ImageOps.PrepareDigits(search, plate, variant, zoom);
            var text = (await _ocr.RecognizeAsync(img, ct).ConfigureAwait(false)).Text;
            reads.Add(new PlateRead(variant, zoom, text, _extractor.Extract(text).RS, img));
        }
        return reads;
    }

    /// <summary>
    /// Auto-locate path: finds the amber RS plate anywhere in <paramref name="search"/> and reads only
    /// the digits inside it. Independent of where the HUD is, of the resolution and of the background.
    /// Returns NoText (with no plate) when no RS indicator is on screen.
    /// </summary>
    /// <param name="screenHeight">Height of the screen the image was captured from, or 0 if unknown.</param>
    public async Task<FrameAnalysis> AnalyzeSearchAsync(Bitmap search, int screenHeight, bool keepDebugImage, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var plates = PlateLocator.Find(search, new PlateLocatorOptions { ScreenHeight = screenHeight });
        if (plates.Count == 0)
            return new FrameAnalysis(Array.Empty<OcrAttempt>(), Empty, null, sw.Elapsed, null, NoText: true);

        var plate = plates[0];
        return await RunAttemptsAsync(
            PlatePlan.Select(p => (p.variant, (Func<Bitmap>)(() => ImageOps.PrepareDigits(search, plate, p.variant, p.zoom)))),
            plate, keepDebugImage, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Offline analysis of a saved screenshot. With auto-locate it searches the profile's RS region (or
    /// the whole image) for the RS plate; otherwise, or when no plate is found, it falls back to
    /// reading the region as text.
    /// </summary>
    public async Task<FrameAnalysis> AnalyzeScreenshotAsync(Bitmap screenshot, RoiProfile? profile, bool keepDebugImage, CancellationToken ct = default)
    {
        var region = profile?.Get(RoiNames.RS);
        if (_settings.AutoLocate)
        {
            // Only trust the image height as "screen height" for full-frame screenshots.
            var screenHeight = screenshot.Height >= 700 ? screenshot.Height : 0;
            using var search = region is null ? null : ImageOps.Crop(screenshot, _settings.SearchArea(region).ToPixels(screenshot.Width, screenshot.Height));
            var located = await AnalyzeSearchAsync(search ?? screenshot, screenHeight, keepDebugImage, ct).ConfigureAwait(false);
            if (located.Plate is not null) return located;
        }

        if (region is not null)
        {
            using var roi = ImageOps.Crop(screenshot, region.ToPixels(screenshot.Width, screenshot.Height));
            return await AnalyzeRoiAsync(roi, keepDebugImage, ct).ConfigureAwait(false);
        }
        // Whole frame: keep it cheap (no upscale for large images).
        return await AnalyzeAsync(screenshot, screenshot.Width >= 1920 ? 1 : 2, keepDebugImage, ct).ConfigureAwait(false);
    }

    private Task<FrameAnalysis> AnalyzeAsync(Bitmap image, int scale, bool keepDebugImage, CancellationToken ct) =>
        RunAttemptsAsync(Attempts(scale).Select(a => (a.variant, (Func<Bitmap>)(() => ImageOps.Prepare(image, a.scale, a.variant)))), null, keepDebugImage, ct);

    private async Task<FrameAnalysis> RunAttemptsAsync(IEnumerable<(Variant variant, Func<Bitmap> prepare)> plan, Plate? plate, bool keepDebugImage, CancellationToken ct)
    {
        var attempts = new List<OcrAttempt>();
        var total = Stopwatch.StartNew();
        SignatureReading? best = null;
        MatchResult? bestMatch = null;
        byte[]? png = null;

        // Stop at the first variant whose number a known material explains cleanly, or as soon as two
        // variants agree on the same value (e.g. a genuine unknown RS); otherwise keep the best reading
        // (known material > fewer substitutions > any number).
        var seen = new HashSet<int>();
        foreach (var (variant, prepare) in plan)
        {
            var sw = Stopwatch.StartNew();
            using var prepared = prepare();
            var output = await _ocr.RecognizeAsync(prepared, ct).ConfigureAwait(false);
            attempts.Add(new OcrAttempt(variant, output.Text, sw.Elapsed));

            var reading = _extractor.Extract(output.Text);
            var match = reading.Chosen is { } t ? _matcher.Match(t.Value, t.Substitutions) : null;
            if (best is null || Score(reading, match) > Score(best, bestMatch))
            {
                best = reading;
                bestMatch = match;
                if (keepDebugImage) png = ImageOps.ToPng(prepared);
            }
            // Only a simple (homogeneous, non-ambiguous) explanation of a clean read ends the search early.
            // Mixed sums explain almost anything — including OCR mistakes like 15,420 → 16,420 — so they
            // must compete with the other variants' readings.
            if (IsSimple(match) && reading.Chosen!.Substitutions == 0) break;
            if (reading.RS is int v && !seen.Add(v)) break;
        }

        return new FrameAnalysis(attempts, best ?? Empty, bestMatch, total.Elapsed, png, Plate: plate);
    }

    private static bool IsSimple(MatchResult? m) => m is { IsMatch: true, Best.IsMixed: false };

    /// <summary>How believable a reading is: simple ore > simple other signal > ambiguous/mixed > unexplained.</summary>
    private static int Score(SignatureReading r, MatchResult? m)
    {
        if (r.Chosen is null) return 0;
        var s = m switch
        {
            { IsMatch: true, Best: { IsMixed: false, Material.IsOre: true } } => 100,
            { IsMatch: true, Best.IsMixed: false } => 80,
            { Status: MatchStatus.Ambiguous } when m.Candidates.Any(c => !c.IsMixed) => 60,
            { Status: not MatchStatus.Unknown } => 40,
            _ => 10,
        };
        return s - r.Chosen.Substitutions;
    }
}
