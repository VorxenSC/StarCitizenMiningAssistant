using System.Text;
using System.Text.Json;
using MiningAssistant.Core.Matching;
using MiningAssistant.Core.Materials;

namespace MiningAssistant.Tests;

public class LearnedSignatureTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"learned-{Guid.NewGuid():N}.json");

    [Fact]
    public void Named_unknown_is_recognised_after_merge()
    {
        var path = TempFile();
        try
        {
            var store = new LearnedSignatureStore(path);
            var matcher0 = TestData.Matcher();
            Assert.Equal(MatchStatus.Unknown, matcher0.Match(5011).Status);

            store.Add(new LearnedSignature { Name = "Mysterium", ObservedRS = 10022, Count = 2, GameVersion = "4.10" });
            var db = MaterialDatabaseLoader.LoadForVersion(TestData.DataRoot, "default");
            new LearnedSignatureStore(path).MergeInto(db); // reload from disk

            var r = new SignatureMatcher(db).Match(5011);
            Assert.True(r.IsMatch);
            Assert.Equal(("Mysterium", 1), (r.Best!.Material.Name, r.Best.RockCount));
            Assert.Equal(("Mysterium", 3), (new SignatureMatcher(db).Match(15033).Best!.Material.Name, new SignatureMatcher(db).Match(15033).Best!.RockCount));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Learned_value_for_a_known_ore_keeps_its_priority_and_does_not_duplicate_known_values()
    {
        var path = TempFile();
        try
        {
            var store = new LearnedSignatureStore(path);
            store.Add(new LearnedSignature { Name = "gold", ObservedRS = 3590, Count = 1 }); // e.g. value changed in a patch
            store.Add(new LearnedSignature { Name = "Whatever", ObservedRS = 3585, Count = 1 }); // already Gold: ignored
            var db = MaterialDatabaseLoader.LoadForVersion(TestData.DataRoot, "default");
            var before = db.Materials.Count;
            store.MergeInto(db);

            Assert.Equal(before + 1, db.Materials.Count);
            var learned = db.Materials.Last();
            Assert.Equal(("Gold", 3590, MaterialPriority.VeryHigh), (learned.Name, learned.BaseRS, learned.Priority));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(10022, 2, 5011)]
    [InlineData(51000, 17, 3000)]
    [InlineData(3585, 1, 3585)]
    public void Base_is_observed_divided_by_count(int rs, int count, int expected) =>
        Assert.Equal(expected, LearnedSignature.ComputeBase(rs, count));

    [Fact]
    public void Contribution_payload_has_values_and_plate_without_any_screen_data()
    {
        var s = new LearnedSignature { Name = "Ouratite (raw)", ObservedRS = 6740, Count = 2, BaseRS = 3370, GameVersion = "4.10.1", LearnedAt = new DateTime(2026, 10, 14, 13, 5, 9) };
        var files = ContributionPayload.Build(s, new byte[] { 1, 2, 3 }, "contributions", "main", "0.1.0");

        Assert.Equal(2, files.Count);
        Assert.Equal("contributions/20261014_130509_rs6740_ouratite-raw/signature.json", files[0].RepoPath);
        Assert.Equal("contributions/20261014_130509_rs6740_ouratite-raw/plate.png", files[1].RepoPath);

        using var body = JsonDocument.Parse(files[0].RequestBodyJson);
        Assert.Equal("main", body.RootElement.GetProperty("branch").GetString());
        var content = Encoding.UTF8.GetString(Convert.FromBase64String(body.RootElement.GetProperty("content").GetString()!));
        using var info = JsonDocument.Parse(content);
        Assert.Equal(3370, info.RootElement.GetProperty("baseRS").GetInt32());
        Assert.Equal("Ouratite (raw)", info.RootElement.GetProperty("name").GetString());

        Assert.Single(ContributionPayload.Build(s, null, "contributions", "main", "0.1.0")); // no image → values only
    }
}
