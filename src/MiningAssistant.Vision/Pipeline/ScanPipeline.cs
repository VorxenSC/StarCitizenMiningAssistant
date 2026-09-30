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
    bool Reused = false)
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
            _ => new[] { Variant.WhiteText, Variant.Amber, Variant.Grayscale, Variant.Binarized },
        };
        foreach (var v in variants) yield return (v, scale);
        // Last resort only reached when nothing matched or agreed: a different scale often fixes
        // glyphs the OCR merged or split at the primary scale.
        if (_settings.Preprocess == PreprocessMode.Auto && scale > 2) yield return (Variant.Grayscale, scale - 1);
    }

    public Task<FrameAnalysis> AnalyzeRoiAsync(Bitmap roi, bool keepDebugImage, CancellationToken ct = default) =>
        AnalyzeAsync(roi, _settings.Upscale, keepDebugImage, ct);

    /// <summary>
    /// Offline analysis of a saved screenshot. Uses the profile's RS region when calibrated,
    /// otherwise OCRs the whole image and prefers numbers labelled "RS".
    /// </summary>
    public async Task<FrameAnalysis> AnalyzeScreenshotAsync(Bitmap screenshot, RoiProfile? profile, bool keepDebugImage, CancellationToken ct = default)
    {
        if (profile?.Get(RoiNames.RS) is { } region)
        {
            using var roi = ImageOps.Crop(screenshot, region.ToPixels(screenshot.Width, screenshot.Height));
            return await AnalyzeRoiAsync(roi, keepDebugImage, ct).ConfigureAwait(false);
        }
        // Whole frame: keep it cheap (no upscale for large images).
        return await AnalyzeAsync(screenshot, screenshot.Width >= 1920 ? 1 : 2, keepDebugImage, ct).ConfigureAwait(false);
    }

    private async Task<FrameAnalysis> AnalyzeAsync(Bitmap image, int scale, bool keepDebugImage, CancellationToken ct)
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
        foreach (var (variant, s) in Attempts(scale))
        {
            var sw = Stopwatch.StartNew();
            using var prepared = ImageOps.Prepare(image, s, variant);
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

        return new FrameAnalysis(attempts, best!, bestMatch, total.Elapsed, png);
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
