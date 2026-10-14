using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using MiningAssistant.Core.Roi;

namespace MiningAssistant.Vision.Imaging;

public enum Variant
{
    /// <summary>
    /// Blue channel + contrast stretch. Star Citizen draws the RS digits in white on an amber plate
    /// next to an amber pin icon: in the blue channel icon and plate nearly vanish.
    /// </summary>
    WhiteText,
    /// <summary>
    /// min(R,G) + contrast stretch: amber/yellow and white text stay bright, while the teal/blue
    /// space background (low red) goes dark. For when the RS code is rendered amber in game.
    /// </summary>
    Amber,
    /// <summary>
    /// min(R,G,B) + contrast stretch: only white-ish pixels stay bright. Rescues white digits over
    /// bright colored backgrounds (blue sky, brown terrain) where the blue/warm channels fail.
    /// </summary>
    Whiteness,
    /// <summary>Max-channel grayscale: keeps colored text bright.</summary>
    Grayscale,
    /// <summary>Max-channel grayscale + Otsu threshold.</summary>
    Binarized,

    // ── Variants for digits already isolated by PlateLocator (see ImageOps.PrepareDigits) ──
    /// <summary>Green channel (the un-shifted one) with a raised black point: hides whatever shows through the plate.</summary>
    PlateGreen,
    /// <summary>Green channel, plain contrast stretch.</summary>
    PlateGreenSoft,
    /// <summary>min(R,G,B): white only.</summary>
    PlateWhite,
    /// <summary>max(R,G,B): any bright color (digits rendered amber).</summary>
    PlateBright,
}

/// <summary>
/// Small, allocation-light preprocessing for HUD text:
/// upscale → grayscale → (optional Otsu binarization) → force dark text on light background.
/// </summary>
public static class ImageOps
{
    public static Bitmap Prepare(Bitmap src, int scale, Variant variant)
    {
        scale = Math.Clamp(scale, 1, 6);
        var channel = variant switch
        {
            Variant.WhiteText => Channel.Blue,
            Variant.Amber => Channel.Warm,
            Variant.Whiteness => Channel.Min,
            _ => Channel.Max,
        };
        var stretch = variant is Variant.WhiteText or Variant.Amber or Variant.Whiteness;
        using var trimmed = stretch ? TrimToBrightText(src, channel) : null;
        using var scaled = Scale(trimmed ?? src, scale);
        var gray = ToGrayBytes(scaled, out var w, out var h, channel);

        if (stretch)
        {
            // Soft contrast stretch (no hard threshold: the font's counters are only 1–2 px wide
            // and binarization closes them). Background → black, brightest text → white.
            var lo = Percentile(gray, 0.50);
            var hi = Math.Max(lo + 1, (int)Percentile(gray, 0.995));
            var lut = new byte[256];
            for (var v = 0; v < 256; v++) lut[v] = (byte)Math.Clamp((v - lo) * 255 / (hi - lo), 0, 255);
            for (var i = 0; i < gray.Length; i++) gray[i] = lut[gray[i]];
        }
        else if (variant == Variant.Binarized)
        {
            var t = OtsuThreshold(gray);
            for (var i = 0; i < gray.Length; i++) gray[i] = gray[i] > t ? (byte)255 : (byte)0;
        }

        // HUD text is bright on a dark background; OCR prefers dark on light.
        long sum = 0;
        foreach (var b in gray) sum += b;
        if (sum / (double)gray.Length < 128)
            for (var i = 0; i < gray.Length; i++) gray[i] = (byte)(255 - gray[i]);

        // Windows OCR often misses text touching the image border: add a white margin.
        var pad = Math.Max(8, h / 3);
        return FromGrayBytes(Pad(gray, w, h, pad, 255, out var pw, out var ph), pw, ph);
    }

    /// <summary>
    /// Height the digit crop is scaled to before OCR. Windows OCR reads these digits best at ≈45–60 px;
    /// above ≈70 px it starts returning nothing (measured on a Daymar capture: clean 72 px digits → "").
    /// </summary>
    public const double PlateTargetDigitHeight = 56;

