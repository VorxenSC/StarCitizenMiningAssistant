namespace MiningAssistant.Core.Materials;

public enum MaterialPriority
{
    Common = 0,
    Conditional = 1,
    High = 2,
    VeryHigh = 3,
}

/// <summary>What a radar signature identifies. Only <see cref="Ore"/> names a mineral.</summary>
public enum SignalKind
{
    /// <summary>Ship-mineable rock whose RS identifies the ore (Gold 3585...).</summary>
    Ore,
    /// <summary>Orbital asteroid class (Q-Type 4870...): composition unknown until a deep scan.</summary>
    AsteroidType,
    /// <summary>Hand/FPS mining deposit (3000): mineral depends on location.</summary>
    FpsDeposit,
    /// <summary>ROC/vehicle mining deposit (4000): mineral depends on location.</summary>
    RocDeposit,
    /// <summary>Salvage panel (2000): not mining, listed to avoid false positives.</summary>
    Salvage,
}

/// <summary>Where a signal can appear. Used by the scan context to rule out impossible explanations.</summary>
public enum ScanContext
{
    /// <summary>Consider everything (default until the user tells us where they are).</summary>
    Auto,
    /// <summary>Orbital / asteroid field: no FPS or ROC deposits.</summary>
    Space,
    /// <summary>Planet or moon surface: no asteroid types.</summary>
    Surface,
}

/// <summary>A radar signature entry from materials.json (ore or other signal).</summary>
public sealed class Material
{
    public string Name { get; set; } = "";
    public int BaseRS { get; set; }
    public SignalKind Kind { get; set; } = SignalKind.Ore;
    public MaterialPriority Priority { get; set; } = MaterialPriority.Common;
    public string Category { get; set; } = "";
    public string Notes { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool Notify { get; set; }
    public List<string> Aliases { get; set; } = new();

    /// <summary>Contexts where it can appear; empty = everywhere.</summary>
    public List<ScanContext> Contexts { get; set; } = new();

    /// <summary>
    /// Reserved for location filtering (e.g. "Stanton", "Pyro"); empty = any system.
    /// Not used by the resolver yet.
    /// </summary>
    public List<string> Systems { get; set; } = new();

    /// <summary>Unverified signature: kept for reference, never used to identify anything automatically.</summary>
    public bool Experimental { get; set; }

    public bool IsOre => Kind == SignalKind.Ore;

    public bool AppearsIn(ScanContext context) =>
        context == ScanContext.Auto || Contexts.Count == 0 || Contexts.Contains(context);

    /// <summary>Singular/plural unit shown next to the count ("3 ROCKS", "2 PANELS").</summary>
    public string Unit(int n) => (Kind, n == 1) switch
    {
        (SignalKind.Ore, true) => "ROCK",
        (SignalKind.Ore, false) => "ROCKS",
        (SignalKind.AsteroidType, true) => "ASTEROID",
        (SignalKind.AsteroidType, false) => "ASTEROIDS",
        (SignalKind.Salvage, true) => "PANEL",
        (SignalKind.Salvage, false) => "PANELS",
        (_, true) => "DEPOSIT",
        _ => "DEPOSITS",
    };

    public override string ToString() => $"{Name} ({BaseRS})";
}

public static class MaterialPriorityExtensions
{
    public static string Label(this MaterialPriority p) => p switch
    {
        MaterialPriority.VeryHigh => "VERY HIGH PRIORITY",
        MaterialPriority.High => "HIGH PRIORITY",
        MaterialPriority.Conditional => "CONDITIONAL",
        _ => "COMMON",
    };

    /// <summary>Text icon so priority is never conveyed by color alone.</summary>
    public static string Icon(this MaterialPriority p) => p switch
    {
        MaterialPriority.VeryHigh => "★★",
        MaterialPriority.High => "★",
        MaterialPriority.Conditional => "◆",
        _ => "·",
    };
}
