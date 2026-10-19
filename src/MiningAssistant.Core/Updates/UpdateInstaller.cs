namespace MiningAssistant.Core.Updates;

/// <summary>
/// Copies a staged version over the installed one so that any failure — a locked file, a full disk, a
/// power cut half-way — ends in a consistent install (all new, or all old again):
///   1. a journal (update\applying.json) listing every file and whether it already existed is written first;
///   2. each existing file is moved to update\backup\ before its replacement is copied in (via a .new file
///      and a rename, so a file is never left half-written); MiningAssistant.exe goes last;
///   3. the journal is deleted only when everything is in place.
/// On an error the journal drives <see cref="Rollback"/>; after a crash, the next start finds the
/// journal and calls <see cref="RecoverIfInterrupted"/>. Pure file logic (no processes, no network), so it is unit-tested.
/// </summary>
public static class UpdateInstaller
{
    public const string ExeName = "MiningAssistant.exe";

    /// <summary>Folders of the install that belong to the user and are never overwritten.</summary>
    public static readonly string[] KeepFolders = { "config", "debug_snapshots", "diagnostics", "unknown_signatures", "update", "secrets" };

    public sealed class Journal
    {
        public string Version { get; set; } = "";
        public DateTime StartedAt { get; set; }
        public List<JournalEntry> Files { get; set; } = new();
    }

    public sealed class JournalEntry
    {
        public string Path { get; set; } = "";
        public bool HadOriginal { get; set; }
    }

    public static string JournalFile(string target) => Path.Combine(target, "update", "applying.json");
    public static string BackupDir(string target) => Path.Combine(target, "update", "backup");

    /// <summary>Files of the new version to install, relative to <paramref name="source"/>; the exe comes last.</summary>
    public static List<string> FilesToInstall(string source) =>
        Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(source, f))
            .Where(rel => !rel.Equals("READY.json", StringComparison.OrdinalIgnoreCase)
                          && !rel.EndsWith(".new", StringComparison.OrdinalIgnoreCase)
                          && !KeepFolders.Any(k => rel.StartsWith(k + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(rel => rel.Equals(ExeName, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(rel => rel, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Installs <paramref name="source"/> over <paramref name="target"/>. Throws after rolling back when
    /// anything fails. <paramref name="copy"/> and <paramref name="sleep"/> are replaceable for tests.
    /// </summary>
    public static int Install(string source, string target, string version,
        Action<string, string>? copy = null, Action<TimeSpan>? sleep = null)
    {
        copy ??= (from, to) => File.Copy(from, to, overwrite: true);
        sleep ??= Thread.Sleep;

        RecoverIfInterrupted(target, sleep); // a previous attempt that never finished
        var backup = BackupDir(target);
        if (Directory.Exists(backup)) Directory.Delete(backup, true);

        var files = FilesToInstall(source);
        var journal = new Journal
        {
            Version = version,
            StartedAt = DateTime.Now,
            Files = files.Select(rel => new JournalEntry { Path = rel, HadOriginal = File.Exists(Path.Combine(target, rel)) }).ToList(),
        };
        Json.Save(JournalFile(target), journal); // before touching anything

        try
        {
            foreach (var entry in journal.Files)
            {
                var dest = Path.Combine(target, entry.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if (entry.HadOriginal)
                {
                    var saved = Path.Combine(backup, entry.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                    Retry(() => File.Move(dest, saved, overwrite: true), sleep);
                }
                var temp = dest + ".new";
                Retry(() => copy(Path.Combine(source, entry.Path), temp), sleep);
                Retry(() => File.Move(temp, dest, overwrite: true), sleep);
            }
        }
        catch
        {
            Rollback(target, journal, sleep);
            throw;
        }

        File.Delete(JournalFile(target));
        return files.Count;
    }

    /// <summary>Puts every backed-up file back, removes files that did not exist before, deletes leftovers.</summary>
    public static void Rollback(string target, Journal journal, Action<TimeSpan>? sleep = null)
    {
        sleep ??= Thread.Sleep;
        var backup = BackupDir(target);
        var errors = new List<string>();
        foreach (var entry in Enumerable.Reverse(journal.Files))
        {
            var dest = Path.Combine(target, entry.Path);
            var saved = Path.Combine(backup, entry.Path);
            try
            {
                if (File.Exists(dest + ".new")) Retry(() => File.Delete(dest + ".new"), sleep);
                if (File.Exists(saved)) Retry(() => File.Move(saved, dest, overwrite: true), sleep);
                else if (!entry.HadOriginal && File.Exists(dest)) Retry(() => File.Delete(dest), sleep);
            }
            catch (Exception ex) { errors.Add($"{entry.Path}: {ex.Message}"); }
        }
        if (errors.Count == 0) TryDelete(JournalFile(target));
        else throw new IOException("rollback incomplete: " + string.Join("; ", errors.Take(3)));
    }

    /// <summary>A journal left behind means an install was interrupted: restore the previous version.</summary>
    public static bool RecoverIfInterrupted(string target, Action<TimeSpan>? sleep = null)
    {
        var file = JournalFile(target);
        if (!File.Exists(file)) return false;
        Journal journal;
        try { journal = Json.Deserialize<Journal>(File.ReadAllText(file)); }
        catch { TryDelete(file); return false; }
        Rollback(target, journal, sleep);
        return true;
    }

    /// <summary>
    /// Locked files (antivirus scan, a handle still closing) are retried for ~20 s. A missing file or folder
    /// is a real error and fails at once.
    /// </summary>
    public static void Retry(Action action, Action<TimeSpan>? sleep = null, int attempts = 40)
    {
        sleep ??= Thread.Sleep;
        for (var i = 1; ; i++)
        {
            try { action(); return; }
            catch (IOException ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException && i < attempts) { sleep(TimeSpan.FromMilliseconds(500)); }
            catch (UnauthorizedAccessException) when (i < attempts) { sleep(TimeSpan.FromMilliseconds(500)); }
        }
    }

    private static void TryDelete(string file) { try { File.Delete(file); } catch { /* ignore */ } }
}
