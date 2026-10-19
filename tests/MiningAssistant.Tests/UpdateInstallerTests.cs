using MiningAssistant.Core;
using MiningAssistant.Core.Updates;

namespace MiningAssistant.Tests;

/// <summary>
/// The install step of automatic updates, on real temporary folders: it must end all-new or all-old,
/// whatever fails half-way (locked file, crash, power cut).
/// </summary>
public sealed class UpdateInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ma-installer-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "staged");
    private string Target => Path.Combine(_root, "app");
    private static readonly Action<TimeSpan> NoSleep = _ => { };

    public UpdateInstallerTests()
    {
        Write(Target, "MiningAssistant.exe", "old exe");
        Write(Target, "Core.dll", "old core");
        Write(Target, "data/default/materials.json", "old db");
        Write(Target, "config/settings.json", "user settings");
        Write(Target, "only-in-old.dll", "old extra");

        Write(Source, "MiningAssistant.exe", "new exe");
        Write(Source, "Core.dll", "new core");
        Write(Source, "data/default/materials.json", "new db");
        Write(Source, "brand-new.dll", "new file");
        Write(Source, "config/settings.json", "shipped defaults must not overwrite the user's");
        Write(Source, "READY.json", "{}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private static void Write(string dir, string rel, string text)
    {
        var path = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private string Read(string rel) => File.ReadAllText(Path.Combine(Target, rel));
    private bool Exists(string rel) => File.Exists(Path.Combine(Target, rel));

    private void AssertOldInstall()
    {
        Assert.Equal("old exe", Read("MiningAssistant.exe"));
        Assert.Equal("old core", Read("Core.dll"));
        Assert.Equal("old db", Read("data/default/materials.json"));
        Assert.Equal("old extra", Read("only-in-old.dll"));
        Assert.False(Exists("brand-new.dll"));
        Assert.Equal("user settings", Read("config/settings.json"));
        Assert.Empty(Directory.EnumerateFiles(Target, "*.new", SearchOption.AllDirectories));
        Assert.False(File.Exists(UpdateInstaller.JournalFile(Target)));
    }

    [Fact]
    public void Success_installs_every_new_file_and_keeps_user_folders()
    {
        var n = UpdateInstaller.Install(Source, Target, "0.2.0", sleep: NoSleep);

        Assert.Equal(4, n); // exe, Core.dll, materials.json, brand-new.dll — not READY.json, not config/
        Assert.Equal("new exe", Read("MiningAssistant.exe"));
        Assert.Equal("new core", Read("Core.dll"));
        Assert.Equal("new db", Read("data/default/materials.json"));
        Assert.Equal("new file", Read("brand-new.dll"));
        Assert.Equal("user settings", Read("config/settings.json"));
        Assert.False(Exists("READY.json"));
        Assert.False(File.Exists(UpdateInstaller.JournalFile(Target)));
        Assert.Empty(Directory.EnumerateFiles(Target, "*.new", SearchOption.AllDirectories));
    }

    [Fact]
    public void The_exe_is_installed_last()
    {
        var order = UpdateInstaller.FilesToInstall(Source);
        Assert.Equal("MiningAssistant.exe", order[^1]);
        Assert.DoesNotContain("READY.json", order);
        Assert.DoesNotContain(order, f => f.StartsWith("config"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)] // the exe itself
    public void A_copy_that_fails_half_way_restores_the_old_install(int failOnCopy)
    {
        // The Nth file can never be copied (e.g. the disk is full): retries do not help.
        var order = UpdateInstaller.FilesToInstall(Source);
        var broken = Path.Combine(Source, order[failOnCopy - 1]);
        void FlakyCopy(string from, string to)
        {
            if (from == broken) throw new IOException("disk full");
            File.Copy(from, to, true);
        }

        Assert.Throws<IOException>(() => UpdateInstaller.Install(Source, Target, "0.2.0", FlakyCopy, NoSleep));
        AssertOldInstall(); // including the file that had been moved to the backup when its copy failed
    }

    [Fact]
    public void A_partial_copy_never_leaves_a_half_written_file()
    {
        void PartialCopy(string from, string to)
        {
            File.WriteAllText(to, "half"); // writes something, then dies
            throw new IOException("lost the handle");
        }

        Assert.Throws<IOException>(() => UpdateInstaller.Install(Source, Target, "0.2.0", PartialCopy, NoSleep));
        AssertOldInstall();
    }

[Fact]
    public void After_a_power_cut_the_next_start_rolls_back_to_the_old_version()
    {
        // State left by a process that died half-way: journal written, Core.dll already swapped
        // (old one in the backup), brand-new.dll already added, the rest untouched.
        Json.Save(UpdateInstaller.JournalFile(Target), new UpdateInstaller.Journal
        {
            Version = "0.2.0",
            Files = UpdateInstaller.FilesToInstall(Source)
                .Select(rel => new UpdateInstaller.JournalEntry { Path = rel, HadOriginal = File.Exists(Path.Combine(Target, rel)) })
                .ToList(),
        });
        Write(UpdateInstaller.BackupDir(Target), "Core.dll", "old core");
        Write(Target, "Core.dll", "new core");
        Write(Target, "brand-new.dll", "new file");
        Write(Target, "data/default/materials.json.new", "half-written");

        Assert.True(UpdateInstaller.RecoverIfInterrupted(Target, NoSleep));
        AssertOldInstall();
        Assert.False(UpdateInstaller.RecoverIfInterrupted(Target, NoSleep)); // nothing left to recover
    }
    [Fact]
    public void Locked_files_are_retried_but_missing_files_fail_at_once()
    {
        var tries = 0;
        UpdateInstaller.Retry(() => { if (++tries < 3) throw new IOException("in use"); }, NoSleep);
        Assert.Equal(3, tries);

        tries = 0;
        Assert.Throws<FileNotFoundException>(() => UpdateInstaller.Retry(() => { tries++; throw new FileNotFoundException(); }, NoSleep));
        Assert.Equal(1, tries);

        tries = 0;
        Assert.Throws<IOException>(() => UpdateInstaller.Retry(() => { tries++; throw new IOException("stuck"); }, NoSleep, attempts: 5));
        Assert.Equal(5, tries);
    }
}
