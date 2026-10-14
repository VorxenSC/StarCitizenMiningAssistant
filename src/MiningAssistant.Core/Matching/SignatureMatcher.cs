using MiningAssistant.Core.Materials;

namespace MiningAssistant.Core.Matching;

public enum MatchStatus
{
    /// <summary>No signal explains the RS within tolerance.</summary>
    Unknown,
    /// <summary>Exactly one dominant explanation with zero error.</summary>
    Exact,
    /// <summary>One dominant explanation, small non-zero error (or OCR-corrected read).</summary>
    Probable,
    /// <summary>Two or more explanations fit about equally well.</summary>
    Ambiguous,
}

/// <summary>Which mixed clusters (A×n + B×m) the resolver may consider.</summary>
public enum MixedClusterMode
{
    /// <summary>Only homogeneous clusters (base × n).</summary>
    Off,
    /// <summary>Mix two different signal kinds, e.g. Stileron + 7 ROC deposits (default).</summary>
    CrossKind,
    /// <summary>Also ore + ore (e.g. Borase + Bexalite). Makes many readings ambiguous.</summary>
    All,
}

public sealed class SignatureMatcherOptions
{
    /// <summary>Largest rock / asteroid count per component and in total (RS = baseRS × count).</summary>
    public int MaxRockCount { get; set; } = 12;

    /// <summary>
    /// Largest count for ground deposits and salvage panels, which come in big groups (a Daymar field
    /// showed RS 51,000 = 17 FPS deposits). Applies to homogeneous clusters only.
    /// </summary>
    public int MaxDepositCount { get; set; } = 40;

    public int MaxCountFor(SignalKind k) =>
        k is SignalKind.FpsDeposit or SignalKind.RocDeposit or SignalKind.Salvage ? Math.Max(MaxRockCount, MaxDepositCount) : MaxRockCount;

    /// <summary>Max |scanned − expected| accepted, in RS units. Ore bases are 15 apart, so keep this below 7.5.</summary>
    public double MaxAbsoluteError { get; set; } = 6;

    /// <summary>Gaussian width for error → fitness. err = 1 → ~97%.</summary>
    public double Sigma { get; set; } = 5.5;

    /// <summary>Ranking penalty per extra item: at equal error, smaller clusters are preferred (they are more common).</summary>
    public double RockCountPenalty { get; set; } = 0.02;

    /// <summary>Best explanation's share of the total score needed to be Exact/Probable instead of Ambiguous.</summary>
    public double DominanceShare { get; set; } = 0.75;

    /// <summary>Confidence multiplier per OCR letter→digit substitution in the reading.</summary>
    public double SubstitutionConfidenceFactor { get; set; } = 0.85;

    /// <summary>Where the player is scanning; rules out signals that cannot appear there.</summary>
    public ScanContext Context { get; set; } = ScanContext.Auto;

    /// <summary>Prior weight of each signal kind (how often it is the right explanation).</summary>
    public double OrePrior { get; set; } = 1.0;
    public double AsteroidTypePrior { get; set; } = 0.8;
    public double RocDepositPrior { get; set; } = 0.8;
    public double FpsDepositPrior { get; set; } = 0.6;
    /// <summary>Low: while mining, salvage panels are rarely the answer when anything else fits (2000 alone still resolves).</summary>
    public double SalvagePrior { get; set; } = 0.15;

    public MixedClusterMode MixedClusters { get; set; } = MixedClusterMode.CrossKind;

    /// <summary>
    /// Prior of any mixed cluster relative to a homogeneous one. Low on purpose: sums create exact
    /// coincidences (7170 = Gold×2 = Quantainium + ROC), and the simpler explanation should win
    /// unless context rules it out; mixed options still appear as alternatives.
    /// </summary>
    public double MixedPrior { get; set; } = 0.1;

