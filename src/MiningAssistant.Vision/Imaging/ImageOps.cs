using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using MiningAssistant.Core.Roi;

namespace MiningAssistant.Vision.Imaging;

public enum Variant { Grayscale, Binarized }

/// <summary>
/// Small, allocation-light preprocessing for HUD text:
/// upscale → grayscale → (optional Otsu binarization) → force dark text on light background.
/// </summary>
public static class ImageOps
{
    public static Bitmap Prepare(Bitmap src, int scale, Variant variant)
    {
        scale = Math.Clamp(scale, 1, 6);
        using var scaled = Scale(src, scale);
        var gray = ToGrayBytes(scaled, out var w, out var h);

        if (variant == Variant.Binarized)
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

    public static unsafe byte[] ToGrayBytes(Bitmap bmp, out int w, out int h)
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
                    gray[y * w + x] = Math.Max(p[0], Math.Max(p[1], p[2]));
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
