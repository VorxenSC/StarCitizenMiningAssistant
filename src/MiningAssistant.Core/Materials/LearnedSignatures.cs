namespace MiningAssistant.Core.Materials;

/// <summary>
/// A signature the user named in the "unknown signature" pop-up: what an unexplained RS turned out to be.
/// Kept in config/learned_signatures.json (separate from the shipped materials.json, so app updates
/// never overwrite it) and merged into the database, so the app recognises it right away.
/// </summary>
public sealed class LearnedSignature
{
    public string Name { get; set; } = "";
    public SignalKind Kind { get; set; } = SignalKind.Ore;
    /// <summary>The RS that was on screen.</summary>
    public int ObservedRS { get; set; }
    /// <summary>How many rocks / deposits the user said it was (RS = BaseRS × Count).</summary>
    public int Count { get; set; } = 1;
    public int BaseRS { get; set; }
    public string Notes { get; set; } = "";
    public string GameVersion { get; set; } = "";
    public DateTime LearnedAt { get; set; }
    /// <summary>Sent to the GitHub repository (contributions/).</summary>
    public bool Submitted { get; set; }
    public string? SubmittedPath { get; set; }
    /// <summary>Local file of the plate crop shown in the pop-up (sent along with the values).</summary>
    public string? PlateImage { get; set; }

    public static int ComputeBase(int observedRs, int count) =>
        (int)Math.Round(observedRs / (double)Math.Max(1, count), MidpointRounding.AwayFromZero);
}

public sealed class LearnedSignatureFile
{
    public List<LearnedSignature> Signatures { get; set; } = new();
}

public sealed class LearnedSignatureStore
{
    private readonly string _path;
    public LearnedSignatureFile Data { get; private set; } = new();

    public LearnedSignatureStore(string path)
    {
        _path = path;
        Reload();
    }

    public IReadOnlyList<LearnedSignature> All => Data.Signatures;
    public IEnumerable<LearnedSignature> Pending => Data.Signatures.Where(s => !s.Submitted);

    public void Reload() =>
        Data = File.Exists(_path) ? Json.Deserialize<LearnedSignatureFile>(File.ReadAllText(_path)) : new LearnedSignatureFile();

    public void Save() => Json.Save(_path, Data);

    public LearnedSignature Add(LearnedSignature s)
    {
        if (s.BaseRS <= 0) s.BaseRS = LearnedSignature.ComputeBase(s.ObservedRS, s.Count);
        if (s.LearnedAt == default) s.LearnedAt = DateTime.Now;
        Data.Signatures.Add(s);
        Save();
        return s;
    }

    /// <summary>
    /// Adds the learned signatures to <paramref name="db"/> as extra entries. A name that already exists
    /// (e.g. a known ore with a new value after a patch) keeps that ore's priority and alert setting.
    /// </summary>
    public void MergeInto(MaterialDatabase db)
    {
        foreach (var s in Data.Signatures.Where(s => s.BaseRS > 0 && !string.IsNullOrWhiteSpace(s.Name)))
        {
            if (db.Materials.Any(m => m.BaseRS == s.BaseRS && m.Kind == s.Kind)) continue; // already known
            var known = db.FindByName(s.Name);
            db.Materials.Add(new Material
            {
                Name = known?.Name ?? s.Name.Trim(),
                BaseRS = s.BaseRS,
                Kind = s.Kind,
                Priority = known?.Priority ?? MaterialPriority.Common,
                Notify = known?.Notify ?? false,
                Category = "Learned",
                Notes = $"Learned {s.LearnedAt:yyyy-MM-dd} from RS {s.ObservedRS} (×{s.Count}). {s.Notes}".Trim(),
                Contexts = s.Kind switch
                {
                    SignalKind.AsteroidType => new() { ScanContext.Space },
                    SignalKind.FpsDeposit or SignalKind.RocDeposit => new() { ScanContext.Surface },
                    _ => new(),
                },
            });
        }
    }
}
