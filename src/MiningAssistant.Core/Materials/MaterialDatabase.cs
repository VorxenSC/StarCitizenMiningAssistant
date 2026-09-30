using System.Text.RegularExpressions;

namespace MiningAssistant.Core.Materials;

public sealed class MaterialDatabase
{
    public string GameVersion { get; set; } = "";
    public string Description { get; set; } = "";
    public List<Material> Materials { get; set; } = new();

    /// <summary>Path this database was loaded from (not serialized meaningfully; informational).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? SourcePath { get; set; }

    public IEnumerable<Material> Enabled => Materials.Where(m => m.Enabled && m.BaseRS > 0);

    private static readonly Regex RawSuffix = new(@"\s*(\(\s*)?(RAW|ORE)(\s*\))?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Normalizes a material name as shown by the HUD: "LARANITE RAW" → "LARANITE", "Gold (Ore)" → "Gold".
    /// RAW is just unrefined ore, never a separate RS variant.
    /// </summary>
    public static string NormalizeName(string name)
    {
        var s = name.Trim();
        s = RawSuffix.Replace(s, "");
        return s.Trim();
    }

    /// <summary>Finds a material by name or alias, case-insensitive, ignoring RAW/ORE suffixes.</summary>
    public Material? FindByName(string name)
    {
        var n = NormalizeName(name);
        return Materials.FirstOrDefault(m =>
            string.Equals(m.Name, n, StringComparison.OrdinalIgnoreCase) ||
            m.Aliases.Any(a => string.Equals(a, n, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Returns human-readable problems (duplicate names / RS values, invalid entries).</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        foreach (var m in Materials.Where(m => string.IsNullOrWhiteSpace(m.Name)))
            problems.Add($"Material with baseRS {m.BaseRS} has no name.");
        foreach (var m in Materials.Where(m => m.BaseRS <= 0))
            problems.Add($"{m.Name}: baseRS must be > 0.");
        foreach (var g in Materials.GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add($"Duplicate material name: {g.Key}");
        foreach (var g in Enabled.GroupBy(m => m.BaseRS).Where(g => g.Count() > 1))
            problems.Add($"Duplicate baseRS {g.Key}: {string.Join(", ", g.Select(m => m.Name))}");
        return problems;
    }
}
