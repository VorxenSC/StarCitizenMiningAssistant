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
    bool IsHeld)
{
    public static readonly OverlayView Idle =
        new(OverlayKind.Idle, "", "", "", null, null, null, Array.Empty<string>(), false, false);
}

public static class OverlayPresenter
{
    private static readonly CultureInfo Fmt = CultureInfo.InvariantCulture;

    public static string FormatRs(int rs) => rs.ToString("N0", Fmt);

    public static string FormatRocks(int n) => n == 1 ? "1 ROCK" : $"{n} ROCKS";

    public static OverlayView Build(SessionState state, AppSettings settings)
    {
        if (state.Match is not { } match) return OverlayView.Idle;
        var rsLine = $"RS {FormatRs(match.ScannedRS)}";

        switch (match.Status)
        {
            case MatchStatus.Unknown:
                return new OverlayView(OverlayKind.Unknown, "UNKNOWN", rsLine, "", "Save with Ctrl+Shift+U", null, null,
                    Array.Empty<string>(), false, state.IsHeld);

            case MatchStatus.Ambiguous:
            {
                var alts = match.Candidates.Take(3)
                    .Select(c => $"{c.Material.Name.ToUpperInvariant()} ×{c.RockCount}  {c.Confidence:P0}")
                    .ToList();
                var isTarget = match.Candidates.Take(3).Any(c => IsTarget(c.Material, settings));
                if (settings.TargetOnly && settings.TargetMaterials.Count > 0 && !isTarget)
                    return NotTarget(rsLine, state.IsHeld);
                return new OverlayView(OverlayKind.Ambiguous, "POSSIBLE:", rsLine, "", null, null, null, alts, isTarget, state.IsHeld);
            }

            default:
            {
                var best = match.Best!;
                var isTarget = IsTarget(best.Material, settings);
                if (settings.TargetOnly && settings.TargetMaterials.Count > 0 && !isTarget)
                    return NotTarget(rsLine, state.IsHeld);

                var p = best.Material.Priority;
                var confidence = settings.Overlay.ShowConfidence && match.Status != MatchStatus.Exact
                    ? $"Confidence {best.Confidence:P0}"
                    : null;
                var alts = match.Candidates.Skip(1).Take(2)
                    .Select(c => $"{c.Material.Name.ToUpperInvariant()} ×{c.RockCount}  {c.Confidence:P0}")
                    .ToList();
                return new OverlayView(OverlayKind.Match, best.Material.Name.ToUpperInvariant(), rsLine,
                    FormatRocks(best.RockCount), $"{p.Icon()} {p.Label()}", p, confidence, alts, isTarget, state.IsHeld);
            }
        }
    }

    private static OverlayView NotTarget(string rsLine, bool held) =>
        new(OverlayKind.NotTarget, "NO TARGET", rsLine, "", null, null, null, Array.Empty<string>(), false, held);

    public static bool IsTarget(Material m, AppSettings s) =>
        s.TargetMaterials.Any(t => string.Equals(MaterialDatabase.NormalizeName(t), m.Name, StringComparison.OrdinalIgnoreCase));
}
