using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace MiningAssistant.Cli;

/// <summary>
/// Draws the app icon — the Vorxen logo mark (design/logo-mark-source.png, from the Claude Design canvas)
/// on a dark rounded tile — and writes a multi-size .ico (PNG-compressed entries).
/// Usage: mining-cli make-icon [out.ico]  (also writes a preview PNG next to it)
/// </summary>
internal static class IconGenerator
{
    private static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

    public static void Write(string icoPath)
    {
        var pngs = Sizes.Select(s => (size: s, data: Png(Render(s)))).ToList();

        using (var fs = File.Create(icoPath))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((ushort)0);           // reserved
            w.Write((ushort)1);           // type: icon
            w.Write((ushort)pngs.Count);
            var offset = 6 + 16 * pngs.Count;
            foreach (var (size, data) in pngs)
            {
                w.Write((byte)(size >= 256 ? 0 : size));
                w.Write((byte)(size >= 256 ? 0 : size));
                w.Write((byte)0);         // palette
                w.Write((byte)0);         // reserved
                w.Write((ushort)1);       // planes
                w.Write((ushort)32);      // bpp
                w.Write(data.Length);
                w.Write(offset);
                offset += data.Length;
            }
            foreach (var (_, data) in pngs) w.Write(data);
        }

        // Preview sheet: every size at 1:1 plus the big one.
        using var sheet = new Bitmap(Sizes.Sum() + 12 * Sizes.Length + 12, 256 + 24);
        using (var g = Graphics.FromImage(sheet))
        {
            g.Clear(Color.FromArgb(40, 44, 52));
            var x = 12;
            foreach (var s in Sizes)
            {
                using var bmp = Render(s);
                g.DrawImageUnscaled(bmp, x, 12 + (256 - s));
                x += s + 12;
            }
        }
        sheet.Save(Path.ChangeExtension(icoPath, ".preview.png"), ImageFormat.Png);
    }

    private static byte[] Png(Bitmap bmp)
    {
        using (bmp)
        using (var ms = new MemoryStream())
        {
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
    }

    /// <summary>Logo mark from the Claude Design canvas (transparent PNG), kept in design/.</summary>
    private static Bitmap? _mark;

    private static Bitmap Mark()
    {
        if (_mark is not null) return _mark;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MiningAssistant.sln"))) dir = dir.Parent;
        var file = Path.Combine(dir?.FullName ?? ".", "design", "logo-mark-source.png");
        return _mark = new Bitmap(file);
    }

    /// <summary>Renders the icon at <paramref name="px"/> pixels: the logo mark on a dark rounded tile.</summary>
    public static Bitmap Render(int px)
    {
        var bmp = new Bitmap(px, px, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.CompositingQuality = CompositingQuality.HighQuality;
        var small = px <= 32;

        // Tile: the app's stage color with a subtle steel-blue rim (readable on light and dark taskbars).
        var inset = small ? 0f : px / 32f;
        var tileRect = new RectangleF(inset, inset, px - 2 * inset, px - 2 * inset);
        using (var tile = RoundedRect(tileRect, px * (small ? 0.18f : 0.2f)))
        {
            using var bg = new LinearGradientBrush(new PointF(0, 0), new PointF(0, px), Color.FromArgb(255, 18, 28, 40), Color.FromArgb(255, 8, 13, 20));
            g.FillPath(bg, tile);
            using var rim = new Pen(Color.FromArgb(small ? 150 : 120, 53, 185, 255), Math.Max(1f, px / 48f));
            g.DrawPath(rim, tile);
        }

        // Mark (1037×720): as wide as the tile allows; bigger on tiny sizes, where detail is lost anyway.
        var mark = Mark();
        var w = px * (small ? 0.98f : 0.86f);
        var h = w * mark.Height / mark.Width;
        g.DrawImage(mark, (px - w) / 2, (px - h) / 2 + px * 0.02f, w, h);
        return bmp;
    }
    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
