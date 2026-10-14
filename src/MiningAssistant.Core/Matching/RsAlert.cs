using System.Globalization;

namespace MiningAssistant.Core.Matching;

/// <summary>
/// "Beep when the RS is …": user-chosen signature numbers that trigger a sound, optionally including
/// their multiples (3585 → also 7170, 10755 … = clusters of 2, 3 … rocks).
/// </summary>
public static class RsAlert
{
    /// <summary>The alert value that <paramref name="rs"/> hits, or null.</summary>
    public static int? Match(int rs, IEnumerable<int> values, bool includeMultiples, double tolerance, int maxCount = 40)
    {
        foreach (var v in values.Where(v => v > 0))
        {
            if (Math.Abs(rs - v) <= tolerance) return v;
            if (!includeMultiples) continue;
            var n = (int)Math.Round(rs / (double)v, MidpointRounding.AwayFromZero);
            if (n >= 2 && n <= maxCount && Math.Abs(rs - n * v) <= tolerance) return v;
        }
        return null;
    }

    /// <summary>Parses "3585, 7170 3870;4300" (also "3,585" style digits) into distinct values ≥ 1000.</summary>
    public static List<int> Parse(string text)
    {
        var result = new List<int>();
        foreach (var raw in text.Split(new[] { ';', ' ', '\t', '\n', '\r', '|' }, StringSplitOptions.RemoveEmptyEntries))
        foreach (var part in SplitCommas(raw))
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 1000 && !result.Contains(v))
                result.Add(v);
        return result;
    }

    /// <summary>"3,585" is one number (thousands separator); "3585,7170" is two.</summary>
    private static IEnumerable<string> SplitCommas(string token)
    {
        var pieces = token.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var groupedThousands = pieces.Length > 1 && pieces[0].Length <= 3 && pieces.Skip(1).All(p => p.Length == 3);
        return groupedThousands ? new[] { string.Concat(pieces) } : pieces;
    }

    public static string Format(IEnumerable<int> values) => string.Join(", ", values);
}
