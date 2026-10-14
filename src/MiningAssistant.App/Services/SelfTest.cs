using System.Drawing;
using System.IO;
using System.Text;
using MiningAssistant.Core.Matching;
using MiningAssistant.Core.Materials;
using MiningAssistant.Core.Roi;
using MiningAssistant.Core.Settings;
using MiningAssistant.Vision.Capture;
using MiningAssistant.Vision.Ocr;
using MiningAssistant.Vision.Pipeline;

namespace MiningAssistant.App.Services;

/// <summary>
/// Headless check of everything the app needs on this PC (run with <c>--selftest</c>):
/// writable folders, settings, signature database, monitors, screen capture and Windows OCR.
/// Writes config/selftest.txt and returns 0 when everything passed. No window is shown.
/// </summary>
public static class SelfTest
{
    public static int Run()
    {
        var paths = new AppPaths();
        var sb = new StringBuilder();
        var failures = 0;

        void Check(string name, Func<string> test)
        {
            try { sb.AppendLine($"[ OK ] {name}: {test()}"); }
            catch (Exception ex) { failures++; sb.AppendLine($"[FAIL] {name}: {ex.GetType().Name}: {ex.Message}"); }
        }

        sb.AppendLine($"Mining Assistant by Vorxen — self-test — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Folder: {paths.Root}");
        sb.AppendLine($"OS: {Environment.OSVersion} · .NET {Environment.Version} · 64-bit process: {Environment.Is64BitProcess}");
        sb.AppendLine();

        AppSettings settings = new();
        MaterialDatabase? db = null;
        IOcrEngine? ocr = null;

        Check("Config folder is writable", () =>
        {
            Directory.CreateDirectory(paths.ConfigRoot);
            var probe = Path.Combine(paths.ConfigRoot, "write-test.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return paths.ConfigRoot;
        });
        Check("Settings", () => { settings = paths.LoadSettings(); return paths.SettingsFile; });
        Check("Signature database", () =>
        {
            db = MaterialDatabaseLoader.LoadForVersion(paths.DataRoot, settings.GameVersion);
            var problems = db.Validate();
            if (problems.Count > 0) throw new InvalidDataException(string.Join("; ", problems));
            return $"{db.Ores.Count()} ores + {db.Enabled.Count() - db.Ores.Count()} other signals ({db.SourcePath})";
        });
        Check("Search area", () =>
        {
            var p = paths.LoadProfile(settings.ActiveProfile);
            return p.Get(RoiNames.RS)?.ToString() ?? throw new InvalidDataException("no RS search area (preset file missing?)");
        });
        Check("Monitors", () =>
        {
            Monitors.EnablePerMonitorDpiAwareness();
            var all = Monitors.GetAll();
            var game = Monitors.Get(settings.MonitorIndex);
            if (game.Height < 1080) throw new InvalidOperationException($"game monitor is {game.Width}×{game.Height}; 1080p is the minimum");
            return string.Join(" | ", all.Select(m => m.ToString())) + $"  → game monitor #{game.Index}";
        });
        Check("Screen capture", () =>
        {
            var m = Monitors.Get(settings.MonitorIndex);
            using var bmp = new GdiScreenCapture().Capture(new PixelRect(m.Left + m.Width / 2 - 32, m.Top + m.Height / 2 - 32, 64, 64));
            return $"{bmp.Width}×{bmp.Height} px captured";
        });
        Check("Windows OCR engine", () =>
        {
            ocr = WindowsOcrEngine.TryCreate(settings.Ocr.Language, out var info);
            if (ocr is null) throw new InvalidOperationException(info + " Installed OCR languages: " +
                                                                 (WindowsOcrEngine.AvailableLanguages.Count == 0 ? "none" : string.Join(", ", WindowsOcrEngine.AvailableLanguages)));
            return info;
        });
        Check("OCR reads a number", () =>
        {
            if (ocr is null) throw new InvalidOperationException("no OCR engine");
            using var bmp = new Bitmap(360, 110);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                using var font = new Font("Segoe UI", 40, FontStyle.Bold, GraphicsUnit.Pixel);
                g.DrawString("17,140", font, Brushes.Black, 60, 25);
            }
            var text = ocr.RecognizeAsync(bmp).GetAwaiter().GetResult().Text.Trim();
            if (!text.Replace(".", ",").Contains("17,140")) throw new InvalidDataException($"expected 17,140 but OCR returned “{text}”");
            return $"“{text}”";
        });
        Check("Signature resolver", () =>
        {
            if (db is null) throw new InvalidOperationException("no database");
            var r = new SignatureMatcher(db, settings.Matcher).Match(17140);
            if (r.Best?.Material.Name != "Aluminum") throw new InvalidDataException($"17140 resolved as {r.Best?.Label ?? r.Status.ToString()}");
            return $"17140 → {r.Best.Label}";
        });

        sb.AppendLine();
        sb.AppendLine(failures == 0 ? "RESULT: ALL CHECKS PASSED" : $"RESULT: {failures} CHECK(S) FAILED");

        var report = sb.ToString();
        try
        {
            Directory.CreateDirectory(paths.ConfigRoot);
            File.WriteAllText(Path.Combine(paths.ConfigRoot, "selftest.txt"), report);
        }
        catch { File.WriteAllText(Path.Combine(Path.GetTempPath(), "MiningAssistant-selftest.txt"), report); }
        return failures == 0 ? 0 : 1;
    }
}
