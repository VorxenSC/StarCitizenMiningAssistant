using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using MiningAssistant.Core.Roi;

namespace MiningAssistant.Vision.Imaging;

public sealed class PlateLocatorOptions
{
    /// <summary>Accepted pin-icon height in pixels (≈23 px at 1440p, ≈17 at 1080p, ≈35 at 2160p).</summary>
    public int MinIconHeight { get; set; } = 9;
    public int MaxIconHeight { get; set; } = 72;

    /// <summary>Icon pixels: max(R,G) at least this bright…</summary>
    public int IconMinBrightness { get; set; } = 120;
    /// <summary>…with blue at most this fraction of max(R,G)…</summary>
    public double IconMaxBlueRatio { get; set; } = 0.55;
    /// <summary>…and max(R,G) − B at least this (amber / yellow, including its red and green fringes).</summary>
    public int IconMinChroma { get; set; } = 60;

    /// <summary>
    /// Height in pixels of the screen the image comes from (0 = unknown). The HUD scales with it:
    /// the icon is ≈1.6% of the screen height, so sizes outside 1.1%–2.6% are ignored.
    /// </summary>
    public int ScreenHeight { get; set; }

    /// <summary>Minimum contrast (green channel) between plate background and digits.</summary>
    public int MinDigitContrast { get; set; } = 70;

    /// <summary>Diagnostics: receives one line per icon-shaped blob explaining why it was accepted or rejected.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Action<string>? Trace { get; set; }
}

/// <summary>An RS indicator found on screen: the amber pin icon and the digits to its right.</summary>
public sealed record Plate(PixelRect Icon, PixelRect Digits, double Score)
{
    public int IconHeight => Icon.Height;

    /// <summary>Icon + digits, for drawing / diagnostics.</summary>
    public PixelRect Bounds
    {
        get
        {
            var x0 = Math.Min(Icon.X, Digits.X);
            var y0 = Math.Min(Icon.Y, Digits.Y);
            var x1 = Math.Max(Icon.X + Icon.Width, Digits.X + Digits.Width);
            var y1 = Math.Max(Icon.Y + Icon.Height, Digits.Y + Digits.Height);
            return new PixelRect(x0, y0, x1 - x0, y1 - y0);
        }
    }
}

/// <summary>
/// Finds Star Citizen's RS indicator — the amber plate with a pin icon followed by white digits —
/// anywhere inside a search image, so reading does not depend on a fixed screen position, on the
/// resolution, or on what is behind the HUD.
///
/// 1. Mask "amber" pixels (bright, low blue). The HUD's chromatic aberration splits the icon into
///    yellow core + red/green fringes; all of them have low blue.
/// 2. Connected components → blobs with the pin icon's size and aspect.
/// 3. For each blob, look right of it within the icon's rows for a run of bright white digits
///    (green channel, which is the un-shifted one), of the expected relative height.
/// All geometry is relative to the icon height, so it scales with the resolution.
/// </summary>
public static class PlateLocator
{
    public static IReadOnlyList<Plate> Find(Bitmap bmp, PlateLocatorOptions? options = null)
    {
        var o = options ?? new PlateLocatorOptions();
        var px = ToBgra(bmp, out var w, out var h);
        if (w < 24 || h < 12) return Array.Empty<Plate>();

        int minIcon = o.MinIconHeight, maxIcon = o.MaxIconHeight;
        if (o.ScreenHeight > 0)
        {
            minIcon = Math.Max(minIcon, (int)(0.011 * o.ScreenHeight));
            maxIcon = Math.Min(maxIcon, (int)Math.Ceiling(0.026 * o.ScreenHeight));
        }

        // 1. amber mask
        var mask = new bool[w * h];
        for (int i = 0, p = 0; i < mask.Length; i++, p += 4)
        {
            int b = px[p], g = px[p + 1], r = px[p + 2];
            var m = r > g ? r : g;
            mask[i] = m >= o.IconMinBrightness && b <= o.IconMaxBlueRatio * m && m - b >= o.IconMinChroma;
        }

        // 2. connected components on the mask dilated by 1 px (joins the pin to its base)
        var dil = Dilate(mask, w, h);
        var seen = new bool[w * h];
        var stack = new Stack<int>();
        var plates = new List<Plate>();

        for (var start = 0; start < dil.Length; start++)
        {
            if (!dil[start] || seen[start]) continue;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, count = 0, area = 0;
            stack.Push(start);
            seen[start] = true;
            while (stack.Count > 0)
            {
                var i = stack.Pop();
                int x = i % w, y = i / w;
                area++;
                if (mask[i]) count++;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    var n = ny * w + nx;
                    if (dil[n] && !seen[n]) { seen[n] = true; stack.Push(n); }
                }
            }

            // undo the dilation on the bbox
            minX++; minY++; maxX--; maxY--;
            int bw = maxX - minX + 1, bh = maxY - minY + 1;
            if (bh < minIcon || bh > maxIcon) continue;
            // An icon touching the border is cut off: its digits would be read truncated.
            if (minX <= 0 || minY <= 0 || maxX >= w - 1 || maxY >= h - 1) continue;
            var aspect = bw / (double)bh; // the pin is ≈ 20×23 (0.83–0.91 measured)
            if (aspect is < 0.72 or > 1.05) continue;
            var fill = count / (double)(bw * bh);
            if (fill is < 0.30 or > 0.65) continue; // measured 0.40–0.46
            if (!HasPinShape(mask, w, minX, minY, bw, bh)) { o.Trace?.Invoke($"icon {bw}x{bh}@({minX},{minY}) -> not pin-shaped"); continue; }

            var icon = new PixelRect(minX, minY, bw, bh);
            var digitsRect = FindDigits(px, w, h, icon, o, out var why);
            o.Trace?.Invoke($"icon {bw}x{bh}@({minX},{minY}) aspect {aspect:F2} fill {fill:F2} -> {why}");
            if (digitsRect is { } digits)
            {
                // Prefer larger icons, then the one nearest the center of the search image.
                var cx = icon.X + icon.Width / 2.0 - w / 2.0;
                var cy = icon.Y + icon.Height / 2.0 - h / 2.0;
                var score = bh * 10 - Math.Sqrt(cx * cx + cy * cy) / Math.Max(w, h) * 20;
                plates.Add(new Plate(icon, digits, score));
            }
        }