    /// <summary>
    /// Prepares the digits of a located RS plate for OCR. The crop holds only the digits (no icon, no
    /// surroundings), and the scale comes from the icon height, so the OCR input looks the same at
    /// 1080p, 1440p or 4K.
    /// </summary>
    public static Bitmap PrepareDigits(Bitmap search, Plate plate, Variant variant, double zoom = 1.0)
    {
        var channel = variant switch
        {
            Variant.PlateWhite => Channel.Min,
            Variant.PlateBright => Channel.Max,
            _ => Channel.Green,
        };
        var factor = Math.Clamp(PlateTargetDigitHeight * zoom / plate.Digits.Height, 1.5, 10);

        using var crop = Crop(search, plate.Digits);
        using var scaled = new Bitmap((int)Math.Round(crop.Width * factor), (int)Math.Round(crop.Height * factor), PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(crop, new Rectangle(0, 0, scaled.Width, scaled.Height));
        }

        var gray = ToGrayBytes(scaled, out var w, out var h, channel);
        // Plate background (darkened scene) → black, digit cores → white. PlateGreen raises the black
        // point so asteroids / scan dots seen through the semi-transparent plate disappear.
        var lo = (int)Percentile(gray, 0.40);
        var hi = Math.Max(lo + 1, (int)Percentile(gray, 0.99));
        var black = variant == Variant.PlateGreen ? lo + (int)((hi - lo) * 0.35) : lo;
        var lut = new byte[256];
        for (var v = 0; v < 256; v++) lut[v] = (byte)(255 - Math.Clamp((v - black) * 255 / Math.Max(1, hi - black), 0, 255));
        for (var i = 0; i < gray.Length; i++) gray[i] = lut[gray[i]];

        var pad = Math.Max(12, h / 2);
        return FromGrayBytes(Pad(gray, w, h, pad, 255, out var pw, out var ph), pw, ph);
    }

    private static byte[] Pad(byte[] gray, int w, int h, int pad, byte fill, out int pw, out int ph)
    {
        pw = w + 2 * pad;
        ph = h + 2 * pad;
        var dst = new byte[pw * ph];
        Array.Fill(dst, fill);
        for (var y = 0; y < h; y++)
            Array.Copy(gray, y * w, dst, (y + pad) * pw + pad, w);
        return dst;
    }

    /// <summary>
    /// Crops to the bounding box of bright (blue-channel) pixels plus a margin, so a generously drawn
    /// ROI costs no extra OCR time. Returns null when nothing stands out (keep the full ROI).
    /// </summary>
    public static Bitmap? TrimToBrightText(Bitmap src, Channel channel = Channel.Blue, int margin = 6)
    {
        var blue = ToGrayBytes(src, out var w, out var h, channel);
        var lo = Percentile(blue, 0.50);
        var hi = Percentile(blue, 0.995);
        if (hi - lo < 40) return null;
        var t = lo + (hi - lo) / 2;

        int x0 = w, y0 = h, x1 = -1, y1 = -1;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                if (blue[y * w + x] > t)
                {
                    if (x < x0) x0 = x;
                    if (x > x1) x1 = x;
                    if (y < y0) y0 = y;
                    if (y > y1) y1 = y;
                }
        if (x1 < 0) return null;

        var rect = Rectangle.FromLTRB(Math.Max(0, x0 - margin), Math.Max(0, y0 - margin), Math.Min(w, x1 + margin + 1), Math.Min(h, y1 + margin + 1));
        if (rect.Width * rect.Height > w * h * 0.8) return null; // not worth it
        return src.Clone(rect, PixelFormat.Format32bppArgb);
    }

    public static Bitmap Scale(Bitmap src, int scale)
    {
        var dst = new Bitmap(src.Width * scale, src.Height * scale, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(src, new Rectangle(0, 0, dst.Width, dst.Height));
        return dst;
    }

    public static Bitmap Crop(Bitmap src, PixelRect r)
    {
        var rect = Rectangle.Intersect(new Rectangle(r.X, r.Y, r.Width, r.Height), new Rectangle(0, 0, src.Width, src.Height));
        if (rect.Width <= 0 || rect.Height <= 0) throw new ArgumentException("ROI is outside the image.");
        return src.Clone(rect, PixelFormat.Format32bppArgb);
    }

    /// <summary>Cheap change detector: FNV-1a over a sparse pixel sample.</summary>
    public static unsafe ulong SampleHash(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            ulong h = 14695981039346656037UL;
            for (var y = 0; y < data.Height; y += 2)
            {
                var row = (uint*)((byte*)data.Scan0 + y * data.Stride);
                for (var x = 0; x < data.Width; x += 2)
                {
                    h ^= row[x] & 0x00F0F0F0; // ignore alpha and low bits (dithering noise)
                    h *= 1099511628211UL;
                }
            }
            return h ^ (ulong)(data.Width * 31 + data.Height);
        }
        finally { bmp.UnlockBits(data); }
    }

