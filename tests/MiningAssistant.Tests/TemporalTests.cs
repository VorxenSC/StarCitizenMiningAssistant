using MiningAssistant.Core.Matching;
using MiningAssistant.Core.Temporal;

namespace MiningAssistant.Tests;

public class TemporalTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0);
    private static DateTime At(int frame) => T0.AddMilliseconds(200 * frame);

    [Fact]
    public void Majority_vote_filters_single_glitch()
    {
        var v = new TemporalVoter<int>(5, 3);
        int?[] frames = { 3585, 3585, 3565, 3585, 3585 };
        for (var i = 0; i < frames.Length; i++) v.Add(frames[i], At(i));

        var vote = v.Current(At(4));
        Assert.Equal(3585, vote.Value);
        Assert.Equal(4, vote.Votes);
    }

    [Fact]
    public void No_stable_value_until_min_votes()
    {
        var v = new TemporalVoter<int>(5, 3);
        v.Add(3585, At(0));
        v.Add(3585, At(1));
        Assert.False(v.Current(At(1)).HasValue);
        v.Add(3585, At(2));
        Assert.Equal(3585, v.Current(At(2)).Value);
    }

    [Fact]
    public void Old_observations_expire()
    {
        var v = new TemporalVoter<int>(5, 1, TimeSpan.FromSeconds(1));
        v.Add(3585, At(0));
        Assert.True(v.Current(At(1)).HasValue);
        Assert.False(v.Current(At(10)).HasValue);
    }

    [Fact]
    public void Two_clean_reads_of_a_known_ore_are_enough()
    {
        // OCR flickering between the right value and nothing must still show the ore.
        var s = new ScanSession(TestData.Matcher(), 5, 3);
        s.Observe(15420, At(0));
        s.Observe(null, At(1));
        var st = s.Observe(15420, At(2));
        Assert.Equal(15420, st.StableRS);
        Assert.Equal("Titanium", st.Match!.Best!.Material.Name);
    }

    [Fact]
    public void Unexplained_or_corrected_values_still_need_full_agreement()
    {
        var s = new ScanSession(TestData.Matcher(), 5, 3);
        s.Observe(5001, At(0));
        Assert.Null(s.Observe(5001, At(1)).StableRS);       // unknown value: 2 votes are not enough
        Assert.Equal(5001, s.Observe(5001, At(2)).StableRS); // 3 votes: shown as UNKNOWN

        var t = new ScanSession(TestData.Matcher(), 5, 3);
        t.Observe(3585, At(0), substitutions: 1);
        Assert.Null(t.Observe(3585, At(1), substitutions: 1).StableRS); // OCR-corrected: needs 3
    }

    [Fact]
    public void Session_uses_cleanest_read_of_the_voted_value()
    {
        var s = new ScanSession(TestData.Matcher(), 5, 3);
        s.Observe(3585, At(0), substitutions: 1);
        s.Observe(3585, At(1), substitutions: 1);
        var st = s.Observe(3585, At(2), substitutions: 1);
        Assert.Equal(MatchStatus.Probable, st.Match!.Status);

        st = s.Observe(3585, At(3), substitutions: 0);
        Assert.Equal(MatchStatus.Exact, st.Match!.Status);
    }

    [Fact]
    public void Session_holds_result_briefly_then_clears()
    {
        var s = new ScanSession(TestData.Matcher(), 3, 2, TimeSpan.FromSeconds(2));
        s.Observe(3585, At(0));
        var st = s.Observe(3585, At(1));
        Assert.True(st.IsNew);
        Assert.Equal(MatchStatus.Exact, st.Match!.Status);
        Assert.False(s.Observe(3585, At(2)).IsNew);

        // RS disappears: voter loses majority, result is held...
        s.Observe(null, At(3));
        var held = s.Observe(null, At(4));
        Assert.True(held.IsHeld);
        Assert.Equal(3585, held.StableRS);

        // ...and cleared after the hold time.
        var cleared = s.Observe(null, At(20));
        Assert.Null(cleared.Match);
    }
}
