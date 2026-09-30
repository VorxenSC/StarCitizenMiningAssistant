using MiningAssistant.Core.Matching;
using MiningAssistant.Core.Ocr;

namespace MiningAssistant.Tests;

public class OcrNormalizationTests
{
    private readonly OcrNumberNormalizer _n = new();

    [Theory]
    [InlineData("3585", 3585, 0)]
    [InlineData("3S85", 3585, 1)]
    [InlineData("35B5", 3585, 1)]
    [InlineData("3B70", 3870, 1)]
    [InlineData("358S", 3585, 1)]
    [InlineData("3O85", 3085, 1)]
    [InlineData("4l95", 4195, 1)]
    [InlineData("14,340", 14340, 0)]
    [InlineData("14.340", 14340, 0)]
    [InlineData("(10755)", 10755, 0)]
    public void Normalizes_numeric_tokens(string raw, int expected, int subs)
    {
        Assert.True(_n.TryNormalize(raw, out var v, out var s));
        Assert.Equal(expected, v);
        Assert.Equal(subs, s);
    }

    [Theory]
    [InlineData("RS")]
    [InlineData("MASS")]
    [InlineData("SOS")]      // all confusables, no real digits
    [InlineData("SBS5")]     // more substitutions than digits
    [InlineData("1.72")]     // decimal, not an RS
    [InlineData("GOLD")]
    [InlineData("3585m")]    // unknown trailing letter
    [InlineData("")]
    public void Rejects_words_and_decimals(string raw) =>
        Assert.False(_n.TryNormalize(raw, out _, out _));

    [Fact]
    public void Extracts_rs_from_labelled_line()
    {
        var tokens = _n.ExtractNumbers("RS: 358S");
        var t = Assert.Single(tokens);
        Assert.Equal(3585, t.Value);
        Assert.True(t.HasRsLabel);
    }

    [Fact]
    public void Extracts_attached_prefix_and_spaced_thousands()
    {
        Assert.Equal(3870, Assert.Single(_n.ExtractNumbers("RS:3870")).Value);
        Assert.Equal(14340, Assert.Single(_n.ExtractNumbers("14 340")).Value);
    }

    [Fact]
    public void Extractor_prefers_labelled_and_matching_values()
    {
        var extractor = new SignatureExtractor(_n, TestData.Matcher());
        var reading = extractor.Extract("DIST 1250\nRS 10755\n87%");
        Assert.Equal(10755, reading.RS);

        var unlabelled = extractor.Extract("1250 3B70");
        Assert.Equal(3870, unlabelled.RS); // 3870 explains a material, 1250 does not
    }

    [Fact]
    public void Extractor_returns_null_when_no_number()
    {
        var extractor = new SignatureExtractor(_n, TestData.Matcher());
        Assert.Null(extractor.Extract("SCANNING...").RS);
    }

    [Fact]
    public void Substitution_limit_is_configurable()
    {
        var strict = new OcrNumberNormalizer(new NumberNormalizerOptions { MaxSubstitutions = 0 });
        Assert.False(strict.TryNormalize("358S", out _, out _));
        Assert.True(strict.TryNormalize("3585", out _, out _));
    }
}
