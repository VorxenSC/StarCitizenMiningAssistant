using System.IO;
using System.Reflection;
using MiningAssistant.App.Windows;
using MiningAssistant.Core.Matching;
using MiningAssistant.Core.Materials;
using MiningAssistant.Core.Roi;
using MiningAssistant.Vision.Capture;
using MiningAssistant.Vision.Imaging;
using MiningAssistant.Vision.Pipeline;

namespace MiningAssistant.App.Services;

/// <summary>Unknown-signature pop-up, learned signatures and (opt-in) contributions to GitHub.</summary>
public sealed partial class AppHost
{
    private LearnedSignatureStore _learned = null!;
    private SignatureContributor _contributor = null!;
    private UnknownSignatureWindow? _unknownWindow;
    private readonly HashSet<int> _askedThisSession = new();
    private (bool ok, string detail) _ghStatus = (false, "checking…");

    private static string AppVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

    public int LearnedCount => _learned.All.Count;
    public int PendingContributions => _learned.Pending.Count();

    private void InitLearning()
    {
        _learned = new LearnedSignatureStore(Paths.LearnedSignaturesFile);
        _contributor = new SignatureContributor(Path.Combine(Paths.ConfigRoot, "uploads"));
        RefreshGitHubStatus();
    }

    public async void RefreshGitHubStatus()
    {
        _ghStatus = (false, "checking…");
        _ghStatus = await Task.Run(SignatureContributor.CheckAsync);
        UpdateStatus();
    }

    /// <summary>A new stable RS that nothing explains: ask the user what it is (once per value per session).</summary>
    private void MaybeAskUnknown(ScanFrame frame)
    {
        if (!Settings.AskForUnknownSignatures || frame.State is not { IsNew: true, StableRS: int rs, Match.Status: MatchStatus.Unknown }) return;
        if (Settings.IgnoredUnknownRs.Contains(rs) || !_askedThisSession.Add(rs)) return;

        byte[]? png = null;
        if (frame.Analysis?.Plate is { } plate)
        {
            using var roi = _scanner?.CloneLastRoi();
            if (roi is not null)
            {
                var b = plate.Bounds;
                using var crop = ImageOps.Crop(roi, new PixelRect(Math.Max(0, b.X - 12), Math.Max(0, b.Y - 12), b.Width + 24, b.Height + 24));
                png = ImageOps.ToPng(crop);
            }
        }

        UnknownWindow().Enqueue(new UnknownItem(rs, DateTime.Now, png), Monitors.Get(Settings.MonitorIndex));
        Log($"Unknown signature RS {rs}: asking the user.");
    }

    private UnknownSignatureWindow UnknownWindow()
    {
        if (_unknownWindow is null)
        {
            _unknownWindow = new UnknownSignatureWindow(_db.Materials.Select(m => m.Name));
            _unknownWindow.Saved += OnUnknownSaved;
            _unknownWindow.NeverAsk += item =>
            {
                if (!Settings.IgnoredUnknownRs.Contains(item.RS)) Settings.IgnoredUnknownRs.Add(item.RS);
                Paths.SaveSettings(Settings);
                Log($"Never ask again for RS {item.RS}.");
            };
        }
        _unknownWindow.SetSendOption(Settings.Contribute.Enabled && _ghStatus.ok,
            !Settings.Contribute.Enabled ? "Sending is off in the control panel (Functions → Send named signatures to GitHub)."
            : !_ghStatus.ok ? "Not available: " + _ghStatus.detail + ". The signature is still saved on this PC."
            : $"Only these values and the plate image go to {Settings.Contribute.Repository} (private). Never the full screen.");
        return _unknownWindow;
    }

    private async void OnUnknownSaved(UnknownAnswer a)
    {
        string? imagePath = null;
        if (a.Item.PlatePng is { } png)
        {
            Directory.CreateDirectory(Paths.LearnedImagesRoot);
            imagePath = Path.Combine(Paths.LearnedImagesRoot, $"{a.Item.At:yyyyMMdd_HHmmss}_rs{a.Item.RS}.png");
            await File.WriteAllBytesAsync(imagePath, png);
        }

        var s = _learned.Add(new LearnedSignature
        {
            Name = a.Name, Kind = a.Kind, ObservedRS = a.Item.RS, Count = a.Count, Notes = a.Notes,
            GameVersion = Settings.GameVersion, LearnedAt = a.Item.At, PlateImage = imagePath,
        });

        // Recognise it from now on.
        LoadDatabase();
        _scanner?.Reconfigure(BuildConfig());
        Flash($"✓ learned: RS {s.ObservedRS:N0} = {s.Name} ×{s.Count}", 4);
        Log($"Learned signature: RS {s.ObservedRS} = {s.Name} ×{s.Count} (base {s.BaseRS}, {s.Kind}).");

        if (a.Send) await SubmitAsync(s);
        UpdateStatus();
    }

    private async Task SubmitAsync(LearnedSignature s)
    {
        if (!Settings.Contribute.Enabled) return;
        var png = s.PlateImage is { } p && File.Exists(p) ? await File.ReadAllBytesAsync(p) : null;
        var (ok, message, folder) = await Task.Run(() => _contributor.SubmitAsync(s, png, Settings.Contribute, AppVersion));
        if (ok)
        {
            s.Submitted = true;
            s.SubmittedPath = folder;
            _learned.Save();
            Flash($"↑ sent RS {s.ObservedRS:N0} to GitHub", 3);
            Log($"Contribution sent: {folder}");
        }
        else
        {
            Flash("⚠ not sent (kept on this PC): " + message, 5);
            Log($"Contribution failed for RS {s.ObservedRS}: {message}");
        }
    }

    /// <summary>Retries every learned signature that was not sent yet.</summary>
    public async void SendPendingContributions()
    {
        if (!Settings.Contribute.Enabled) { Flash("Turn on “Send named signatures to GitHub” first"); return; }
        foreach (var s in _learned.Pending.ToList()) await SubmitAsync(s);
        UpdateStatus();
    }

    public void OpenLearnedFile()
    {
        if (!File.Exists(Paths.LearnedSignaturesFile)) _learned.Save();
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = Paths.LearnedSignaturesFile, UseShellExecute = true });
    }

    private HealthItem ContributionHealth() => !Settings.Contribute.Enabled
        ? new(true, "GitHub contributions", $"off · {LearnedCount} learned signature(s) on this PC")
        : new(_ghStatus.ok, "GitHub contributions",
            (_ghStatus.ok ? $"on → {Settings.Contribute.Repository}" : _ghStatus.detail) +
            $" · learned {LearnedCount}, not sent {PendingContributions}");
}
