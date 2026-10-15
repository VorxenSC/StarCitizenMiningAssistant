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
    public void Discord_text_and_github_issue_carry_only_the_typed_values()
    {
        var s = new LearnedSignature { Name = "Ouratite (raw)", ObservedRS = 6740, Count = 2, BaseRS = 3370, Kind = SignalKind.Ore,
            Notes = "near | Yela", GameVersion = "4.10.1", LearnedAt = new DateTime(2026, 10, 14, 13, 5, 9), PlateImage = @"C:\secret\path\plate.png" };
        var items = new[] { s };

        var text = SignatureShare.DiscordText(items, "0.1.8");
        Assert.Contains("RS 6,740 = Ouratite (raw) ×2 (base 3,370, Ore)", text);
        Assert.Contains("4.10.1", text);
        Assert.DoesNotContain(@"C:\secret", text); // local paths never leave the PC

        var url = SignatureShare.IssueUrl("VorxenSC/MiningAssistant", items, "0.1.8", out var included);
        Assert.Equal(1, included);
        Assert.StartsWith("https://github.com/VorxenSC/MiningAssistant/issues/new?title=", url);
        var body = Uri.UnescapeDataString(url[(url.IndexOf("&body=", StringComparison.Ordinal) + 6)..]);
        Assert.Contains("| 6,740 | Ouratite (raw) | 2 | 3,370 | Ore | 4.10.1 | 2026-10-14 | near / Yela |", body); // pipes escaped
        Assert.DoesNotContain(@"C:\secret", body);
    }

    [Fact]
    public void A_long_list_is_split_so_the_issue_link_stays_usable()
    {
        var items = Enumerable.Range(1, 400).Select(i => new LearnedSignature
        {
            Name = "Mineral number " + i, ObservedRS = 3000 + i, Count = 1, BaseRS = 3000 + i, Notes = new string('x', 40),
            GameVersion = "4.10.1", LearnedAt = new DateTime(2026, 10, 15),
        }).ToList();

        var url = SignatureShare.IssueUrl("VorxenSC/MiningAssistant", items, "0.1.8", out var included);
        Assert.InRange(included, 1, 399);
        Assert.True(url.Length <= SignatureShare.MaxIssueUrlLength);
    }
}