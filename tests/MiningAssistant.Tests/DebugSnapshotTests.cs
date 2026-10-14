using System.Drawing;
using MiningAssistant.Core.Roi;
using MiningAssistant.Core.Settings;
using MiningAssistant.Vision.Capture;
using MiningAssistant.Vision.Ocr;
using MiningAssistant.Vision.Pipeline;

namespace MiningAssistant.Tests;

public class DebugSnapshotTests
{
    [Fact]
    public async Task Snapshot_folder_contains_every_stage_and_a_report()
    {
        var ocr = WindowsOcrEngine.TryCreate("en-US", out _);
        if (ocr is null) return;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MiningAssistant.sln"))) dir = dir.Parent;
        var file = Directory.GetFiles(Path.Combine(dir!.FullName, "test_images", "real"), "*titanium*_a.jpg").Single();
        using var screen = new Bitmap(file);

        var settings = new AppSettings();
        var monitor = new MonitorInfo(0, "TEST", 0, 0, screen.Width, screen.Height, true);
        var area = settings.Ocr.SearchArea(new NormalizedRect(0.30, 0.12, 0.40, 0.46)).ToPixels(screen.Width, screen.Height);
        var matcher = TestData.Matcher();
        var root = Path.Combine(Path.GetTempPath(), "ma-snapshot-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = await new DebugSnapshotWriter(root).SaveAsync(screen,
                new SnapshotContext(monitor, area, settings, "default", "live status", "TITANIUM", "unit test"),
                new ScanPipeline(ocr, matcher, settings.Ocr), matcher);

            foreach (var f in new[] { "1_screen.png", "2_search_area.png", "2_search_area_marked.png", "3_plate.png", "report.txt" })
                Assert.True(File.Exists(Path.Combine(path, f)), f);
            Assert.NotEmpty(Directory.GetFiles(path, "4_ocr_*.png"));

            var report = File.ReadAllText(Path.Combine(path, "report.txt"));
            Assert.Contains("1 plate(s) found", report);
            Assert.Contains("Read: 15,420", report);
            Assert.Contains("Titanium", report);
            Assert.Contains("unit test", report);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
