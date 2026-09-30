using MiningAssistant.Core.Matching;

namespace MiningAssistant.Tests;

public class SignatureMatcherTests
{
    private readonly SignatureMatcher _matcher = TestData.Matcher();

    [Theory]
    [InlineData(3585, "Gold", 1)]
    [InlineData(7170, "Gold", 2)]
    [InlineData(10755, "Gold", 3)]
    [InlineData(14340, "Gold", 4)]
    [InlineData(17925, "Gold", 5)]
    [InlineData(3870, "Tungsten", 1)]
    [InlineData(7740, "Tungsten", 2)]
    [InlineData(11610, "Tungsten", 3)]
    [InlineData(3600, "Bexalite", 1)]
    [InlineData(3825, "Laranite", 1)]
    [InlineData(3170, "Quantainium", 1)]
    [InlineData(4300, "Ice", 1)]
    public void Exact_signatures_and_multiples(int rs, string material, int rocks)
    {
        var r = _matcher.Match(rs);

        Assert.Equal(MatchStatus.Exact, r.Status);
        Assert.Equal(material, r.Best!.Material.Name);
        Assert.Equal(rocks, r.Best.RockCount);
        Assert.False(r.Best.IsMixed);
        Assert.Equal(0, r.Best.Error);
        // Confidence can be < 100% in Auto context: exact mixed sums (e.g. 7170 = Quantainium + ROC) are
        // listed as low-probability alternatives. It must still clearly dominate.
        Assert.True(r.Best.Confidence > 0.75, $"confidence {r.Best.Confidence}");
    }

    [Theory]
    [InlineData(3585, "Gold", 1)]
    [InlineData(10755, "Gold", 3)]
    [InlineData(3870, "Tungsten", 1)]
    public void Single_explanations_are_fully_confident(int rs, string material, int rocks)
    {
        var r = TestData.Matcher(new SignatureMatcherOptions { Context = Core.Materials.ScanContext.Space }).Match(rs);
        Assert.Equal((material, rocks), (r.Best!.Material.Name, r.Best.RockCount));
        Assert.True(r.Best.Confidence > 0.95, $"confidence {r.Best.Confidence}");
    }

    [Fact]
    public void Off_by_one_is_probable_with_high_confidence()
    {
        var r = _matcher.Match(3584);

        Assert.Equal(MatchStatus.Probable, r.Status);
        Assert.Equal("Gold", r.Best!.Material.Name);
        Assert.InRange(r.Best.Confidence, 0.95, 0.99);
    }

    [Fact]
    public void Ocr_substitutions_prevent_exact_and_lower_confidence()
    {
        var clean = _matcher.Match(3585, 0);
        var patched = _matcher.Match(3585, 1); // read as "358S"

        Assert.Equal(MatchStatus.Exact, clean.Status);
        Assert.Equal(MatchStatus.Probable, patched.Status);
        Assert.Equal("Gold", patched.Best!.Material.Name);
        Assert.True(patched.Best.Confidence < clean.Best!.Confidence);
    }

    [Fact]
    public void Unexplained_signature_is_unknown()
    {
        // Not within tolerance of any N×base, and not a multiple of 5 so no exact mixed sum exists.
        // (5000 is NOT unknown any more: FPS deposit 3000 + salvage panel 2000.)
        var r = _matcher.Match(5001);

        Assert.Equal(MatchStatus.Unknown, r.Status);
        Assert.Null(r.Best);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(42)]
    [InlineData(999_999)]
    public void Implausible_values_are_unknown(int rs) =>
        Assert.Equal(MatchStatus.Unknown, _matcher.Match(rs).Status);

    [Fact]
    public void Close_explanations_are_reported_as_ambiguous_not_guessed()
    {
        // 6 × Gold = 21510 and 5 × Ice = 21500: 21505 sits between both.
        var r = _matcher.Match(21505);

        Assert.Equal(MatchStatus.Ambiguous, r.Status);
        var names = r.Candidates.Select(c => c.Material.Name).ToList();
        Assert.Contains("Gold", names);
        Assert.Contains("Ice", names);
    }

    [Fact]
    public void Ambiguous_confidences_share_the_probability()
    {
        var r = _matcher.Match(21505);
        var top2 = r.Candidates.Take(2).Sum(c => c.Confidence);
        Assert.True(top2 <= 1.0001);
        Assert.All(r.Candidates, c => Assert.InRange(c.Confidence, 0, 1));
    }

    [Fact]
    public void Disabled_materials_are_ignored()
    {
        var db = TestData.Database;
        var materials = db.Materials.Select(m => new Core.Materials.Material
        {
            Name = m.Name, BaseRS = m.BaseRS, Priority = m.Priority, Enabled = m.Name != "Gold",
        });
        var r = new SignatureMatcher(materials).Match(3585);
        Assert.Equal(MatchStatus.Unknown, r.Status);
    }

    [Fact]
    public void Tolerance_is_configurable()
    {
        var strict = TestData.Matcher(new SignatureMatcherOptions { MaxAbsoluteError = 0 });
        Assert.Equal(MatchStatus.Unknown, strict.Match(3584).Status);
        Assert.Equal(MatchStatus.Exact, strict.Match(3585).Status);
    }

    /// <summary>
    /// Every single-material cluster up to 6 rocks must either resolve to that material or be flagged
    /// Ambiguous with the true answer among the candidates — never a confident wrong answer.
    /// </summary>
    [Fact]
    public void Clusters_never_produce_a_confident_wrong_answer()
    {
        foreach (var m in TestData.Database.Ores)
        {
            for (var n = 1; n <= 6; n++)
            {
                var r = _matcher.Match(m.BaseRS * n);
                if (r.IsMatch)
                {
                    Assert.Equal(m.Name, r.Best!.Material.Name);
                    Assert.Equal(n, r.Best.RockCount);
                }
                else
                {
                    Assert.Equal(MatchStatus.Ambiguous, r.Status);
                    Assert.Contains(r.Candidates, c => c.Material.Name == m.Name && c.RockCount == n);
                }
            }
        }
    }
}