    /// <summary>
    /// Tolerance for mixed clusters. The game sums exact integers, and with ~36 signals × counts almost
    /// any number is within ±5 of some A×n + B×m, so mixed explanations must match exactly by default.
    /// </summary>
    public double MixedMaxAbsoluteError { get; set; } = 0;

    /// <summary>RS values outside this range are ignored (noise, other HUD numbers).</summary>
    public int MinPlausibleRS { get; set; } = 1000;
    public int MaxPlausibleRS { get; set; } = 200_000;

    public double PriorOf(SignalKind k) => k switch
    {
        SignalKind.Ore => OrePrior,
        SignalKind.AsteroidType => AsteroidTypePrior,
        SignalKind.RocDeposit => RocDepositPrior,
        SignalKind.FpsDeposit => FpsDepositPrior,
        _ => SalvagePrior,
    };
}

/// <summary>
/// One explanation of a scanned RS: <see cref="Material"/> × <see cref="RockCount"/>, optionally plus
/// <see cref="Secondary"/> × <see cref="SecondaryCount"/> for a mixed cluster. When an ore is involved it is the primary.
/// </summary>
public sealed record Candidate(
    Material Material,
    int RockCount,
    int ExpectedRS,
    int Error,
    /// <summary>How well this candidate alone explains the RS (0..1).</summary>
    double Fitness,
    /// <summary>Displayed confidence (0..1): share among candidates × fitness.</summary>
    double Confidence,
    Material? Secondary = null,
    int SecondaryCount = 0)
{
    public bool IsMixed => Secondary is not null;

    public string Label =>
        IsMixed ? $"{Material.Name} ×{RockCount} + {Secondary!.Name} ×{SecondaryCount}" : $"{Material.Name} ×{RockCount}";

    public string Describe() => $"{Label} (expected {ExpectedRS}, err {Error}) {Confidence:P0}";
}

public sealed record MatchResult(int ScannedRS, MatchStatus Status, IReadOnlyList<Candidate> Candidates)
{
    public Candidate? Best => Candidates.Count > 0 ? Candidates[0] : null;
    public bool IsMatch => Status is MatchStatus.Exact or MatchStatus.Probable;

    public static MatchResult Unknown(int rs) => new(rs, MatchStatus.Unknown, Array.Empty<Candidate>());
}

/// <summary>
/// Signature resolver. Explains a scanned RS as
///   1. base × n for every signal (ores, asteroid types, deposits, salvage), and
///   2. A × n + B × m mixed clusters (see <see cref="MixedClusterMode"/>),
/// filtered by scan context and ranked by fit × prior × cluster size. Close alternatives are
/// reported as Ambiguous instead of silently picking one.
/// </summary>
public sealed class SignatureMatcher
{
    private const int MaxCandidates = 10;
    private readonly IReadOnlyList<Material> _all;
    public SignatureMatcherOptions Options { get; }

    public SignatureMatcher(MaterialDatabase db, SignatureMatcherOptions? options = null)
        : this(db.Enabled, options) { }

    public SignatureMatcher(IEnumerable<Material> materials, SignatureMatcherOptions? options = null)
    {
        _all = materials.Where(m => m.Enabled && !m.Experimental && m.BaseRS > 0).ToList();
        Options = options ?? new SignatureMatcherOptions();
    }

    public bool IsPlausible(int rs) => rs >= Options.MinPlausibleRS && rs <= Options.MaxPlausibleRS;

    /// <summary>
    /// Match a value that required OCR letter→digit substitutions ("358S").
    /// Such a read can never be Exact, and each substitution lowers confidence.
    /// </summary>
    public MatchResult Match(int scannedRS, int ocrSubstitutions, ScanContext? context = null)
    {
        var r = Match(scannedRS, context);
        if (ocrSubstitutions <= 0 || r.Candidates.Count == 0) return r;

        var factor = Math.Pow(Options.SubstitutionConfidenceFactor, ocrSubstitutions);
        var candidates = r.Candidates.Select(c => c with { Confidence = c.Confidence * factor }).ToList();
        var status = r.Status == MatchStatus.Exact ? MatchStatus.Probable : r.Status;
        return new MatchResult(scannedRS, status, candidates);
    }

