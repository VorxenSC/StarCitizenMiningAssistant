using System.Drawing;
using System.Drawing.Imaging;
using MiningAssistant.Core;
using MiningAssistant.Core.Roi;

namespace MiningAssistant.Vision.Pipeline;

public sealed record UnknownSignatureRecord(
    DateTime Timestamp,
    int? RS,
    string RawOcr,
    string? ImageFile,
    PixelRect ScreenRect,
    string GameVersion);

/// <summary>
/// Saves unrecognized signatures locally (cropped PNG + one JSON line) so new RS codes can be
/// identified after patches. Nothing leaves the machine.
/// </summary>
public sealed class UnknownSignatureLogger
{
    private readonly string _folder;

    public UnknownSignatureLogger(string folder) => _folder = folder;

    public string LogFile => Path.Combine(_folder, "unknown_signatures.jsonl");

    public UnknownSignatureRecord Save(int? rs, string rawOcr, Bitmap? roi, PixelRect screenRect, string gameVersion)
    {
        Directory.CreateDirectory(_folder);
        var now = DateTime.Now;
        string? image = null;
        if (roi is not null)
        {
            image = $"{now:yyyyMMdd_HHmmss}_rs{rs?.ToString() ?? "none"}.png";
            roi.Save(Path.Combine(_folder, image), ImageFormat.Png);
        }

        var record = new UnknownSignatureRecord(now, rs, rawOcr, image, screenRect, gameVersion);
        var line = System.Text.Json.JsonSerializer.Serialize(record, new System.Text.Json.JsonSerializerOptions(Json.Options) { WriteIndented = false });
        File.AppendAllText(LogFile, line + Environment.NewLine);
        return record;
    }
}
