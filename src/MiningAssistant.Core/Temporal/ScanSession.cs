using MiningAssistant.Core.Matching;
using MiningAssistant.Core.Materials;

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
    bool IsNew,
    /// <summary>Context the match was resolved in (configured, or inferred when configured is Auto).</summary>
    ScanContext Context = ScanContext.Auto,
    /// <summary>True when <see cref="Context"/> was inferred from recent signals rather than set by the user.</summary>
    bool ContextInferred = false,
    /// <summary>What the inference is based on, e.g. "Q-Type Asteroid (RS 4870)".</summary>
    string? ContextEvidence = null);

/// <summary>Turns noisy per-frame RS readings into a stable, matched result for the overlay.</summary>
public sealed class ScanSession
{
    private readonly SignatureMatcher _matcher;
    private readonly TemporalVoter<int> _voter;
    private readonly TimeSpan _hold;
    private readonly ContextInference _inference;
    // Fewest OCR substitutions seen for each value still in the window: one clean read is enough.
    private readonly Dictionary<int, int> _minSubs = new();
    private MatchResult? _last;
    private int _lastSubs;
    private ScanContext _lastContext;
    private DateTime _lastSeen;

    public ScanSession(SignatureMatcher matcher, int voteWindow = 5, int minVotes = 3, TimeSpan? hold = null,
        ContextInference? inference = null)
    {
        _matcher = matcher;
        _voter = new TemporalVoter<int>(voteWindow, minVotes);
        _hold = hold ?? TimeSpan.FromSeconds(4);
        _inference = inference ?? new ContextInference();
    }

    public IReadOnlyList<int?> VoteBuffer => _voter.Snapshot();
    public ContextInference Inference => _inference;

    /// <summary>Forgets the current readings (Quick Scan). The learned context is kept.</summary>
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
            var configured = _matcher.Options.Context;

            if (configured == ScanContext.Auto)
            {
                // Evidence is always judged in Auto, so the inference cannot reinforce itself.
                if (isNew || subs != _lastSubs) _inference.Learn(_matcher.Match(stable, subs, ScanContext.Auto), subs, now);
                var ctx = _inference.Current(now) ?? ScanContext.Auto;
                if (isNew || subs != _lastSubs || ctx != _lastContext) _last = _matcher.Match(stable, subs, ctx);
                _lastContext = ctx;
            }
            else
            {
                if (isNew || subs != _lastSubs || _lastContext != configured) _last = _matcher.Match(stable, subs, configured);
                _lastContext = configured;
            }

            _lastSubs = subs;
            _lastSeen = now;
            return State(stable, vote, held: false, isNew);
        }

        if (_last is not null && now - _lastSeen <= _hold)
            return State(_last.ScannedRS, vote, held: true, isNew: false);

        _last = null;
        return State(null, vote, held: false, isNew: false);
    }

    private SessionState State(int? rs, Vote<int> vote, bool held, bool isNew)
    {
        var inferred = _matcher.Options.Context == ScanContext.Auto && _lastContext != ScanContext.Auto;
        return new SessionState(rs, rs is null ? null : _last, vote.Votes, vote.WindowCount, held, isNew,
            _lastContext, inferred, inferred ? _inference.Evidence : null);
    }
}
