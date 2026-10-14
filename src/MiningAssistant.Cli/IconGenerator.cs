using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace MiningAssistant.Cli;

/// <summary>
/// Draws the app icon — an amber faceted ore crystal (the color of the in-game RS plate) with cyan
/// scanner arcs on a dark rounded tile — and writes a multi-size .ico (PNG-compressed entries).
/// Each size is drawn separately so 16–32 px stay crisp: small sizes drop the arcs and fine edges.
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

    /// <summary>Renders the icon at <paramref name="px"/> pixels; geometry is designed on a 256 grid.</summary>
    public static Bitmap Render(int px)
    {
        var bmp = new Bitmap(px, px, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        var k = px / 256f;
        g.ScaleTransform(k, k);
        var small = px <= 32;

        // Tile: dark rounded square with an amber rim.
        var inset = small ? 4f : 8f;
        using (var tile = RoundedRect(new RectangleF(inset, inset, 256 - 2 * inset, 256 - 2 * inset), small ? 44 : 52))
        {
            using var bg = new LinearGradientBrush(new PointF(0, 0), new PointF(0, 256), Color.FromArgb(255, 22, 32, 52), Color.FromArgb(255, 7, 11, 20));
            g.FillPath(bg, tile);
            using var rim = new Pen(Color.FromArgb(small ? 255 : 230, 245, 158, 11), small ? 14f : 7f);
            g.DrawPath(rim, tile);
        }

        // Scanner arcs (radar ping) behind the crystal, top-right — only where there is room for them.
        if (!small)
        {
            var c = new PointF(172, 84);
            foreach (var (r, a) in new[] { (22f, 255), (38f, 200), (54f, 140) })
            {
                using var arc = new Pen(Color.FromArgb(a, 34, 211, 238), px >= 64 ? 9f : 12f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(arc, c.X - r, c.Y - r, 2 * r, 2 * r, -78, 66);
            }
        }

        // Ore crystal: hexagonal gem with three faces (light top, mid right, dark left).
        var s = small ? 1.18f : 1f;           // bigger crystal on small icons
        var ox = small ? -22f : -12f;         // keep it centered after scaling
        var oy = small ? -16f : 6f;
        PointF P(float x, float y) => new(x * s + ox, y * s + oy);

        var top = P(124, 46); var ur = P(182, 92); var lr = P(170, 186); var bottom = P(124, 214);
        var ll = P(78, 186); var ul = P(66, 92); var mid = P(124, 116);

        void Face(PointF[] pts, Color from, Color to)
        {
            var minY = pts.Min(p => p.Y); var maxY = pts.Max(p => p.Y);
            using var b = new LinearGradientBrush(new PointF(0, minY - 1), new PointF(0, maxY + 1), from, to);
            g.FillPolygon(b, pts);
        }

        Face(new[] { top, ur, mid, ul }, Color.FromArgb(255, 254, 240, 138), Color.FromArgb(255, 250, 204, 21)); // top
        Face(new[] { ur, lr, bottom, mid }, Color.FromArgb(255, 245, 158, 11), Color.FromArgb(255, 217, 119, 6)); // right
        Face(new[] { ul, mid, bottom, ll }, Color.FromArgb(255, 217, 119, 6), Color.FromArgb(255, 146, 64, 14));  // left

        using (var edge = new Pen(Color.FromArgb(255, 69, 26, 3), small ? 9f : 5f) { LineJoin = LineJoin.Round })
            g.DrawPolygon(edge, new[] { top, ur, lr, bottom, ll, ul });

        if (!small)
        {
            using var facet = new Pen(Color.FromArgb(150, 120, 53, 15), 3.5f) { LineJoin = LineJoin.Round };
            g.DrawLines(facet, new[] { ul, mid, ur });
            g.DrawLine(facet, mid, bottom);
            // glint on the top face
            using var glint = new SolidBrush(Color.FromArgb(220, 255, 255, 255));
            g.FillPolygon(glint, new[] { P(108, 66), P(124, 58), P(132, 70), P(114, 80) });
        }

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
