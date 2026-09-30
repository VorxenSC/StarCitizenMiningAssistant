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
            .ThenByDescending(n => _matcher.Match(n.Value).Status != MatchStatus.Unknown)
            .ThenBy(n => n.Substitutions)
            .ThenByDescending(n => n.Value)
            .FirstOrDefault();
        return new SignatureReading(chosen, numbers);
    }
}
