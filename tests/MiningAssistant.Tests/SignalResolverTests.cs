using MiningAssistant.Core.Matching;
using MiningAssistant.Core.Materials;
using MiningAssistant.Core.Presentation;
using MiningAssistant.Core.Settings;
using MiningAssistant.Core.Temporal;

namespace MiningAssistant.Tests;

/// <summary>Non-ore signals (asteroid types, deposits, salvage), scan context and mixed clusters.</summary>
public class SignalResolverTests
{
    private static SignatureMatcher In(ScanContext ctx, Action<SignatureMatcherOptions>? tweak = null)
    {
        var o = new SignatureMatcherOptions { Context = ctx };
        tweak?.Invoke(o);
        return TestData.Matcher(o);
    }

    [Theory]
    [InlineData(4700, "C-Type Asteroid", 1)]
    [InlineData(4720, "S-Type Asteroid", 1)]
    [InlineData(4750, "P-Type Asteroid", 1)]
    [InlineData(4850, "M-Type Asteroid", 1)]
    [InlineData(4870, "Q-Type Asteroid", 1)]
    [InlineData(4900, "E-Type Asteroid", 1)]
    [InlineData(9740, "Q-Type Asteroid", 2)]
    [InlineData(4000, "I-Type Asteroid", 1)]
    public void Asteroid_types_in_space(int rs, string name, int count)
    {
        var r = In(ScanContext.Space).Match(rs);
        Assert.True(r.IsMatch, r.Status.ToString());
        Assert.Equal((name, count), (r.Best!.Material.Name, r.Best.RockCount));
        Assert.Equal(SignalKind.AsteroidType, r.Best.Material.Kind);
    }

    [Theory]
    [InlineData(3000, "FPS Deposit", 1)]
    [InlineData(9000, "FPS Deposit", 3)]
    [InlineData(4000, "ROC Deposit", 1)]
    [InlineData(8000, "ROC Deposit", 2)]
    public void Ground_deposits_on_surface(int rs, string name, int count)
    {
        var r = In(ScanContext.Surface).Match(rs);
        Assert.True(r.IsMatch, r.Status.ToString());
        Assert.Equal((name, count), (r.Best!.Material.Name, r.Best.RockCount));
    }

    [Theory]
    [InlineData(16000)] // Savrilium ×5 = ROC ×4 = salvage ×8
    [InlineData(12000)] // ROC ×3 = FPS ×4
    public void Coinciding_multiples_are_reported_as_ambiguous(int rs) =>
        Assert.Equal(MatchStatus.Ambiguous, In(ScanContext.Surface).Match(rs).Status);

    [Fact]
    public void Large_ground_deposit_fields_resolve_on_the_surface()
    {
        // Seen on Daymar: RS 51,000 = 17 FPS (hand-mining) deposits.
        var r = In(ScanContext.Surface).Match(51000);
        Assert.True(r.IsMatch, r.Status.ToString());
        Assert.Equal(("FPS Deposit", 17, false), (r.Best!.Material.Name, r.Best.RockCount, r.Best.IsMixed));
    }

    [Fact]
    public void Context_rules_out_impossible_signals()
    {
        Assert.Equal(MatchStatus.Unknown, In(ScanContext.Surface).Match(4870).Status); // no asteroids on the ground
        Assert.Equal(MatchStatus.Unknown, In(ScanContext.Space).Match(3000).Status);   // no FPS deposits in orbit
    }

    [Fact]
    public void Rs_4000_is_ambiguous_without_context()
    {
        var r = In(ScanContext.Auto).Match(4000);
        Assert.Equal(MatchStatus.Ambiguous, r.Status);
        var names = r.Candidates.Select(c => c.Material.Name).ToList();
        Assert.Contains("I-Type Asteroid", names);
        Assert.Contains("ROC Deposit", names);
        Assert.Contains("Salvage Panel", names);
    }

    [Theory]
    [InlineData(2000, 1)]
    [InlineData(6000, 3)]
    public void Salvage_is_recognised_as_not_mining(int rs, int panels)
    {
        var r = In(ScanContext.Space).Match(rs);
        Assert.Equal(SignalKind.Salvage, r.Best!.Material.Kind);
        Assert.Equal(panels, r.Best.RockCount);
    }

