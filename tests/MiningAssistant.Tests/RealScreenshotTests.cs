using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text.RegularExpressions;
using MiningAssistant.Core;
using MiningAssistant.Core.Matching;
using MiningAssistant.Core.Materials;
using MiningAssistant.Core.Roi;
using MiningAssistant.Core.Settings;
using MiningAssistant.Vision.Imaging;
using MiningAssistant.Vision.Ocr;
using MiningAssistant.Vision.Pipeline;

namespace MiningAssistant.Tests;

/// <summary>
/// End-to-end tests on real captures:
///   test_images/real  — native 3440×1440 in-game screenshots (JPG),
///   test_images/live  — live ROI captures the app originally FAILED to read (asteroid touching the
///                       plate, scan dots behind it). They are the regression set for the plate locator.
/// OCR tests return early when the Windows OCR language pack is not installed.
/// </summary>
public class RealScreenshotTests
{
    private static string Dir(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MiningAssistant.sln"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "test_images", name);
    }

    private static int Expected(string file) => int.Parse(Regex.Match(Path.GetFileName(file), @"rs(\d+)").Groups[1].Value);

    private static ScanPipeline? Pipeline()
    {
        var ocr = WindowsOcrEngine.TryCreate("en-US", out _);
        return ocr is null ? null : new ScanPipeline(ocr, TestData.Matcher(), new AppSettings().Ocr);
    }

    private static Bitmap Scaled(Bitmap src, int width)
    {
        var dst = new Bitmap(width, src.Height * width / src.Width);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(src, 0, 0, dst.Width, dst.Height);
        return dst;
    }

    // ───────────────────────── plate locator (no OCR needed) ─────────────────────────

    [Fact]
    public void Locator_finds_exactly_the_rs_plate_on_a_whole_screenshot()
    {
        foreach (var file in Directory.GetFiles(Dir("real"), "*.jpg"))
        {
            using var bmp = new Bitmap(file);
            var plates = PlateLocator.Find(bmp, new PlateLocatorOptions { ScreenHeight = bmp.Height });

            var plate = Assert.Single(plates);            // chat text, HUD labels etc. are not plates
            Assert.InRange(plate.Icon.Height, 21, 26);    // ≈23 px at 1440p
            Assert.InRange(plate.Icon.X, 1600, 1800);     // just above the center reticle
            Assert.InRange(plate.Icon.Y, 400, 520);
            Assert.True(plate.Digits.X > plate.Icon.X + plate.Icon.Width, "digits are right of the icon");
        }
    }

