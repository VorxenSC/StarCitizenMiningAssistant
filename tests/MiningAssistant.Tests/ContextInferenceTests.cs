using MiningAssistant.Core.Matching;
using MiningAssistant.Core.Materials;
using MiningAssistant.Core.Presentation;
using MiningAssistant.Core.Settings;
using MiningAssistant.Core.Temporal;

namespace MiningAssistant.Tests;

public class ContextInferenceTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0);

    /// <summary>Session with 1-frame voting so each Observe is a stable reading.</summary>
    private static ScanSession NewSession(ScanContext configured = ScanContext.Auto, TimeSpan? memory = null) =>
        new(TestData.Matcher(new SignatureMatcherOptions { Context = configured }), 1, 1, TimeSpan.Zero,
            new ContextInference(memory ?? TimeSpan.FromMinutes(10)));

    [Fact]
    public void Rs_4000_is_ambiguous_until_the_location_is_revealed()
    {
        var s = NewSession();
        Assert.Equal(MatchStatus.Ambiguous, s.Observe(4000, T0).Match!.Status);
    }

    [Fact]
    public void Asteroid_type_reveals_space()
    {
        var s = NewSession();
        s.Observe(4870, T0); // Q-Type asteroid: only exists in space
        var st = s.Observe(4000, T0.AddSeconds(5));

        Assert.Equal(ScanContext.Space, st.Context);
        Assert.True(st.ContextInferred);
        Assert.Contains("Q-Type", st.ContextEvidence);
        Assert.Equal("I-Type Asteroid", st.Match!.Best!.Material.Name);
        Assert.True(st.Match.IsMatch);
    }

    [Fact]
    public void Fps_deposit_reveals_surface()
    {
        var s = NewSession();
        s.Observe(3000, T0);
        var st = s.Observe(4000, T0.AddSeconds(5));

        Assert.Equal(ScanContext.Surface, st.Context);
        Assert.Equal("ROC Deposit", st.Match!.Best!.Material.Name);
    }

    [Fact]
    public void Ores_and_salvage_teach_nothing()
    {
        var s = NewSession();
        s.Observe(3585, T0);
        s.Observe(2000, T0.AddSeconds(1));
        Assert.Equal(ScanContext.Auto, s.Observe(4000, T0.AddSeconds(2)).Context);
    }

    [Fact]
    public void Newer_contradicting_signal_takes_over()
    {
        var s = NewSession();
        s.Observe(4870, T0);          // space
        var st = s.Observe(3000, T0.AddMinutes(1)); // FPS deposit: must not be hidden by the old "space"
        Assert.Equal(ScanContext.Surface, st.Context);
        Assert.Equal("FPS Deposit", st.Match!.Best!.Material.Name);
    }

    [Fact]
    public void Memory_expires()
    {
        var s = NewSession(memory: TimeSpan.FromMinutes(10));
        s.Observe(4870, T0);
        Assert.Equal(ScanContext.Auto, s.Observe(4000, T0.AddMinutes(11)).Context);
    }

    [Fact]
    public void Ocr_corrected_reads_do_not_teach()
    {
        var s = NewSession();
        s.Observe(4870, T0, substitutions: 1); // e.g. "487O"
        Assert.Equal(ScanContext.Auto, s.Observe(4000, T0.AddSeconds(1)).Context);
    }

    [Fact]
    public void Manual_context_wins_over_inference()
    {
        var s = NewSession(ScanContext.Surface);
        s.Observe(4870, T0);
        var st = s.Observe(4000, T0.AddSeconds(1));
        Assert.Equal(ScanContext.Surface, st.Context);
        Assert.False(st.ContextInferred);
    }

    [Fact]
    public void Overlay_shows_inferred_context()
    {
        var s = NewSession();
        s.Observe(4870, T0);
        var v = OverlayPresenter.Build(s.Observe(4000, T0.AddSeconds(1)), new AppSettings());
        Assert.Equal("SPACE · auto", v.ContextLabel);
        Assert.Equal("I-TYPE ASTEROID", v.Headline);
    }
}
