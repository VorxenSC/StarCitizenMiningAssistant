using MiningAssistant.Core.Roi;

namespace MiningAssistant.Core.Settings;

/// <summary>
/// Portable layout next to the executable:
///   data/&lt;version&gt;/materials.json   (editable material DB)
///   config/settings.json
///   config/profiles/&lt;name&gt;.roi.json
///   unknown_signatures/               (Save Unknown Signature output)
/// </summary>
public sealed class AppPaths
{
    public string Root { get; }
    public string DataRoot => Path.Combine(Root, "data");
    public string ConfigRoot => Path.Combine(Root, "config");
    public string SettingsFile => Path.Combine(ConfigRoot, "settings.json");
    public string ProfilesRoot => Path.Combine(ConfigRoot, "profiles");
    /// <summary>Debug snapshots taken with the "]" key, one folder per snapshot.</summary>
    public string DebugSnapshotsRoot => Path.Combine(Root, "debug_snapshots");
    public string MissedReadsRoot => Path.Combine(Root, "diagnostics", "missed");
    /// <summary>Signatures the user named in the unknown-signature pop-up.</summary>
    public string LearnedSignaturesFile => Path.Combine(ConfigRoot, "learned_signatures.json");
    public string LearnedImagesRoot => Path.Combine(ConfigRoot, "learned");
    /// <summary>Activation (name + key) for this installation.</summary>
    public string LicenseFile => Path.Combine(ConfigRoot, "license.json");

    public AppPaths(string? root = null) => Root = root ?? AppContext.BaseDirectory;

    public string ProfileFile(string name) => Path.Combine(ProfilesRoot, $"{name}.roi.json");

    public AppSettings LoadSettings() => Json.LoadOrCreate<AppSettings>(SettingsFile);
    public void SaveSettings(AppSettings s) => Json.Save(SettingsFile, s);

    /// <summary>Built-in preset for the Star Citizen ship-scanner RS code (used until the user calibrates).</summary>
    public string PresetProfileFile => Path.Combine(DataRoot, "profiles", "star-citizen-center.roi.json");

    public RoiProfile LoadProfile(string name)
    {
        var path = ProfileFile(name);
        if (File.Exists(path)) return Json.Deserialize<RoiProfile>(File.ReadAllText(path));
        if (File.Exists(PresetProfileFile))
        {
            var preset = Json.Deserialize<RoiProfile>(File.ReadAllText(PresetProfileFile));
            preset.Name = name;
            preset.CalibratedAt = null; // marks it as an uncalibrated preset
            return preset;
        }
        return new RoiProfile { Name = name };
    }

    public void SaveProfile(RoiProfile p) => Json.Save(ProfileFile(p.Name), p);
}
