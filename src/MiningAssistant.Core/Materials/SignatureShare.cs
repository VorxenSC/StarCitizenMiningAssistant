using System.Globalization;
using System.Text;

namespace MiningAssistant.Core.Materials;

/// <summary>
/// Turns signatures the user named into something they can share by hand: a text block to paste in
/// Discord, or a link that opens a pre-filled GitHub issue in their browser. Pure (no I/O): the app
/// itself never opens a network connection — the user decides where the text goes.
/// Only the values the user typed are included (RS, name, count, type, notes, versions, date);
/// plate images stay on the PC and are attached by the user if they want.
/// </summary>
public static class SignatureShare
{
    /// <summary>GitHub accepts long URLs, but browsers and proxies get unreliable past ~8 KB.</summary>
    public const int MaxIssueUrlLength = 7500;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string Rs(int v) => v.ToString("N0", Inv);

    private static string Line(LearnedSignature s) =>
        $"RS {Rs(s.ObservedRS)} = {s.Name} ×{s.Count} (base {Rs(s.BaseRS)}, {s.Kind})" +
        (string.IsNullOrWhiteSpace(s.Notes) ? "" : $" — {s.Notes.Trim()}");

    public static string Title(IReadOnlyList<LearnedSignature> items) =>
        items.Count == 1 ? $"New signature: {Line(items[0])}" : $"{items.Count} new signatures";

    /// <summary>Plain text for Discord (or any chat).</summary>
    public static string DiscordText(IReadOnlyList<LearnedSignature> items, string appVersion)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**Mining Assistant — {(items.Count == 1 ? "new signature" : $"{items.Count} new signatures")}** (app v{appVersion})");
        foreach (var s in items)
            sb.AppendLine($"• {Line(s)} · game {Version(s)} · {s.LearnedAt:yyyy-MM-dd}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Markdown body for the GitHub issue: a table the maintainer can copy into materials.json.</summary>
    public static string IssueBody(IReadOnlyList<LearnedSignature> items, string appVersion)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Signatures named in Mining Assistant v{appVersion}.");
        sb.AppendLine();
        sb.AppendLine("| RS seen | Name | Count | Base RS | Type | Game version | Date | Notes |");
        sb.AppendLine("|---:|---|---:|---:|---|---|---|---|");
        foreach (var s in items)
            sb.AppendLine($"| {Rs(s.ObservedRS)} | {Cell(s.Name)} | {s.Count} | {Rs(s.BaseRS)} | {s.Kind} | {Cell(Version(s))} | {s.LearnedAt:yyyy-MM-dd} | {Cell(s.Notes)} |");
        sb.AppendLine();
        sb.AppendLine("_Plate image(s), optional: drag them here from the folder the app opened (`config\\learned`)._");
        return sb.ToString();
    }

    /// <summary>
    /// "https://github.com/{repo}/issues/new?title=…&amp;body=…". When the list is too long for one URL,
    /// only the first signatures that fit are included; <paramref name="included"/> says how many.
    /// </summary>
    public static string IssueUrl(string repository, IReadOnlyList<LearnedSignature> items, string appVersion, out int included)
    {
        var baseUrl = $"https://github.com/{repository}/issues/new";
        for (included = items.Count; included > 1; included--)
        {
            var url = Build(baseUrl, items.Take(included).ToList(), appVersion);
            if (url.Length <= MaxIssueUrlLength) return url;
        }
        return Build(baseUrl, items.Take(Math.Max(1, included)).ToList(), appVersion);
    }

    private static string Build(string baseUrl, IReadOnlyList<LearnedSignature> items, string appVersion) =>
        $"{baseUrl}?title={Uri.EscapeDataString(Title(items))}&body={Uri.EscapeDataString(IssueBody(items, appVersion))}";

    private static string Version(LearnedSignature s) => string.IsNullOrWhiteSpace(s.GameVersion) ? "?" : s.GameVersion;

    private static string Cell(string text) => (text ?? "").Replace("|", "/").Replace("\r", " ").Replace("\n", " ").Trim();
}
