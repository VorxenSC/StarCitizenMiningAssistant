using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using MiningAssistant.Core;
using MiningAssistant.Core.Updates;

namespace MiningAssistant.App.Services;

/// <summary>
/// Automatic updates — the ONLY place in the app that touches the network, and only when the user turned
/// updates on. It downloads two public files from the downloads repository on github.com
/// (update.json and MiningAssistant.zip) and sends nothing about the user.
///
/// Flow:
///   1. Check: GET .../releases/latest/download/update.json; accept it only if its ECDSA signature is valid
///      (<see cref="UpdateVerifier"/>) and the version is newer.
///   2. Stage: download the zip into update\, check exact size + SHA-256, extract to update\staged\, check the
///      exe there is that version, then write update\staged\READY.json.
///   3. Apply (next start, or "Install now"): the old app starts update\staged\MiningAssistant.exe
///      --apply-update and exits. The NEW version waits until the old one is gone (single-instance mutex),
///      copies itself over the app folder (old files backed up to update\backup\, restored on any failure)
///      and starts the updated app. No scripts, no PowerShell, no installer.
/// </summary>
public static class Updater
{
    public const string MutexName = "StarCitizenMiningAssistant.SingleInstance";
    private static readonly string[] KeepFolders = { "config", "debug_snapshots", "diagnostics", "unknown_signatures", "update", "secrets" };

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("MiningAssistant-Updater/" + CurrentVersion);
        return c;
    }

    public static string CurrentVersion =>
        UpdateVerifier.Strip(FileVersionInfo.GetVersionInfo(Environment.ProcessPath ?? "").ProductVersion ?? "0.0.0");

    public static string AppDir => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    private static string UpdateDir => Path.Combine(AppDir, "update");
    private static string StagedDir => Path.Combine(UpdateDir, "staged");
    private static string ReadyFile => Path.Combine(StagedDir, "READY.json");

    // ───────────────────────── 1. check ─────────────────────────

    /// <summary>The signed manifest of the latest release, if it is newer than this app; otherwise null.</summary>
    public static async Task<UpdateManifest?> CheckAsync(string repository, CancellationToken ct = default)
    {
        var json = await Http.GetStringAsync($"https://github.com/{repository}/releases/latest/download/update.json", ct);
        var m = Json.Deserialize<UpdateManifest>(json);
        if (!UpdateVerifier.IsSignatureValid(m)) throw new InvalidDataException("update.json is not signed with the release key");
        return UpdateVerifier.IsNewer(m.Version, CurrentVersion) ? m : null;
    }

    // ───────────────────────── 2. stage ─────────────────────────

    /// <summary>Version already downloaded, verified and waiting to be installed (null if none / not newer).</summary>
    public static UpdateManifest? Staged()
    {
        try
        {
            if (!File.Exists(ReadyFile)) return null;
            var m = Json.Deserialize<UpdateManifest>(File.ReadAllText(ReadyFile));
            return UpdateVerifier.IsSignatureValid(m) && UpdateVerifier.IsNewer(m.Version, CurrentVersion)
                   && File.Exists(Path.Combine(StagedDir, "MiningAssistant.exe")) ? m : null;
        }
        catch { return null; }
    }

    public static async Task StageAsync(string repository, UpdateManifest m, CancellationToken ct = default)
    {
        Directory.CreateDirectory(UpdateDir);
        var zip = Path.Combine(UpdateDir, "download.zip");
        var url = $"https://github.com/{repository}/releases/download/v{m.Version}/{m.Asset}";

        using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            await using var src = await response.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zip);
            await src.CopyToAsync(dst, ct);
        }

        var size = new FileInfo(zip).Length;
        string sha;
        using (var fs = File.OpenRead(zip)) sha = UpdateVerifier.Sha256Hex(fs);
        if (size != m.Size || !string.Equals(sha, m.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(zip);
            throw new InvalidDataException("the downloaded file does not match the signed release (size or SHA-256)");
        }

        if (Directory.Exists(StagedDir)) Directory.Delete(StagedDir, true);
        ZipFile.ExtractToDirectory(zip, StagedDir);
        TryDelete(zip);

        var exe = Path.Combine(StagedDir, "MiningAssistant.exe");
        var staged = File.Exists(exe) ? UpdateVerifier.Strip(FileVersionInfo.GetVersionInfo(exe).ProductVersion ?? "") : "";
        if (staged != UpdateVerifier.Strip(m.Version))
        {
            Directory.Delete(StagedDir, true);
            throw new InvalidDataException($"the package says v{m.Version} but contains v{staged}");
        }
        Json.Save(ReadyFile, m);
    }

    // ───────────────────────── 3. apply ─────────────────────────

    /// <summary>Starts the staged new version in apply mode. The caller must then exit right away.</summary>
    public static bool LaunchApply()
    {
        var exe = Path.Combine(StagedDir, "MiningAssistant.exe");
        if (Staged() is null || !File.Exists(exe)) return false;
        Process.Start(new ProcessStartInfo(exe, $"--apply-update \"{AppDir}\"") { UseShellExecute = false, WorkingDirectory = StagedDir });
        return true;
    }

    /// <summary>
    /// Runs inside the NEW version (update\staged\MiningAssistant.exe --apply-update "&lt;app folder&gt;").
    /// Returns a message for the log; always starts an app (new on success, old after a rollback).
    /// </summary>
    public static string Apply(string targetDir, bool startAfter = true)
    {
        var source = AppDir;
        var backup = Path.Combine(targetDir, "update", "backup");
        var log = new List<string>();

        // Wait until the old app has exited (it holds the single-instance mutex).
        using (var mutex = new Mutex(false, MutexName))
        {
            bool owned;
            try { owned = mutex.WaitOne(TimeSpan.FromSeconds(60)); }
            catch (AbandonedMutexException) { owned = true; }
            if (owned) mutex.ReleaseMutex();
            else return "the old version did not close; update postponed";
        }

        var copied = new List<(string target, string? backedUp)>();
        try
        {
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(source, file);
                if (rel.Equals("READY.json", StringComparison.OrdinalIgnoreCase) || KeepFolders.Any(k => rel.StartsWith(k + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var target = Path.Combine(targetDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string? saved = null;
                if (File.Exists(target))
                {
                    saved = Path.Combine(backup, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                    Retry(() => File.Move(target, saved, true));
                }
                Retry(() => File.Copy(file, target, true));
                copied.Add((target, saved));
            }
            log.Add($"updated {copied.Count} files");
        }
        catch (Exception ex)
        {
            // Roll back: put every replaced file back, remove the new ones.
            foreach (var (target, saved) in Enumerable.Reverse(copied))
            {
                try
                {
                    if (saved is not null) File.Move(saved, target, true);
                    else File.Delete(target);
                }
                catch { /* best effort */ }
            }
            if (startAfter) StartApp(targetDir, "--update-failed");
            return "update failed and was rolled back: " + ex.Message;
        }

        if (startAfter) StartApp(targetDir, "--updated");
        return string.Join("; ", log);
    }

    /// <summary>After a successful update (started with --updated): remove the staging copy and the backup.</summary>
    public static void CleanUp()
    {
        TryDeleteDir(StagedDir);
        TryDeleteDir(Path.Combine(UpdateDir, "backup"));
    }

    private static void StartApp(string dir, string arg) =>
        Process.Start(new ProcessStartInfo(Path.Combine(dir, "MiningAssistant.exe"), arg) { UseShellExecute = false, WorkingDirectory = dir });

    /// <summary>Files can stay locked for a moment after the old process exits (antivirus scans, handles closing).</summary>
    private static void Retry(Action action)
    {
        for (var i = 0; ; i++)
        {
            try { action(); return; }
            catch (IOException) when (i < 40) { Thread.Sleep(500); }
            catch (UnauthorizedAccessException) when (i < 40) { Thread.Sleep(500); }
        }
    }

    private static void TryDelete(string file) { try { File.Delete(file); } catch { /* ignore */ } }
    private static void TryDeleteDir(string dir) { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* ignore */ } }
}