    public enum Channel { Max, Blue, Warm, Min, Green }

    public static unsafe byte[] ToGrayBytes(Bitmap bmp, out int w, out int h, Channel channel = Channel.Max)
    {
        w = bmp.Width; h = bmp.Height;
        var gray = new byte[w * h];
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < h; y++)
            {
                var row = (byte*)data.Scan0 + y * data.Stride;
                for (var x = 0; x < w; x++)
                {
                    var p = row + x * 4; // BGRA
                    // Max channel keeps colored HUD text (cyan/amber) bright instead of averaging it away.
                    // Blue alone: white text stays bright and sharp (the HUD's chromatic aberration shifts
                    // each channel ~1 px), while amber icons/plates have almost no blue.
                    // Warm = min(R,G): amber/yellow/white stay bright, teal/blue space goes dark.
                    gray[y * w + x] = channel switch
                    {
                        Channel.Blue => p[0],
                        Channel.Warm => Math.Min(p[1], p[2]),
                        Channel.Min => Math.Min(p[0], Math.Min(p[1], p[2])),
                        Channel.Green => p[1],
                        _ => Math.Max(p[0], Math.Max(p[1], p[2])),
                    };
                }
            }
        }
        finally { bmp.UnlockBits(data); }
        return gray;
    }

    public static unsafe Bitmap FromGrayBytes(byte[] gray, int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < h; y++)
            {
                var row = (uint*)((byte*)data.Scan0 + y * data.Stride);
                for (var x = 0; x < w; x++)
                {
                    uint v = gray[y * w + x];
                    row[x] = 0xFF000000u | (v << 16) | (v << 8) | v;
                }
            }
        }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }

    /// <summary>
    /// Cheap "is there any HUD text here?" test used to skip OCR on empty frames:
    /// contrast between background (median) and the brightest pixels, and the fraction of bright pixels.
    /// Stars are tiny (&lt;0.2%); the RS plate with digits covers a few percent.
    /// </summary>
    public static (int contrast, double brightFraction) TextStats(Bitmap roi)
    {
        var g = ToGrayBytes(roi, out _, out _, Channel.Max);
        var lo = Percentile(g, 0.50);
        var hi = Percentile(g, 0.995);
        var t = lo + (hi - lo) / 2;
        var bright = 0;
        foreach (var b in g) if (b > t) bright++;
        return (hi - lo, bright / (double)g.Length);
    }

    public static bool HasText(Bitmap roi, int minContrast = 45, double minFraction = 0.003)
    {
        var (c, f) = TextStats(roi);
        return c >= minContrast && f >= minFraction;
    }

    /// <summary>Fraction between background (median) and text peak used by <see cref="Variant.WhiteText"/>.</summary>
    public static double WhiteTextThreshold { get; set; } = 0.55;

    public static byte Percentile(byte[] gray, double p)
    {
        var hist = new int[256];
        foreach (var b in gray) hist[b]++;
        var target = (long)(gray.Length * p);
        long acc = 0;
        for (var i = 0; i < 256; i++)
        {
            acc += hist[i];
            if (acc >= target) return (byte)i;
        }
        return 255;
    }

    public static byte OtsuThreshold(byte[] gray)
    {
        var hist = new int[256];
        foreach (var b in gray) hist[b]++;
        var total = gray.Length;
        double sum = 0;
        for (var i = 0; i < 256; i++) sum += i * (double)hist[i];

        double sumB = 0, best = -1;
        int wB = 0, threshold = 127;
        for (var t = 0; t < 256; t++)
        {
            wB += hist[t];
            if (wB == 0) continue;
            var wF = total - wB;
            if (wF == 0) break;
            sumB += t * (double)hist[t];
            var mB = sumB / wB;
            var mF = (sum - sumB) / wF;
            var between = (double)wB * wF * (mB - mF) * (mB - mF);
            if (between > best) { best = between; threshold = t; }
        }
        return (byte)threshold;
    }

    public static byte[] ToPng(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}
