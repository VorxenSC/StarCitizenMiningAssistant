namespace MiningAssistant.Core.Roi;

/// <summary>Well-known region names. Only <see cref="RS"/> is used by the MVP (Quick Scan).</summary>
public static class RoiNames
{
    public const string RS = "rs";
    public const string ScanDetails = "scanDetails";
    public const string Mass = "mass";
    public const string Resistance = "resistance";
    public const string Instability = "instability";
    public const string Composition = "composition";

    public static readonly IReadOnlyList<(string Key, string Label)> All = new[]
    {
        (RS, "RS Signature"),
        (ScanDetails, "Scan Details"),
        (Mass, "Mass"),
        (Resistance, "Resistance"),
        (Instability, "Instability"),
        (Composition, "Composition"),
    };
}

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>A rectangle expressed as fractions (0..1) of the monitor, so it survives resolution changes.</summary>
public sealed class NormalizedRect
{
    public double XPercent { get; set; }
    public double YPercent { get; set; }
    public double WidthPercent { get; set; }
    public double HeightPercent { get; set; }

    public NormalizedRect() { }

    public NormalizedRect(double x, double y, double w, double h)
    {
        XPercent = x; YPercent = y; WidthPercent = w; HeightPercent = h;
    }

    public bool IsValid => WidthPercent > 0 && HeightPercent > 0;

    public static NormalizedRect FromPixels(PixelRect r, int surfaceWidth, int surfaceHeight) =>
        new(r.X / (double)surfaceWidth, r.Y / (double)surfaceHeight, r.Width / (double)surfaceWidth, r.Height / (double)surfaceHeight);

    /// <summary>Converts to pixels on a surface, clamped to its bounds.</summary>
    public PixelRect ToPixels(int surfaceWidth, int surfaceHeight)
    {
        var x = (int)Math.Round(Math.Clamp(XPercent, 0, 1) * surfaceWidth);
        var y = (int)Math.Round(Math.Clamp(YPercent, 0, 1) * surfaceHeight);
        var w = (int)Math.Round(Math.Clamp(WidthPercent, 0, 1) * surfaceWidth);
        var h = (int)Math.Round(Math.Clamp(HeightPercent, 0, 1) * surfaceHeight);
        w = Math.Min(w, surfaceWidth - x);
        h = Math.Min(h, surfaceHeight - y);
        return new PixelRect(x, y, Math.Max(0, w), Math.Max(0, h));
    }

    public override string ToString() => $"x={XPercent:P2} y={YPercent:P2} w={WidthPercent:P2} h={HeightPercent:P2}";
}

public sealed class RoiProfile
{
    public string Name { get; set; } = "default";
    /// <summary>Resolution at calibration time (informational; regions are normalized).</summary>
    public int CalibratedWidth { get; set; }
    public int CalibratedHeight { get; set; }
    public DateTime? CalibratedAt { get; set; }
    public Dictionary<string, NormalizedRect> Regions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public NormalizedRect? Get(string name) =>
        Regions.TryGetValue(name, out var r) && r.IsValid ? r : null;

    public bool HasRegion(string name) => Get(name) is not null;
}
