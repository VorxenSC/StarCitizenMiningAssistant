using System.Drawing;
using System.Text.RegularExpressions;
using MiningAssistant.Core;
using MiningAssistant.Core.Matching;
using MiningAssistant.Core.Materials;
using MiningAssistant.Core.Roi;
using MiningAssistant.Core.Settings;
using MiningAssistant.Vision.Ocr;
using MiningAssistant.Vision.Pipeline;

namespace MiningAssistant.Tests;

/// <summary>
/// End-to-end OCR on native in-game captures (test_images/real, 3440×1440).
/// Requires the Windows OCR language pack; returns early when it is not installed.
/// </summary>
public class RealScreenshotTests
{
    private static string RealDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MiningAssistant.sln"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "test_images", "real");
    }

    [Fact]
    public async Task Native_screenshots_read_the_expected_rs()
    {
        var ocr = WindowsOcrEngine.TryCreate("en-US", out _);
        if (ocr is null) return;

        var dir = RealDir();
        var profile = Json.Deserialize<RoiProfile>(File.ReadAllText(Path.Combine(dir, "profile.roi.json")));
        var pipeline = new ScanPipeline(ocr, TestData.Matcher(), new AppSettings().Ocr);
        var files = Directory.GetFiles(dir, "*.jpg");
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var expected = int.Parse(Regex.Match(Path.GetFileName(file), @"rs(\d+)").Groups[1].Value);
            using var bmp = new Bitmap(file);
            var r = await pipeline.AnalyzeScreenshotAsync(bmp, profile, false);
            Assert.True(r.Reading.RS == expected, $"{Path.GetFileName(file)}: expected {expected}, got {r.Reading.RS} ({r.RawText})");
        }
    }

    [Fact]
    public void Real_values_resolve_to_the_right_answer()
    {
        var m = TestData.Matcher();
        var titanium = m.Match(15420);
        Assert.Equal(MatchStatus.Exact, titanium.Status);
        Assert.Equal(("Titanium", 4), (titanium.Best!.Material.Name, titanium.Best.RockCount));

        var taranite = m.Match(7110);
        Assert.Equal(("Taranite", 2), (taranite.Best!.Material.Name, taranite.Best.RockCount));

        var salvage = m.Match(2000); // not mining: a salvage panel, never an "unknown ore"
        Assert.Equal(MatchStatus.Exact, salvage.Status);
        Assert.Equal(SignalKind.Salvage, salvage.Best!.Material.Kind);
    }
}
