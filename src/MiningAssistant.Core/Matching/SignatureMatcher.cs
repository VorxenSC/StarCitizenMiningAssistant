using MiningAssistant.Core.Materials;

namespace MiningAssistant.Core.Matching;

public enum MatchStatus
{
    /// <summary>No material explains the RS within tolerance.</summary>
    Unknown,
    /// <summary>Exactly one dominant candidate with zero error.</summary>
    Exact,
    /// <summary>One dominant candidate, small non-zero error.</summary>
    Probable,
    /// <summary>Two or more candidates explain the RS about equally well.</summary>
    Ambiguous,
}

public sealed class SignatureMatcherOptions
{
    /// <summary>Largest cluster size considered (RS = baseRS × rocks).</summary>
    public int MaxRockCount { get; set; } = 12;

    /// <summary>Max |scanned − expected| accepted, in RS units. Base values are 15 apart, so keep this below 7.5.</summary>
    public double MaxAbsoluteError { get; set; } = 6;

    /// <summary>Gaussian width for error → fitness. err = 1 → ~97%.</summary>
    public double Sigma { get; set; } = 5.5;

    /// <summary>Ranking penalty per extra rock: at equal error, smaller clusters are preferred (they are more common).</summary>
    public double RockCountPenalty { get; set; } = 0.02;

    /// <summary>Best candidate's share of the total score needed to be declared Exact/Probable instead of Ambiguous.</summary>
    public double DominanceShare { get; set; } = 0.75;

    /// <summary>Confidence multiplier per OCR letter→digit substitution in the reading.</summary>
    public double SubstitutionConfidenceFactor { get; set; } = 0.85;

    /// <summary>RS values outside this range are ignored (noise, other HUD numbers).</summary>
    public int MinPlausibleRS { get; set; } = 1000;
    public int MaxPlausibleRS { get; set; } = 200_000;
}

public sealed record Candidate(
    Material Material,
    int RockCount,
    int ExpectedRS,
    int Error,
    /// <summary>How well this candidate alone explains the RS (0..1).</summary>
    double Fitness,
    /// <summary>Displayed confidence (0..1): share among candidates × fitness.</summary>
    double Confidence)
{
    public string Describe() =>
        $"{Material.Name} ×{RockCount} (expected {ExpectedRS}, err {Error}) {Confidence:P0}";
}

public sealed record MatchResult(int ScannedRS, MatchStatus Status, IReadOnlyList<Candidate> Candidates)
{
    public Candidate? Best => Candidates.Count > 0 ? Candidates[0] : null;
    public bool IsMatch => Status is MatchStatus.Exact or MatchStatus.Probable;

    public static MatchResult Unknown(int rs) => new(rs, MatchStatus.Unknown, Array.Empty<Candidate>());
}

/// <summary>
/// Explains a scanned RS as baseRS × rockCount for every enabled material and ranks all candidates.
/// Never picks a winner silently when two explanations are close: that is reported as Ambiguous.
/// </summary>
public sealed class SignatureMatcher
{
    private readonly IReadOnlyList<Material> _materials;
    public SignatureMatcherOptions Options { get; }

    public SignatureMatcher(MaterialDatabase db, SignatureMatcherOptions? options = null)
        : this(db.Enabled, options) { }

    public SignatureMatcher(IEnumerable<Material> materials, SignatureMatcherOptions? options = null)
    {
        _materials = materials.Where(m => m.Enabled && m.BaseRS > 0).ToList();
        Options = options ?? new SignatureMatcherOptions();
    }

    public bool IsPlausible(int rs) => rs >= Options.MinPlausibleRS && rs <= Options.MaxPlausibleRS;

    /// <summary>
    /// Match a value that required OCR letter→digit substitutions ("358S").
    /// Such a read can never be Exact, and each substitution lowers confidence.
    /// </summary>
    public MatchResult Match(int scannedRS, int ocrSubstitutions)
    {
        var r = Match(scannedRS);
        if (ocrSubstitutions <= 0 || r.Candidates.Count == 0) return r;

        var factor = Math.Pow(Options.SubstitutionConfidenceFactor, ocrSubstitutions);
        var candidates = r.Candidates.Select(c => c with { Confidence = c.Confidence * factor }).ToList();
        var status = r.Status == MatchStatus.Exact ? MatchStatus.Probable : r.Status;
        return new MatchResult(scannedRS, status, candidates);
    }

    public MatchResult Match(int scannedRS)
    {
        if (!IsPlausible(scannedRS)) return MatchResult.Unknown(scannedRS);

        var raw = new List<(Material m, int n, int expected, int err, double fitness, double rank)>();
        foreach (var m in _materials)
        {
            var n = (int)Math.Round(scannedRS / (double)m.BaseRS, MidpointRounding.AwayFromZero);
            if (n < 1 || n > Options.MaxRockCount) continue;

            var expected = m.BaseRS * n;
            var err = Math.Abs(scannedRS - expected);
            if (err > Options.MaxAbsoluteError) continue;

            var fitness = Math.Exp(-Math.Pow(err / Options.Sigma, 2));
            var rank = fitness * Math.Max(0.05, 1 - Options.RockCountPenalty * (n - 1));
            raw.Add((m, n, expected, err, fitness, rank));
        }

        if (raw.Count == 0) return MatchResult.Unknown(scannedRS);

        var total = raw.Sum(r => r.rank);
        var candidates = raw
            .Select(r => new Candidate(r.m, r.n, r.expected, r.err, r.fitness, r.rank / total * r.fitness))
            .OrderByDescending(c => c.Confidence)
            .ThenBy(c => c.RockCount)
            .ToList();

        var best = raw.OrderByDescending(r => r.rank).First();
        var share = best.rank / total;
        var status = share < Options.DominanceShare
            ? MatchStatus.Ambiguous
            : best.err == 0 ? MatchStatus.Exact : MatchStatus.Probable;

        return new MatchResult(scannedRS, status, candidates);
    }
}
