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

/// <summary>
/// Unknown-signature pop-up, learned signatures and sharing them. Sharing is always manual: the user
/// copies a text for Discord or opens a pre-filled GitHub issue in their own browser. The app never
/// sends anything by itself and needs no GitHub account or tool.
/// </summary>
public sealed partial class AppHost
{
    private LearnedSignatureStore _learned = null!;
    private UnknownSignatureWindow? _unknownWindow;
    private readonly HashSet<int> _askedThisSession = new();

    private static string AppVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

    public int LearnedCount => _learned.All.Count;
    public int NotSharedCount => _learned.Pending.Count();

    private void InitLearning() => _learned = new LearnedSignatureStore(Paths.LearnedSignaturesFile);

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
        UpdateStatus();
    }

    /// <summary>What to share: the signatures not shared yet, or all of them when everything was shared before.</summary>
    private List<LearnedSignature>? ToShare()
    {
        var items = _learned.Pending.ToList();
        if (items.Count == 0) items = _learned.All.ToList();
        if (items.Count > 0) return items;
        System.Windows.MessageBox.Show("No learned signatures yet. When an unknown RS appears, name it in the pop-up first.",
            "Share learned signatures", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        return null;
    }

    private void MarkShared(IEnumerable<LearnedSignature> items, string how)
    {
        foreach (var s in items)
        {
            s.Submitted = true;
            s.SubmittedPath = $"{how} {DateTime.Now:yyyy-MM-dd}";
        }
        _learned.Save();
        UpdateStatus();
    }

    /// <summary>Copies a ready-to-paste text for Discord. Plate images can be dragged from the folder that opens.</summary>
    public void ShareForDiscord()
    {
        if (ToShare() is not { } items) return;
        var text = SignatureShare.DiscordText(items, AppVersion, out var shown);
        // Only what fit in one Discord message counts as shared; the rest stays "not shared" for the next copy.
        items = items.Take(shown).ToList();
        try { System.Windows.Clipboard.SetText(text); }
        catch (Exception ex)
        {
            Log("Clipboard failed: " + ex.Message);
            System.Windows.MessageBox.Show("Could not copy to the clipboard. Try again.", "Share learned signatures");
            return;
        }
        MarkShared(items, "discord");
        Flash($"📋 copied {items.Count} signature(s) — paste in Discord", 4);
        Log($"Copied {items.Count} learned signature(s) for Discord.");
        OpenPlateImagesIfAny(items);
    }

    /// <summary>Opens a pre-filled issue in the user's browser; they review it and click Submit there.</summary>
    public void ShareOnGitHub()
    {
        if (ToShare() is not { } items) return;
        var url = SignatureShare.IssueUrl(Settings.Share.Repository, items, AppVersion, out var included);
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch (Exception ex)
        {
            Log("Opening the browser failed: " + ex.Message);
            System.Windows.MessageBox.Show("Could not open your web browser. Use “Share on Discord (copy)” instead.", "Share learned signatures");
            return;
        }
        MarkShared(items.Take(included), "github");
        Flash(included < items.Count
            ? $"🌐 opened a GitHub issue with {included} of {items.Count} — click it again for the rest"
            : $"🌐 opened a GitHub issue with {included} signature(s) — submit it in the browser", 5);
        Log($"Opened a GitHub issue for {included} learned signature(s).");
        OpenPlateImagesIfAny(items.Take(included));
    }

    private void OpenPlateImagesIfAny(IEnumerable<LearnedSignature> items)
    {
        if (items.Any(s => s.PlateImage is { } p && File.Exists(p)) && Directory.Exists(Paths.LearnedImagesRoot))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = Paths.LearnedImagesRoot, UseShellExecute = true });
    }

    public void OpenLearnedFile()
    {
        if (!File.Exists(Paths.LearnedSignaturesFile)) _learned.Save();
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = Paths.LearnedSignaturesFile, UseShellExecute = true });
    }

    private HealthItem LearningHealth() => new(true, "Learned signatures",
        LearnedCount == 0
            ? "none yet — name unknown signatures in the pop-up, then share them from Advanced settings"
            : $"{LearnedCount} on this PC · {NotSharedCount} not shared yet" +
              (NotSharedCount > 0 ? " — Advanced settings → Share on Discord / GitHub" : ""));
}
