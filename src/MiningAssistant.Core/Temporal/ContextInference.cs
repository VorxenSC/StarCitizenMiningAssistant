using MiningAssistant.Core.Matching;
using MiningAssistant.Core.Materials;

namespace MiningAssistant.Core.Temporal;

/// <summary>
/// Infers Space vs Surface from what the scanner has shown recently, using only screen readings.
///
/// A reading teaches the context only when it is unmistakable: a stable (voted), exact, clean (no OCR
/// correction), homogeneous match of a signal that exists in a single context — asteroid types
/// (C/S/P/M/Q/E-Type) mean Space, FPS deposits mean Surface. Ores and salvage appear everywhere and
/// teach nothing. Evidence is always evaluated in Auto so the inference never reinforces itself, and a
/// newer contradicting signal immediately takes over. Memory expires after <see cref="Memory"/>.
/// </summary>
public sealed class ContextInference
{
    public TimeSpan Memory { get; }

    public ScanContext? Context { get; private set; }
    public string? Evidence { get; private set; }
    public DateTime LearnedAt { get; private set; }

    public ContextInference(TimeSpan? memory = null) => Memory = memory ?? TimeSpan.FromMinutes(10);

    /// <summary>Feed a match computed in Auto context. Returns true when the inferred context changed.</summary>
    public bool Learn(MatchResult autoMatch, int ocrSubstitutions, DateTime now)
    {
        if (autoMatch.Status != MatchStatus.Exact || ocrSubstitutions > 0) return false;
        if (autoMatch.Best is not { IsMixed: false } best) return false;
        if (best.Material.Contexts.Count != 1 || best.Material.Contexts[0] == ScanContext.Auto) return false;

        var ctx = best.Material.Contexts[0];
        var changed = Context != ctx;
        Context = ctx;
        Evidence = $"{best.Material.Name} (RS {autoMatch.ScannedRS})";
        LearnedAt = now;
        return changed;
    }

    /// <summary>Current inferred context, or null when unknown / expired.</summary>
    public ScanContext? Current(DateTime now)
    {
        if (Context is not null && now - LearnedAt > Memory)
        {
            Context = null;
            Evidence = null;
        }
        return Context;
    }

    public void Reset()
    {
        Context = null;
        Evidence = null;
    }
}
