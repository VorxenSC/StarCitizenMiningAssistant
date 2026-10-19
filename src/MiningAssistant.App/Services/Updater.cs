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
    private const long MaxManifestBytes = 16 * 1024;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10), MaxResponseContentBufferSize = MaxManifestBytes };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("MiningAssistant-Updater/" + CurrentVersion);
        return c;
    }

    public static string CurrentVersion =>
        UpdateVerifier.Strip(FileVersionInfo.GetVersionInfo(Environment.ProcessPath ?? "").ProductVersion ?? "0.0.0");

    public static string AppDir => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    private static string UpdateDir => Path.Combine(AppDir, "update");
    private static string StagedDir => Path.Combine(UpdateDir, "staged");
    private static string ReadyFile => Path.Combine(StagedDir, "READY.json");
    private static string FailedFile => Path.Combine(UpdateDir, "failed.json");

    // ───────────────────────── 1. check ─────────────────────────

    /// <summary>The signed manifest of the latest release, if it is newer than this app; otherwise null.</summary>
    public static async Task<UpdateManifest?> CheckAsync(string repository, CancellationToken ct = default)
    {
        if (!UpdateVerifier.IsRepositoryName(repository)) throw new ArgumentException($"invalid update repository \"{repository}\" in settings");
        var json = await Http.GetStringAsync($"https://github.com/{repository}/releases/latest/download/update.json", ct);
        var m = Json.Deserialize<UpdateManifest>(json);
        if (!UpdateVerifier.IsSignatureValid(m)) throw new InvalidDataException("update.json is not signed with the release key");

        // Replay guard: never accept a manifest older than the newest one already seen on this PC
        // (someone serving an old, still validly signed release to hold players back).
        var seenFile = Path.Combine(UpdateDir, "seen.json");
        DateTime? seen = null;
        try { if (File.Exists(seenFile)) seen = Json.Deserialize<UpdateManifest>(File.ReadAllText(seenFile)).IssuedAt; } catch { /* unreadable: ignore */ }
        if (seen is { } s && m.IssuedAt < s) throw new InvalidDataException("update.json is older than one already seen (possible replay)");
        if (seen is null || m.IssuedAt > seen) Json.Save(seenFile, new UpdateManifest { Version = m.Version, IssuedAt = m.IssuedAt });

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
            var exe = Path.Combine(StagedDir, UpdateInstaller.ExeName);
            return UpdateVerifier.IsSignatureValid(m) && UpdateVerifier.IsNewer(m.Version, CurrentVersion) && File.Exists(exe)
                   && FailedVersion() != m.Version // failed once: wait for "Install update now" or a new download
                   && UpdateVerifier.Strip(FileVersionInfo.GetVersionInfo(exe).ProductVersion ?? "") == UpdateVerifier.Strip(m.Version) ? m : null;
        }
        catch { return null; }
    }

    /// <summary>The version whose installation failed last time (it is not retried automatically).</summary>
    public static string? FailedVersion()
    {
        try { return File.Exists(FailedFile) ? Json.Deserialize<UpdateManifest>(File.ReadAllText(FailedFile)).Version : null; }
        catch { return null; }
    }

    public static void ClearFailed() => TryDelete(FailedFile);

    public static async Task StageAsync(string repository, UpdateManifest m, CancellationToken ct = default)
    {
        if (!UpdateVerifier.IsRepositoryName(repository) || !UpdateVerifier.IsSignatureValid(m)) throw new InvalidDataException("refusing an unsigned or malformed update");
        Directory.CreateDirectory(UpdateDir);
        var zip = Path.Combine(UpdateDir, "download.zip");
        var url = $"https://github.com/{repository}/releases/download/v{m.Version}/{m.Asset}";

        using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long announced && announced != m.Size)
                throw new InvalidDataException($"the server announces {announced:N0} bytes, the signed release says {m.Size:N0}");
            await using var src = await response.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zip);
            // Never write more than the signed size: a bad server cannot fill the disk.
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (total > m.Size) throw new InvalidDataException("the download is larger than the signed release");
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            }
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
        ClearFailed(); // a fresh download gets a fresh chance
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
        var exe = Path.Combine(StagedDir, UpdateInstaller.ExeName);
        if (Staged() is null || !File.Exists(exe)) return false;
        Process.Start(new ProcessStartInfo(exe, $"--apply-update \"{AppDir}\"") { UseShellExecute = false, WorkingDirectory = StagedDir });
        return true;
    }

    /// <summary>
    /// Runs inside the NEW version (update\staged\MiningAssistant.exe --apply-update "&lt;app folder&gt;").
    /// Holds the single-instance mutex for the whole copy, so the app cannot be started (or a second
    /// install launched) half-way. Returns a message for the log and starts the resulting app.
    /// </summary>
    public static string Apply(string targetDir, bool startAfter = true)
    {
        var source = AppDir;
        if (!File.Exists(Path.Combine(source, "READY.json")))
            return "refused: --apply-update only runs from a verified update\\staged folder";
        var version = CurrentVersion;

        using var mutex = new Mutex(false, MutexName);
        bool owned;
        try { owned = mutex.WaitOne(TimeSpan.FromSeconds(60)); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned) return "the old version did not close within 60 s; update postponed";

        string result;
        var ok = false;
        try
        {
            var count = UpdateInstaller.Install(source, targetDir, version);
            result = $"updated to v{version} ({count} files)";
            ok = true;
        }
        catch (Exception ex)
        {
            // Remember it, so the next start opens the old version normally instead of retrying forever.
            try { Json.Save(Path.Combine(targetDir, "update", "failed.json"), new UpdateManifest { Version = version, Notes = ex.Message }); }
            catch { /* the folder may be read-only */ }
            result = $"update to v{version} failed and was rolled back: {ex.Message}";
        }
        finally
        {
            mutex.ReleaseMutex();
        }

        if (startAfter) StartApp(targetDir, ok ? "--updated" : "--update-failed");
        return result;
    }

    /// <summary>After a successful update: remove the staging copy and the backup.</summary>
    public static void CleanUp()
    {
        TryDeleteDir(StagedDir);
        TryDeleteDir(UpdateInstaller.BackupDir(AppDir));
    }

    /// <summary>At start: an install interrupted by a crash or power cut is rolled back to the previous version.</summary>
    public static bool RecoverIfInterrupted()
    {
        try { return UpdateInstaller.RecoverIfInterrupted(AppDir); }
        catch { return false; }
    }

    private static void StartApp(string dir, string arg) =>
        Process.Start(new ProcessStartInfo(Path.Combine(dir, UpdateInstaller.ExeName), arg) { UseShellExecute = false, WorkingDirectory = dir });
    private static void TryDelete(string file) { try { File.Delete(file); } catch { /* ignore */ } }
    private static void TryDeleteDir(string dir) { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* ignore */ } }
}