    public MatchResult Match(int scannedRS) => Match(scannedRS, (ScanContext?)null);

    /// <summary>Resolves in <paramref name="context"/> (defaults to <see cref="SignatureMatcherOptions.Context"/>).</summary>
    public MatchResult Match(int scannedRS, ScanContext? context)
    {
        if (!IsPlausible(scannedRS)) return MatchResult.Unknown(scannedRS);

        var o = Options;
        var ctx = context ?? o.Context;
        var signals = _all.Where(m => m.AppearsIn(ctx)).ToList();
        var raw = new List<(Candidate c, double rank)>();

        // 1. Homogeneous clusters.
        foreach (var m in signals)
        {
            var n = (int)Math.Round(scannedRS / (double)m.BaseRS, MidpointRounding.AwayFromZero);
            if (n < 1 || n > o.MaxCountFor(m.Kind)) continue;
            var expected = m.BaseRS * n;
            var err = Math.Abs(scannedRS - expected);
            if (err > o.MaxAbsoluteError) continue;
            var fitness = Fitness(err);
            raw.Add((new Candidate(m, n, expected, err, fitness, 0), fitness * o.PriorOf(m.Kind) * SizePenalty(n)));
        }

        // 2. Mixed clusters of two different signals.
        if (o.MixedClusters != MixedClusterMode.Off)
        {
            for (var i = 0; i < signals.Count; i++)
            for (var j = i + 1; j < signals.Count; j++)
            {
                Material a = signals[i], b = signals[j];
                if (a.BaseRS == b.BaseRS) continue; // same value: already covered by homogeneous
                if (o.MixedClusters == MixedClusterMode.CrossKind && a.Kind == b.Kind) continue;
                if (!a.IsOre && b.IsOre) (a, b) = (b, a); // ore is always the primary component

                for (var n = 1; n < o.MaxRockCount; n++)
                {
                    var rest = scannedRS - n * a.BaseRS;
                    if (rest < b.BaseRS - o.MixedMaxAbsoluteError) break;
                    var m = (int)Math.Round(rest / (double)b.BaseRS, MidpointRounding.AwayFromZero);
                    if (m < 1 || n + m > o.MaxRockCount) continue;
                    var expected = n * a.BaseRS + m * b.BaseRS;
                    var err = Math.Abs(scannedRS - expected);
                    if (err > o.MixedMaxAbsoluteError) continue;
                    var fitness = Fitness(err);
                    var rank = fitness * o.PriorOf(a.Kind) * o.PriorOf(b.Kind) * o.MixedPrior * SizePenalty(n + m);
                    raw.Add((new Candidate(a, n, expected, err, fitness, 0, b, m), rank));
                }
            }
        }

        if (raw.Count == 0) return MatchResult.Unknown(scannedRS);

        var total = raw.Sum(r => r.rank);
        var ordered = raw.OrderByDescending(r => r.rank).ThenBy(r => r.c.RockCount + r.c.SecondaryCount).ToList();
        var candidates = ordered
            .Take(MaxCandidates)
            .Select(r => r.c with { Confidence = r.rank / total * r.c.Fitness })
            .ToList();

        var best = ordered[0];
        var share = best.rank / total;
        var status = share < o.DominanceShare
            ? MatchStatus.Ambiguous
            : best.c.Error == 0 ? MatchStatus.Exact : MatchStatus.Probable;

        return new MatchResult(scannedRS, status, candidates);
    }

    private double Fitness(int err) => Math.Exp(-Math.Pow(err / Options.Sigma, 2));

    private double SizePenalty(int items) => Math.Max(0.05, 1 - Options.RockCountPenalty * (items - 1));
}
