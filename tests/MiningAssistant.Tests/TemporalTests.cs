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
