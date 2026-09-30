namespace MiningAssistant.Core.Materials;

/// <summary>
/// Loads per-patch databases from a data root:
///   data/default/materials.json
///   data/4.10.1/materials.json
/// Falls back to "default" when the requested version folder does not exist.
/// </summary>
public static class MaterialDatabaseLoader
{
    public const string DefaultVersion = "default";
    public const string FileName = "materials.json";

    public static MaterialDatabase Load(string path)
    {
        var db = Json.Deserialize<MaterialDatabase>(File.ReadAllText(path));
        db.SourcePath = Path.GetFullPath(path);
        return db;
    }

    public static string ResolvePath(string dataRoot, string? gameVersion)
    {
        if (!string.IsNullOrWhiteSpace(gameVersion))
        {
            var versioned = Path.Combine(dataRoot, gameVersion, FileName);
            if (File.Exists(versioned)) return versioned;
        }
        var fallback = Path.Combine(dataRoot, DefaultVersion, FileName);
        if (File.Exists(fallback)) return fallback;
        throw new FileNotFoundException($"No {FileName} found for version '{gameVersion}' or '{DefaultVersion}' under {dataRoot}");
    }

    public static MaterialDatabase LoadForVersion(string dataRoot, string? gameVersion) =>
        Load(ResolvePath(dataRoot, gameVersion));

    /// <summary>Lists version folders that contain a materials.json.</summary>
    public static IReadOnlyList<string> ListVersions(string dataRoot)
    {
        if (!Directory.Exists(dataRoot)) return Array.Empty<string>();
        return Directory.GetDirectories(dataRoot)
            .Where(d => File.Exists(Path.Combine(d, FileName)))
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderBy(v => v == DefaultVersion ? "" : v, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
