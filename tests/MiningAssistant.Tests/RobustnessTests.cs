using MiningAssistant.Core;
using MiningAssistant.Core.Materials;
using MiningAssistant.Core.Settings;
using MiningAssistant.Core.Updates;

namespace MiningAssistant.Tests;

/// <summary>Inputs that used to crash or misbehave (found in the Ruflo review, Oct 2026).</summary>
public class RobustnessTests
{
    // ── update manifest ──

    [Theory]
    [InlineData("""{"version":"0.2.0","asset":"MiningAssistant.zip","size":10,"sha256":null,"signature":"AA=="}""")]
    [InlineData("""{"version":null,"asset":"MiningAssistant.zip","size":10,"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","signature":"AA=="}""")]
    [InlineData("""{"version":"0.2.0","asset":"../../evil.exe","size":10,"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","signature":"AA=="}""")]
    [InlineData("""{"version":"0.2.0","asset":"MiningAssistant.zip","size":10,"sha256":"zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz","signature":"AA=="}""")]
    [InlineData("""{"version":"0.2.0","asset":"MiningAssistant.zip","size":-1,"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","signature":"AA=="}""")]
    public void Malformed_manifests_are_rejected_without_crashing(string json)
    {
        var m = Json.Deserialize<UpdateManifest>(json);
        Assert.False(UpdateVerifier.IsSignatureValid(m));
    }

    [Theory]
    [InlineData("VorxenSC/MiningAssistant", true)]
    [InlineData("evil.com/x?y", false)]
    [InlineData("a/b/c", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_plain_owner_slash_name_repositories_are_used(string? repo, bool ok) =>
        Assert.Equal(ok, UpdateVerifier.IsRepositoryName(repo));

    // ── settings / learned signatures files ──

    [Fact]
    public void A_corrupted_settings_file_is_kept_aside_and_defaults_are_used()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ma-json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, "{ \"hotkeys\": { \"toggleOverlay\": \"PageUp\""); // truncated
            var s = Json.LoadOrCreate<AppSettings>(path);
            Assert.Equal("PageUp", s.Hotkeys.ToggleOverlay);             // defaults
            Assert.True(File.Exists(path + ".bad"));                    // the broken file is kept for inspection
            Assert.NotNull(Json.Deserialize<AppSettings>(File.ReadAllText(path))); // and a valid one was written

            var learned = Path.Combine(dir, "learned_signatures.json");
            File.WriteAllText(learned, "not json at all");
            Assert.Empty(new LearnedSignatureStore(learned).All);       // app still starts
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Settings_from_a_newer_app_version_are_not_touched()
    {
        var s = new AppSettings { ConfigVersion = AppSettings.CurrentConfigVersion + 5 };
        s.Hotkeys.DebugSnapshot = "\\";
        Assert.False(s.Migrate());
        Assert.Equal("\\", s.Hotkeys.DebugSnapshot);
    }

    // ── sharing ──

    private static LearnedSignature Sig(int rs, string name, string notes = "") => new()
    {
        Name = name, ObservedRS = rs, Count = 1, BaseRS = rs, Notes = notes, GameVersion = "4.10.1", LearnedAt = new DateTime(2026, 10, 19),
    };

    [Fact]
    public void One_huge_note_still_gives_a_usable_issue_link()
    {
        var url = SignatureShare.IssueUrl("VorxenSC/MiningAssistant", new[] { Sig(5000, "Odd rock", new string('x', 20_000)) }, "0.1.10", out var n);
        Assert.Equal(1, n);
        Assert.True(url.Length <= SignatureShare.MaxIssueUrlLength, $"{url.Length} chars");
    }

    [Fact]
    public void Discord_text_fits_one_message_and_says_how_many_were_included()
    {
        var items = Enumerable.Range(0, 200).Select(i => Sig(3000 + i, "Mineral " + i, "some notes here")).ToList();
        var text = SignatureShare.DiscordText(items, "0.1.10", out var shown);
        Assert.True(text.Length <= SignatureShare.MaxDiscordLength, $"{text.Length} chars");
        Assert.InRange(shown, 1, 199);
        Assert.Contains($"and {200 - shown} more", text);
    }

    [Fact]
    public void Shared_text_has_no_live_mentions_or_line_breaks()
    {
        var text = SignatureShare.DiscordText(new[] { Sig(5000, "@everyone", "line1\nline2 @here") }, "0.1.10");
        Assert.DoesNotContain("@everyone", text, StringComparison.Ordinal); // "@​everyone" is shown but does not ping
        Assert.DoesNotContain("@here", text, StringComparison.Ordinal);
        Assert.Single(text.Split('\n').Where(l => l.StartsWith("•")));
    }
}
