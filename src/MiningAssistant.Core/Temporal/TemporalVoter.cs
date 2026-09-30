namespace MiningAssistant.Core.Temporal;

public readonly record struct Vote<T>(T? Value, int Votes, int WindowCount) where T : struct
{
    public bool HasValue => Value.HasValue;
}

/// <summary>
/// Majority vote over the last N observations (including "nothing seen" frames).
/// A value becomes stable only after it wins at least <see cref="MinVotes"/> of the window,
/// so single OCR glitches ("358S" read as 3565) never reach the overlay.
/// </summary>
public sealed class TemporalVoter<T> where T : struct, IEquatable<T>
{
    private readonly Queue<(DateTime at, T? value)> _window = new();

    public int WindowSize { get; }
    public int MinVotes { get; }
    public TimeSpan MaxAge { get; }

    public TemporalVoter(int windowSize = 5, int minVotes = 3, TimeSpan? maxAge = null)
    {
        if (windowSize < 1) throw new ArgumentOutOfRangeException(nameof(windowSize));
        WindowSize = windowSize;
        MinVotes = Math.Clamp(minVotes, 1, windowSize);
        MaxAge = maxAge ?? TimeSpan.FromSeconds(5);
    }

    public void Add(T? value, DateTime now)
    {
        _window.Enqueue((now, value));
        while (_window.Count > WindowSize) _window.Dequeue();
        Prune(now);
    }

    public void Clear() => _window.Clear();

    public IReadOnlyList<T?> Snapshot() => _window.Select(w => w.value).ToList();

    public Vote<T> Current(DateTime now)
    {
        var leader = Leader(now);
        return leader.Votes >= MinVotes ? leader : new Vote<T>(null, leader.Votes, leader.WindowCount);
    }

    /// <summary>The most voted value in the window regardless of <see cref="MinVotes"/>.</summary>
    public Vote<T> Leader(DateTime now)
    {
        Prune(now);
        var best = _window
            .Where(w => w.value.HasValue)
            .GroupBy(w => w.value!.Value)
            .Select(g => (value: g.Key, votes: g.Count(), last: g.Max(x => x.at)))
            .OrderByDescending(g => g.votes)
            .ThenByDescending(g => g.last)
            .FirstOrDefault();

        return best.votes > 0
            ? new Vote<T>(best.value, best.votes, _window.Count)
            : new Vote<T>(null, 0, _window.Count);
    }

    private void Prune(DateTime now)
    {
        while (_window.Count > 0 && now - _window.Peek().at > MaxAge) _window.Dequeue();
    }
}