    [Fact]
    public void Locator_finds_the_plate_in_the_live_failure_cases()
    {
        var files = Directory.GetFiles(Dir("live"), "*.png");
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            using var bmp = new Bitmap(file);
            var plate = Assert.Single(PlateLocator.Find(bmp));
            Assert.InRange(plate.Icon.Height, 21, 26);
        }
    }

    [Fact]
    public void Locator_rejects_a_plate_cut_by_the_edge_instead_of_reading_half_a_number()
    {
        var file = Directory.GetFiles(Dir("live"), "*scan-dots*.png").Single();
        using var bmp = new Bitmap(file);
        var plate = Assert.Single(PlateLocator.Find(bmp));

        // cut through the digits
        var cutX = plate.Digits.X + plate.Digits.Width / 2;
        using var cutDigits = bmp.Clone(new Rectangle(0, 0, cutX, bmp.Height), bmp.PixelFormat);
        Assert.Empty(PlateLocator.Find(cutDigits));

        // cut through the icon
        var iconMid = plate.Icon.X + plate.Icon.Width / 2;
        using var cutIcon = bmp.Clone(new Rectangle(iconMid, 0, bmp.Width - iconMid, bmp.Height), bmp.PixelFormat);
        Assert.Empty(PlateLocator.Find(cutIcon));
    }

    [Fact]
    public void Locator_finds_nothing_in_empty_space_or_on_a_desktop_like_image()
    {
        using var bmp = new Bitmap(Directory.GetFiles(Dir("real"), "*.jpg")[0]);
        using var empty = bmp.Clone(new Rectangle(1200, 900, 1000, 400), bmp.PixelFormat); // space below the HUD
        Assert.Empty(PlateLocator.Find(empty));

        using var desktop = new Bitmap(900, 300);
        using (var g = Graphics.FromImage(desktop))
        {
            g.Clear(Color.FromArgb(32, 32, 32));
            using var f = new Font("Segoe UI", 14);
            g.DrawString("Downloads   (D:)  12,345 free of 949 GB", f, Brushes.White, 20, 40);
            g.DrawString("[GLOBAL] warning 17,140 items", f, Brushes.Orange, 20, 120);
            g.FillRectangle(Brushes.Orange, 400, 200, 20, 23); // amber block followed by white digits
            g.DrawString("17,140", f, Brushes.White, 430, 198);
        }
        Assert.Empty(PlateLocator.Find(desktop));
    }

    // ───────────────────────── full pipeline (needs Windows OCR) ─────────────────────────

    [Fact]
    public async Task Native_screenshots_read_the_expected_rs_through_the_search_area()
    {
        if (Pipeline() is not { } pipeline) return;
        var dir = Dir("real");
        var profile = Json.Deserialize<RoiProfile>(File.ReadAllText(Path.Combine(dir, "profile.roi.json")));

        foreach (var file in Directory.GetFiles(dir, "*.jpg"))
        {
            using var bmp = new Bitmap(file);
            var r = await pipeline.AnalyzeScreenshotAsync(bmp, profile, false);
            Assert.NotNull(r.Plate);
            Assert.True(r.Reading.RS == Expected(file), $"{Path.GetFileName(file)}: expected {Expected(file)}, got {r.Reading.RS} ({r.RawText})");
        }
    }

    [Fact]
    public async Task Live_captures_the_app_used_to_miss_are_now_read()
    {
        if (Pipeline() is not { } pipeline) return;
        foreach (var file in Directory.GetFiles(Dir("live"), "*.png"))
        {
            using var bmp = new Bitmap(file);
            var r = await pipeline.AnalyzeScreenshotAsync(bmp, null, false);
            Assert.NotNull(r.Plate);
            Assert.True(r.Reading.RS == Expected(file), $"{Path.GetFileName(file)}: expected {Expected(file)}, got {r.Reading.RS} ({r.RawText})");
        }
    }

    /// <summary>
    /// Downscaled copies simulate smaller screens. 2560 wide ≈ 1072 px tall, i.e. the HUD size of a
    /// 1080p monitor (icon ≈ 17 px). Below ~1000 px of height digits get under 8 px and OCR becomes
    /// unreliable — that is the documented minimum, not something these tests claim to cover.
    /// </summary>
    [Theory]
    [InlineData(2560)]
    public async Task Works_at_1080p_hud_size_without_recalibration(int width)
    {
        if (Pipeline() is not { } pipeline) return;
        var dir = Dir("real");
        var profile = Json.Deserialize<RoiProfile>(File.ReadAllText(Path.Combine(dir, "profile.roi.json")));

        foreach (var file in Directory.GetFiles(dir, "*.jpg"))
        {
            using var native = new Bitmap(file);
            using var bmp = Scaled(native, width);
            var r = await pipeline.AnalyzeScreenshotAsync(bmp, profile, false);
            Assert.True(r.Reading.RS == Expected(file), $"{Path.GetFileName(file)} @ {bmp.Width}×{bmp.Height}: got {r.Reading.RS} ({r.RawText})");
        }
    }

    [Fact]
    public void Real_values_resolve_to_the_right_answer()
    {
        var m = TestData.Matcher();
        var titanium = m.Match(15420);
        Assert.Equal(MatchStatus.Exact, titanium.Status);
        Assert.Equal(("Titanium", 4), (titanium.Best!.Material.Name, titanium.Best.RockCount));

        var aluminum = m.Match(17140);
        Assert.Equal(("Aluminum", 4), (aluminum.Best!.Material.Name, aluminum.Best.RockCount));

        var taranite = m.Match(7110);
        Assert.Equal(("Taranite", 2), (taranite.Best!.Material.Name, taranite.Best.RockCount));

        var salvage = m.Match(2000); // not mining: a salvage panel, never an "unknown ore"
        Assert.Equal(MatchStatus.Exact, salvage.Status);
        Assert.Equal(SignalKind.Salvage, salvage.Best!.Material.Kind);
    }
}