    [Fact]
    public void Bedrock_example_stileron_plus_seven_roc_on_surface()
    {
        var r = In(ScanContext.Surface).Match(31185);
        Assert.True(r.IsMatch, r.Status.ToString());
        var c = r.Best!;
        Assert.True(c.IsMixed);
        Assert.Equal(("Stileron", 1), (c.Material.Name, c.RockCount));
        Assert.Equal(("ROC Deposit", 7), (c.Secondary!.Name, c.SecondaryCount));
    }

    [Fact]
    public void Homogeneous_ore_cluster_beats_equal_mixed_sum_but_alternative_is_listed()
    {
        // 7170 = Gold×2 = Quantainium + ROC deposit (both exact).
        var r = In(ScanContext.Surface).Match(7170);
        Assert.True(r.IsMatch);
        Assert.Equal(("Gold", 2, false), (r.Best!.Material.Name, r.Best.RockCount, r.Best.IsMixed));
        Assert.Contains(r.Candidates, c => c.IsMixed && c.Material.Name == "Quantainium" && c.Secondary!.Name == "ROC Deposit");
    }

    [Fact]
    public void Mixed_clusters_can_be_disabled_and_ore_plus_ore_is_opt_in()
    {
        Assert.Equal(MatchStatus.Unknown, In(ScanContext.Surface, o => o.MixedClusters = MixedClusterMode.Off).Match(31185).Status);

        var crossKind = In(ScanContext.Space).Match(7170);
        Assert.DoesNotContain(crossKind.Candidates, c => c.IsMixed && c.Secondary!.IsOre);

        var all = In(ScanContext.Space, o => o.MixedClusters = MixedClusterMode.All).Match(7170);
        Assert.Contains(all.Candidates, c => c.IsMixed && c.Secondary!.IsOre); // Borase + Bexalite
    }

    [Fact]
    public void Mixed_clusters_require_an_exact_sum_by_default()
    {
        var r = In(ScanContext.Surface).Match(31185);
        Assert.All(r.Candidates.Where(c => c.IsMixed), c => Assert.Equal(0, c.Error));
    }

    [Fact]
    public void Experimental_carinite_is_never_an_answer()
    {
        var r = In(ScanContext.Surface).Match(3000);
        Assert.DoesNotContain(r.Candidates, c => c.Material.Name == "Carinite");
    }

    [Fact]
    public void Every_ore_cluster_stays_correct_or_honestly_ambiguous_in_every_context()
    {
        foreach (var ctx in Enum.GetValues<ScanContext>())
        {
            var matcher = In(ctx);
            foreach (var ore in TestData.Database.Ores)
                for (var n = 1; n <= 6; n++)
                {
                    var r = matcher.Match(ore.BaseRS * n);
                    if (r.IsMatch)
                        Assert.True(ore.Name == r.Best!.Material.Name && n == r.Best.RockCount && !r.Best.IsMixed,
                            $"{ctx}: {ore.Name}×{n} resolved as {r.Best.Label}");
                    else
                    {
                        Assert.Equal(MatchStatus.Ambiguous, r.Status);
                        Assert.Contains(r.Candidates, c => c.Material.Name == ore.Name && c.RockCount == n && !c.IsMixed);
                    }
                }
        }
    }

    [Fact]
    public void Overlay_describes_non_ore_signals()
    {
        var settings = new AppSettings();
        settings.Matcher.Context = ScanContext.Space;
        var session = new ScanSession(new SignatureMatcher(TestData.Database, settings.Matcher), 1, 1);

        var q = OverlayPresenter.Build(session.Observe(4870, DateTime.UtcNow), settings);
        Assert.Equal("Q-TYPE ASTEROID", q.Headline);
        Assert.Equal("1 ASTEROID", q.RocksLine);
        Assert.Contains("COMPOSITION UNKNOWN", q.PriorityLine);

        session.Reset();
        var s = OverlayPresenter.Build(session.Observe(4000 + 2000, DateTime.UtcNow.AddSeconds(1)), settings);
        Assert.Equal(SignalKind.Salvage, s.Signal);
        Assert.Contains("NOT MINING", s.PriorityLine);
    }
}
