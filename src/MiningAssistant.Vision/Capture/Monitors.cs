using System.Runtime.InteropServices;
using MiningAssistant.Core.Roi;

namespace MiningAssistant.Vision.Capture;

/// <summary>A physical monitor in virtual-desktop pixel coordinates (process must be per-monitor DPI aware).</summary>
public sealed record MonitorInfo(int Index, string DeviceName, int Left, int Top, int Width, int Height, bool IsPrimary)
{
    /// <summary>Converts a monitor-relative rect to virtual-desktop coordinates.</summary>
    public PixelRect ToScreen(PixelRect r) => r with { X = r.X + Left, Y = r.Y + Top };

    public override string ToString() => $"#{Index} {Width}×{Height}{(IsPrimary ? " (primary)" : "")} {DeviceName}";
}

public static class Monitors
{
    /// <summary>All monitors, primary first, then left to right.</summary>
    public static IReadOnlyList<MonitorInfo> GetAll()
    {
        var raw = new List<(string dev, Native.RECT rc, bool primary)>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref Native.RECT _, IntPtr _) =>
        {
            var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (Native.GetMonitorInfo(h, ref mi))
                raw.Add((mi.szDevice, mi.rcMonitor, (mi.dwFlags & 1) != 0));
            return true;
        }, IntPtr.Zero);

        return raw
            .OrderByDescending(m => m.primary)
            .ThenBy(m => m.rc.Left)
            .Select((m, i) => new MonitorInfo(i, m.dev, m.rc.Left, m.rc.Top, m.rc.Right - m.rc.Left, m.rc.Bottom - m.rc.Top, m.primary))
            .ToList();
    }

    public static MonitorInfo Get(int index)
    {
        var all = GetAll();
        if (all.Count == 0) throw new InvalidOperationException("No monitors found.");
        return index >= 0 && index < all.Count ? all[index] : all[0];
    }

    /// <summary>Call first thing in console tools so capture coordinates are physical pixels.</summary>
    public static void EnablePerMonitorDpiAwareness()
    {
        try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { /* already set by manifest */ }
    }
}
