namespace MiningAssistant.Core.Materials;

public enum MaterialPriority
{
    Common = 0,
    Conditional = 1,
    High = 2,
    VeryHigh = 3,
}

public sealed class Material
{
    public string Name { get; set; } = "";
    public int BaseRS { get; set; }
    public MaterialPriority Priority { get; set; } = MaterialPriority.Common;
    public string Category { get; set; } = "";
    public string Notes { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool Notify { get; set; }
    public List<string> Aliases { get; set; } = new();

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
