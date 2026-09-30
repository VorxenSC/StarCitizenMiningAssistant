using MiningAssistant.Core.Matching;

namespace MiningAssistant.Core.Temporal;

public sealed record SessionState(
    /// <summary>Stable (voted) RS, or null.</summary>
    int? StableRS,
    MatchResult? Match,
    int Votes,
    int WindowCount,
    /// <summary>True when the RS is no longer visible but the last result is kept on screen for a few seconds.</summary>
    bool IsHeld,
    /// <summary>True only on the frame where a new stable RS first appears (for sound alerts / logging).</summary>
    bool IsNew);

/// <summary>Turns noisy per-frame RS readings into a stable, matched result for the overlay.</summary>
public sealed class ScanSession
{
    private readonly SignatureMatcher _matcher;
    private readonly TemporalVoter<int> _voter;
    private readonly TimeSpan _hold;
    // Fewest OCR substitutions seen for each value still in the window: one clean read is enough.
    private readonly Dictionary<int, int> _minSubs = new();
    private MatchResult? _last;
    private int _lastSubs;
    private DateTime _lastSeen;

    public ScanSession(SignatureMatcher matcher, int voteWindow = 5, int minVotes = 3, TimeSpan? hold = null)
    {
        _matcher = matcher;
        _voter = new TemporalVoter<int>(voteWindow, minVotes);
        _hold = hold ?? TimeSpan.FromSeconds(4);
    }

    public IReadOnlyList<int?> VoteBuffer => _voter.Snapshot();

    public void Reset()
    {
        _voter.Clear();
        _minSubs.Clear();
        _last = null;
    }

    public SessionState Observe(int? rs, DateTime now, int substitutions = 0)
    {
        _voter.Add(rs, now);
        if (rs is int v)
            _minSubs[v] = _minSubs.TryGetValue(v, out var s) ? Math.Min(s, substitutions) : substitutions;
        var inWindow = _voter.Snapshot().OfType<int>().ToHashSet();
        foreach (var k in _minSubs.Keys.Where(k => !inWindow.Contains(k)).ToList()) _minSubs.Remove(k);

        var vote = _voter.Current(now);
        if (vote.Value is int stable)
        {
            var subs = _minSubs.GetValueOrDefault(stable);
            var isNew = _last?.ScannedRS != stable;
            if (isNew || subs != _lastSubs) _last = _matcher.Match(stable, subs);
            _lastSubs = subs;
            _lastSeen = now;
            return new SessionState(stable, _last, vote.Votes, vote.WindowCount, IsHeld: false, IsNew: isNew);
        }

        if (_last is not null && now - _lastSeen <= _hold)
            return new SessionState(_last.ScannedRS, _last, vote.Votes, vote.WindowCount, IsHeld: true, IsNew: false);

        _last = null;
        return new SessionState(null, null, vote.Votes, vote.WindowCount, IsHeld: false, IsNew: false);
    }
}
