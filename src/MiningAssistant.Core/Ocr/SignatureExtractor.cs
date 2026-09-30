using MiningAssistant.Core.Matching;

namespace MiningAssistant.Core.Ocr;

public sealed record SignatureReading(NumberToken? Chosen, IReadOnlyList<NumberToken> AllNumbers)
{
    public int? RS => Chosen?.Value;
}

/// <summary>
/// Picks the most likely RS value among all numbers found in an ROI's OCR text.
/// Preference order: labelled "RS" → explained by a known material → fewer OCR substitutions → larger value.
/// </summary>
public sealed class SignatureExtractor
{
    private readonly OcrNumberNormalizer _normalizer;
    private readonly SignatureMatcher _matcher;

    public SignatureExtractor(OcrNumberNormalizer normalizer, SignatureMatcher matcher)
    {
        _normalizer = normalizer;
        _matcher = matcher;
    }

    public SignatureReading Extract(string ocrText)
    {
        var numbers = _normalizer.ExtractNumbers(ocrText);
        var chosen = numbers
            .Where(n => _matcher.IsPlausible(n.Value))
            .OrderByDescending(n => n.HasRsLabel)
            .ThenByDescending(n => Plausibility(_matcher.Match(n.Value)))
            .ThenBy(n => n.Substitutions)
            .ThenByDescending(n => n.Value)
            .FirstOrDefault();
        return new SignatureReading(chosen, numbers);
    }

    /// <summary>Simple (homogeneous) explanations beat mixed sums, which beat unexplained numbers.</summary>
    private static int Plausibility(MatchResult m) => m switch
    {
        { IsMatch: true, Best.IsMixed: false } => 3,
        { Status: MatchStatus.Ambiguous } when m.Candidates.Any(c => !c.IsMixed) => 2,
        { Status: not MatchStatus.Unknown } => 1,
        _ => 0,
    };
}
