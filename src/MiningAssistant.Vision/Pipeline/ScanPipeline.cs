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

    private IEnumerable<Variant> Variants => _settings.Preprocess switch
    {
        PreprocessMode.Grayscale => new[] { Variant.Grayscale },
        PreprocessMode.Binarize => new[] { Variant.Binarized },
        _ => new[] { Variant.Grayscale, Variant.Binarized },
    };

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

        // Stop at the first variant whose number a known material explains cleanly; otherwise keep
        // trying and keep the best reading (known material > fewer substitutions > any number).
        foreach (var variant in Variants)
        {
            var sw = Stopwatch.StartNew();
            using var prepared = ImageOps.Prepare(image, scale, variant);
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
            if (match is { Status: not MatchStatus.Unknown } && reading.Chosen!.Substitutions == 0) break;
        }

        return new FrameAnalysis(attempts, best!, bestMatch, total.Elapsed, png);
    }

    private static int Score(SignatureReading r, MatchResult? m) =>
        r.Chosen is null ? 0
        : (m is { Status: not MatchStatus.Unknown } ? 100 : 10) - r.Chosen.Substitutions;
}
