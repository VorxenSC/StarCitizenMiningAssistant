using MiningAssistant.Core;
using MiningAssistant.Core.Materials;
using MiningAssistant.Core.Presentation;
using MiningAssistant.Core.Roi;
using MiningAssistant.Core.Settings;
using MiningAssistant.Core.Temporal;

namespace MiningAssistant.Tests;

public class DatabaseAndProfileTests
{
    [Fact]
    public void Shipped_database_is_valid()
    {
        var db = TestData.Database;
        Assert.Equal(26, db.Ores.Count());
        Assert.Equal(7, db.Enabled.Count(m => m.Kind == SignalKind.AsteroidType));
        Assert.Contains(db.Enabled, m => m.Kind == SignalKind.RocDeposit && m.BaseRS == 4000);
        Assert.Contains(db.Enabled, m => m.Kind == SignalKind.FpsDeposit && m.BaseRS == 3000);
        Assert.Contains(db.Enabled, m => m.Kind == SignalKind.Salvage && m.BaseRS == 2000);
        Assert.DoesNotContain(db.Enabled, m => m.Name == "Carinite"); // experimental: reference only
        Assert.Empty(db.Validate());
        Assert.Equal(3585, db.FindByName("Gold")!.BaseRS);
        Assert.Equal(MaterialPriority.VeryHigh, db.FindByName("Quantainium")!.Priority);
    }

    [Theory]
    [InlineData("LARANITE RAW", "Laranite")]
    [InlineData("Gold (Raw)", "Gold")]
    [InlineData("  gold ore ", "Gold")]
    [InlineData("Aluminium", "Aluminum")]
    public void Raw_suffix_and_aliases_resolve(string hud, string expected) =>
        Assert.Equal(expected, TestData.Database.FindByName(hud)?.Name);

    [Fact]
    public void Missing_version_falls_back_to_default()
    {
        var path = MaterialDatabaseLoader.ResolvePath(TestData.DataRoot, "9.99-does-not-exist");
        Assert.EndsWith(Path.Combine("default", "materials.json"), path);
        Assert.Contains("default", MaterialDatabaseLoader.ListVersions(TestData.DataRoot));
    }

    [Fact]
    public void Normalized_rect_round_trips_and_scales()
    {
        var r = NormalizedRect.FromPixels(new PixelRect(1720, 360, 344, 72), 3440, 1440);
        Assert.Equal(new PixelRect(1720, 360, 344, 72), r.ToPixels(3440, 1440));
        Assert.Equal(new PixelRect(960, 270, 192, 54), r.ToPixels(1920, 1080));
    }

    [Fact]
    public void Normalized_rect_is_clamped_to_surface()
    {
        var r = new NormalizedRect(0.9, 0.9, 0.5, 0.5);
        var p = r.ToPixels(1000, 1000);
        Assert.Equal(new PixelRect(900, 900, 100, 100), p);
    }

    [Fact]
    public void Settings_round_trip_through_json()
    {
        var s = new AppSettings { GameVersion = "4.10.1", TargetMaterials = { "Gold" }, OcrIntervalMs = 500 };
        var back = Json.Deserialize<AppSettings>(Json.Serialize(s));
        Assert.Equal("4.10.1", back.GameVersion);
        Assert.Equal(500, back.OcrIntervalMs);
        Assert.Equal(new[] { "Gold" }, back.TargetMaterials);
    }

    [Fact]
    public void Presenter_formats_match_and_target_filter()
    {
        var session = new ScanSession(TestData.Matcher(), 1, 1);
        var st = session.Observe(14340, DateTime.UtcNow);
        var settings = new AppSettings();

        var v = OverlayPresenter.Build(st, settings);
        Assert.Equal(OverlayKind.Match, v.Kind);
        Assert.Equal("GOLD", v.Headline);
        Assert.Equal("RS 14,340", v.RsLine);
        Assert.Equal("4 ROCKS", v.RocksLine);
        Assert.Contains("VERY HIGH", v.PriorityLine);

        settings.TargetOnly = true;
        settings.TargetMaterials.Add("Tungsten");
        Assert.Equal(OverlayKind.NotTarget, OverlayPresenter.Build(st, settings).Kind);

        settings.TargetMaterials.Add("gold");
        var t = OverlayPresenter.Build(st, settings);
        Assert.True(t.IsTarget);
    }
}