        // Best first; drop candidates overlapping a better one (e.g. a digit's color fringe inside the plate).
        var result = new List<Plate>();
        foreach (var p in plates.OrderByDescending(p => p.Score))
            if (!result.Any(k => Overlaps(k.Bounds, p.Bounds)))
                result.Add(p);
        return result;
    }

    /// <summary>
    /// The map-pin silhouette: a ring on top tapering to a point, standing on a wide flat base.
    /// So the widest row is near the bottom, and there is a narrow "waist" between ring and base.
    /// Letters and digits of HUD text do not have this profile.
    /// </summary>
    private static bool HasPinShape(bool[] mask, int w, int x0, int y0, int bw, int bh)
    {
        var rowWidth = new int[bh];
        for (var y = 0; y < bh; y++)
        {
            int lo = -1, hi = -1;
            for (var x = 0; x < bw; x++)
                if (mask[(y0 + y) * w + x0 + x]) { if (lo < 0) lo = x; hi = x; }
            rowWidth[y] = lo < 0 ? 0 : hi - lo + 1;
        }

        int Max(double from, double to)
        {
            var m = 0;
            for (var y = (int)(from * bh); y < Math.Min(bh, (int)Math.Ceiling(to * bh)); y++) m = Math.Max(m, rowWidth[y]);
            return m;
        }
        int Min(double from, double to)
        {
            var m = int.MaxValue;
            for (var y = (int)(from * bh); y < Math.Min(bh, (int)Math.Ceiling(to * bh)); y++) m = Math.Min(m, rowWidth[y]);
            return m == int.MaxValue ? 0 : m;
        }

        var baseWidth = Max(0.78, 1.0);   // flat base
        var waist = Min(0.50, 0.78);      // pin tip above the base
        var ring = Max(0.05, 0.40);       // ring of the pin
        return baseWidth >= 0.70 * bw && waist <= 0.50 * bw && ring >= 0.35 * bw && ring <= 0.85 * bw;
    }

    private static bool Overlaps(PixelRect a, PixelRect b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    // Gap between the icon and the first digit, in icon heights (see FindDigits).
    private const double MinGap = 0.20, MaxGap = 0.55; // measured 0.30–0.33
    private const double MaxTextWidth = 2.6;           // "17,140" is 1.7h; 7 characters ≈ 2.1h

    /// <summary>White digits right of the icon, or null when this blob is not an RS indicator.</summary>
    private static PixelRect? FindDigits(byte[] px, int w, int h, PixelRect icon, PlateLocatorOptions o, out string why)
    {
        var ih = icon.Height;
        var right = icon.X + icon.Width;
        var y0 = Math.Max(0, icon.Y + (int)Math.Round(0.08 * ih));
        var y1 = Math.Min(h - 1, icon.Y + ih - 1 - (int)Math.Round(0.06 * ih));
        var x0 = right + (int)Math.Round(0.15 * ih);
        var x1 = Math.Min(w - 1, right + (int)Math.Round(3.6 * ih));
        why = "band cut off by image edge";
        if (x1 - x0 < ih || y1 - y0 < ih / 3) return null;

        // background and peak of the green channel in the band
        var hist = new int[256];
        var n = 0;
        for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++) { hist[px[(y * w + x) * 4 + 1]]++; n++; }
        int lo = Pct(hist, n, 0.50), hi = Pct(hist, n, 0.985);
        why = $"contrast {hi - lo} < {o.MinDigitContrast}";
        if (hi - lo < o.MinDigitContrast) return null;
        var t = lo + (hi - lo) / 2;

        // column profile of bright pixels
        var cols = x1 - x0 + 1;
        var colBright = new int[cols];
        for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
            if (px[(y * w + x) * 4 + 1] > t) colBright[x - x0]++;

        // Digits start after a clear gap of ≈0.6 × icon height (measured: 14 px for a 23 px icon).
        // Letters of ordinary amber/white HUD text sit much closer to each other than that.
        var first = Array.FindIndex(colBright, c => c >= 2);
        var gapToIcon = first + (x0 - right);
        why = $"gap {gapToIcon / (double)ih:F2}h outside [{MinGap:F2},{MaxGap:F2}]";
        if (first < 0 || gapToIcon < MinGap * ih || gapToIcon > MaxGap * ih) return null;

        int last = first, gap = 0, maxGap = Math.Max(3, (int)Math.Round(0.5 * ih));
        for (var c = first + 1; c < cols; c++)
        {
            if (colBright[c] >= 1) { last = c; gap = 0; }
            else if (++gap > maxGap) break;
        }
        var textWidth = last - first + 1;
        why = $"gap {gapToIcon / (double)ih:F2}h, text width {textWidth / (double)ih:F2}h < 1.0h";
        if (textWidth < 1.0 * ih) return null;             // at least "2,000"
        why = $"text width {textWidth / (double)ih:F2}h > {MaxTextWidth}h (a line of text, not an RS value)";
        if (textWidth > MaxTextWidth * ih) return null;
        why = "digits run into the image edge (truncated)";
        if (x0 + last >= w - 1 - Math.Max(2, ih / 6)) return null;

        // "Is it white?" over the bright pixels, and how many bright pixels each row has.
        long sr = 0, sg = 0, sb = 0, cnt = 0;
        var rowBright = new int[y1 - y0 + 1];
        for (var y = y0; y <= y1; y++)
        for (var x = x0 + first; x <= x0 + last; x++)
        {
            var p = (y * w + x) * 4;
            if (px[p + 1] <= t) continue;
            rowBright[y - y0]++;
            sb += px[p]; sg += px[p + 1]; sr += px[p + 2]; cnt++;
        }

        // Digit rows = the longest run of rows crossed by most digits. A bright object peeking in at
        // the top or bottom of the band (asteroid behind the plate) lights only a few pixels per row.
        var rowMin = Math.Max(2, (int)(0.35 * rowBright.Max()));
        int top = -1, bottom = -1, runStart = -1;
        for (var r = 0; r <= rowBright.Length; r++)
        {
            var on = r < rowBright.Length && rowBright[r] >= rowMin;
            if (on && runStart < 0) runStart = r;
            if (!on && runStart >= 0)
            {
                if (r - runStart > bottom - top + 1 || top < 0) { top = y0 + runStart; bottom = y0 + r - 1; }
                runStart = -1;
            }
        }
        why = "no bright pixels";
        if (cnt == 0 || top < 0) return null;
        var dh = bottom - top + 1;
        var blue = sb / (double)sg;
        var red = sr / (double)sg;
        why = $"gap {gapToIcon / (double)ih:F2}h width {textWidth / (double)ih:F2}h digitH {dh / (double)ih:F2}h B/G {blue:F2} R/G {red:F2} contrast {hi - lo}";
        if (dh < 0.3 * ih || dh > 0.85 * ih) { why += " [digit height]"; return null; } // digits are ~half the icon height
        if (blue < 0.45 || red < 0.40) { why += " [not white]"; return null; } // amber/cyan neighbours are not white digits
        why += " OK";

        // Tight box: 1 px around the digit rows/columns only. Anything further out (an asteroid above
        // the plate, scan dots) must not reach the OCR; PrepareDigits adds the clean white margin.
        const int mx = 1, my = 1;
        var dx0 = Math.Max(0, x0 + first - mx);
        var dy0 = Math.Max(0, top - my);
        var dx1 = Math.Min(w - 1, x0 + last + mx);
        var dy1 = Math.Min(h - 1, bottom + my);
        return new PixelRect(dx0, dy0, dx1 - dx0 + 1, dy1 - dy0 + 1);
    }

    private static int Pct(int[] hist, int total, double p)
    {
        var target = (long)(total * p);
        long acc = 0;
        for (var i = 0; i < 256; i++)
        {
            acc += hist[i];
            if (acc >= target) return i;
        }
        return 255;
    }

    private static bool[] Dilate(bool[] mask, int w, int h)
    {
        var d = new bool[mask.Length];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            if (!mask[y * w + x]) continue;
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if (nx >= 0 && ny >= 0 && nx < w && ny < h) d[ny * w + nx] = true;
            }
        }
        return d;
    }

    /// <summary>Top-down BGRA bytes without stride padding.</summary>
    public static byte[] ToBgra(Bitmap bmp, out int w, out int h)
    {
        w = bmp.Width; h = bmp.Height;
        var bytes = new byte[w * h * 4];
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < h; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * w * 4, w * 4);
        }
        finally { bmp.UnlockBits(data); }
        return bytes;
    }
}
