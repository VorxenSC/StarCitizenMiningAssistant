using System.Drawing;
using System.Drawing.Imaging;
using MiningAssistant.Vision.Imaging;

namespace MiningAssistant.Vision.Pipeline;

/// <summary>
/// Keeps local evidence of recognition failures so they can be fixed: when the RS indicator is on
/// screen but no number could be read ("missed"), or a new stable RS matches nothing ("unknown").
/// Rate-limited, de-duplicated and capped; files never leave the PC.
/// </summary>
public sealed class MissedReadRecorder
{
    private readonly string _folder;
    private readonly int _maxFiles;
    private readonly TimeSpan _minInterval;
    private DateTime _lastSave = DateTime.MinValue;
    private ulong _lastHash;

    public MissedReadRecorder(string folder, int maxFiles = 300, TimeSpan? minInterval = null)
    {
        _folder = folder;
        _maxFiles = maxFiles;
        _minInterval = minInterval ?? TimeSpan.FromSeconds(3);
    }

    public int Saved { get; private set; }

    public void Record(string reason, Bitmap roi, string rawOcr, DateTime now)
    {
        if (now - _lastSave < _minInterval) return;
        var hash = ImageOps.SampleHash(roi);
        if (hash == _lastHash) return;
        _lastHash = hash;
        _lastSave = now;

        try
        {
            Directory.CreateDirectory(_folder);
            var stem = Path.Combine(_folder, $"{now.ToLocalTime():yyyyMMdd_HHmmss_fff}_{reason}");
            roi.Save(stem + ".png", ImageFormat.Png);
            File.WriteAllText(stem + ".txt", rawOcr);
            Saved++;
            Trim();
        }
        catch (IOException) { /* diagnostics must never disturb scanning */ }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Keeps only the newest files (these are the app's own diagnostic files).</summary>
    private void Trim()
    {
        var files = new DirectoryInfo(_folder).GetFiles("*.png").OrderByDescending(f => f.Name).Skip(_maxFiles).ToList();
        foreach (var f in files)
        {
            f.Delete();
            var txt = Path.ChangeExtension(f.FullName, ".txt");
            if (File.Exists(txt)) File.Delete(txt);
        }
    }
}
