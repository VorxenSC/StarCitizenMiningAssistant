using System.Globalization;
using MiningAssistant.Core.Matching;
using MiningAssistant.Core.Materials;
using MiningAssistant.Core.Settings;
using MiningAssistant.Core.Temporal;

namespace MiningAssistant.Core.Presentation;

public enum OverlayKind
{
    Idle,
    Match,
    Ambiguous,
    Unknown,
    /// <summary>Target-only filter is on and the detected material is not a target.</summary>
    NotTarget,
}

public sealed record OverlayView(
    OverlayKind Kind,
    string Headline,
    string RsLine,
    string RocksLine,
    string? PriorityLine,
    MaterialPriority? Priority,
    string? ConfidenceLine,
    IReadOnlyList<string> Alternatives,
    bool IsTarget,
    bool IsHeld,
    /// <summary>What the best explanation is (ore, asteroid type, deposit, salvage); null when not a match.</summary>
    SignalKind? Signal = null,
    /// <summary>Extra context line (mixed cluster, "approach and scan", possible minerals...).</summary>
    string? Note = null,
    /// <summary>Scan context indicator: "SPACE · auto" (inferred), "SURFACE" (manual) or null (unknown).</summary>
    string? ContextLabel = null)
{
    public static readonly OverlayView Idle =
        new(OverlayKind.Idle, "", "", "", null, null, null, Array.Empty<string>(), false, false);
}

public static class OverlayPresenter
{
    private static readonly CultureInfo Fmt = CultureInfo.InvariantCulture;

    public static string FormatRs(int rs) => rs.ToString("N0", Fmt);

    public static string FormatRocks(int n) => n == 1 ? "1 ROCK" : $"{n} ROCKS";

    public static string FormatCount(Candidate c) =>
        c.IsMixed
            ? $"{c.RockCount} {c.Material.Unit(c.RockCount)} + {c.SecondaryCount} × {c.Secondary!.Name.ToUpperInvariant()}"
            : $"{c.RockCount} {c.Material.Unit(c.RockCount)}";

    private static string Alt(Candidate c) => $"{c.Label.ToUpperInvariant()}  {c.Confidence:P0}";

    public static string? ContextLabel(SessionState s) => s.Context switch
    {
        ScanContext.Auto => null,
        _ when s.ContextInferred => $"{s.Context.ToString().ToUpperInvariant()} · auto",
        _ => s.Context.ToString().ToUpperInvariant(),
    };

    public static OverlayView Build(SessionState state, AppSettings settings) =>
        BuildCore(state, settings) is var v && v.Kind != OverlayKind.Idle ? v with { ContextLabel = ContextLabel(state) } : v;

    private static OverlayView BuildCore(SessionState state, AppSettings settings)
    {
        if (state.Match is not { } match) return OverlayView.Idle;
        var rsLine = $"RS {FormatRs(match.ScannedRS)}";
        var targetFilter = settings.TargetOnly && settings.TargetMaterials.Count > 0;

        switch (match.Status)
        {
            case MatchStatus.Unknown:
                return new OverlayView(OverlayKind.Unknown, "UNKNOWN", rsLine, "", "Name it in the pop-up window", null, null,
                    Array.Empty<string>(), false, state.IsHeld);

            case MatchStatus.Ambiguous:
            {
                var top = match.Candidates.Take(3).ToList();
                var isTarget = top.Any(c => IsTarget(c, settings));
                if (targetFilter && !isTarget) return NotTarget(rsLine, state.IsHeld);
                var note = state.Context == ScanContext.Auto && top.Select(c => c.Material.Kind).Distinct().Count() > 1
                    ? "Location not detected yet (space/surface)"
                    : null;
                return new OverlayView(OverlayKind.Ambiguous, "POSSIBLE:", rsLine, "", null, null, null,
                    top.Select(Alt).ToList(), isTarget, state.IsHeld, null, note);
            }

            default:
            {
                var best = match.Best!;
                var m = best.Material;
                var isTarget = IsTarget(best, settings);
                if (targetFilter && !isTarget) return NotTarget(rsLine, state.IsHeld);

                var confidence = settings.Overlay.ShowConfidence && match.Status != MatchStatus.Exact
                    ? $"Confidence {best.Confidence:P0}"
                    : null;
                var alts = match.Candidates.Skip(1).Take(2).Select(Alt).ToList();

                var (priorityLine, priority, note) = m.Kind switch
                {
                    SignalKind.Ore => ($"{m.Priority.Icon()} {m.Priority.Label()}", (MaterialPriority?)m.Priority,
                        best.IsMixed ? "MIXED CLUSTER" : null),
                    SignalKind.AsteroidType => ("◌ COMPOSITION UNKNOWN", null, "Approach and scan for composition"),
                    SignalKind.FpsDeposit or SignalKind.RocDeposit => ("◌ MINERAL NOT IDENTIFIED BY RS", null, ShortNote(m.Notes)),
                    _ => ("✕ NOT MINING", null, null),
                };

                return new OverlayView(OverlayKind.Match, m.Name.ToUpperInvariant(), rsLine, FormatCount(best),
                    priorityLine, priority, confidence, alts, isTarget, state.IsHeld, m.Kind, note);
            }
        }
    }

    private static string? ShortNote(string notes) =>
        string.IsNullOrWhiteSpace(notes) ? null : notes.Length <= 60 ? notes : notes[..57] + "...";

    private static OverlayView NotTarget(string rsLine, bool held) =>
        new(OverlayKind.NotTarget, "NO TARGET", rsLine, "", null, null, null, Array.Empty<string>(), false, held);

    public static bool IsTarget(Candidate c, AppSettings s) =>
        IsTarget(c.Material, s) || (c.Secondary is { } sec && IsTarget(sec, s));

    public static bool IsTarget(Material m, AppSettings s) =>
        m.IsOre && s.TargetMaterials.Any(t => string.Equals(MaterialDatabase.NormalizeName(t), m.Name, StringComparison.OrdinalIgnoreCase));
}
