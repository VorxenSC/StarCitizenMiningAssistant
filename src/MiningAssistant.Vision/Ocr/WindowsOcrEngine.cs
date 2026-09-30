using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinOcr = Windows.Media.Ocr.OcrEngine;

namespace MiningAssistant.Vision.Ocr;

public sealed record OcrOutput(string Text, IReadOnlyList<string> Lines);

public interface IOcrEngine
{
    string Name { get; }
    Task<OcrOutput> RecognizeAsync(Bitmap image, CancellationToken ct = default);
}

/// <summary>
/// Built-in Windows 10/11 OCR (Windows.Media.Ocr). Fully local, no network, no extra install
/// beyond the OS language pack's OCR component.
/// </summary>
public sealed class WindowsOcrEngine : IOcrEngine
{
    private readonly WinOcr _engine;
    public string Name { get; }

    private WindowsOcrEngine(WinOcr engine, string name)
    {
        _engine = engine;
        Name = name;
    }

    public static IReadOnlyList<string> AvailableLanguages =>
        WinOcr.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList();

    /// <summary>Creates an engine for <paramref name="languageTag"/>, falling back to the user profile languages.</summary>
    public static WindowsOcrEngine? TryCreate(string languageTag, out string info)
    {
        try
        {
            var lang = new Language(languageTag);
            if (WinOcr.IsLanguageSupported(lang) && WinOcr.TryCreateFromLanguage(lang) is { } e)
            {
                info = $"Windows OCR ({lang.LanguageTag})";
                return new WindowsOcrEngine(e, info);
            }
        }
        catch (ArgumentException) { /* bad tag */ }

        if (WinOcr.TryCreateFromUserProfileLanguages() is { } fallback)
        {
            info = $"Windows OCR ({fallback.RecognizerLanguage.LanguageTag}, fallback — '{languageTag}' not installed)";
            return new WindowsOcrEngine(fallback, info);
        }

        info = "No Windows OCR language installed. Install the English OCR capability (see README).";
        return null;
    }

    public async Task<OcrOutput> RecognizeAsync(Bitmap image, CancellationToken ct = default)
    {
        using var sb = ToSoftwareBitmap(image);
        var result = await _engine.RecognizeAsync(sb).AsTask(ct).ConfigureAwait(false);
        var lines = result.Lines.Select(l => l.Text).ToList();
        return new OcrOutput(string.Join("\n", lines), lines);
    }

    private static SoftwareBitmap ToSoftwareBitmap(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[bmp.Width * bmp.Height * 4];
            for (var y = 0; y < bmp.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * bmp.Width * 4, bmp.Width * 4);

            using var writer = new DataWriter();
            writer.WriteBytes(bytes);
            return SoftwareBitmap.CreateCopyFromBuffer(writer.DetachBuffer(), BitmapPixelFormat.Bgra8, bmp.Width, bmp.Height, BitmapAlphaMode.Premultiplied);
        }
        finally { bmp.UnlockBits(data); }
    }
}
