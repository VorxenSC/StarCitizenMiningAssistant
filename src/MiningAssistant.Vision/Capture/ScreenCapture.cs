using System.Drawing;
using System.Drawing.Imaging;
using MiningAssistant.Core.Roi;

namespace MiningAssistant.Vision.Capture;

public interface IScreenCapture
{
    /// <summary>Captures a rectangle in virtual-desktop pixel coordinates.</summary>
    Bitmap Capture(PixelRect screenRect);
}

/// <summary>
/// GDI BitBlt of just the requested region. Reads the composed desktop exactly like a screenshot:
/// works with Borderless / Fullscreen Windowed, never opens or touches the game process.
/// Capturing a few hundred pixels costs well under a millisecond.
/// </summary>
public sealed class GdiScreenCapture : IScreenCapture
{
    public Bitmap Capture(PixelRect r)
    {
        if (r.IsEmpty) throw new ArgumentException("Empty capture rectangle.", nameof(r));
        var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(r.X, r.Y, 0, 0, new Size(r.Width, r.Height), CopyPixelOperation.SourceCopy);
        return bmp;
    }
}
