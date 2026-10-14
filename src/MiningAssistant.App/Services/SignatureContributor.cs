using System.Diagnostics;
using System.IO;
using System.Text;
using MiningAssistant.Core.Materials;
using MiningAssistant.Core.Settings;

namespace MiningAssistant.App.Services;

/// <summary>
/// Sends a learned signature (values + plate crop) to the project's GitHub repository, only when the user
/// asked for it. Designed not to look like anything Windows security tools would block:
///   • the app opens no network connection itself — the transfer is done by the official, digitally
///     signed GitHub CLI (gh.exe) that the user installed and logged in to;
///   • only HTTPS to api.github.com (GitHub "contents" API);
///   • no encoded scripts / base64 on any command line: request bodies are plain JSON files in the app's
///     own config folder, passed with "--input", and deleted afterwards;
///   • nothing is downloaded or executed, nothing runs in the background on its own, and no credentials
///     are stored by the app (gh keeps its token in Windows Credential Manager).
/// </summary>
public sealed class SignatureContributor
{
    private readonly string _workDir;

    public SignatureContributor(string workDir) => _workDir = workDir;

    /// <summary>Path of gh.exe, or null when the GitHub CLI is not installed.</summary>
    public static string? FindGh()
    {
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI", "gh.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "GitHub CLI", "gh.exe"),
        };
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d.Trim(), "gh.exe")));
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Is gh installed and logged in to github.com? (used for the panel checklist)</summary>
    public static async Task<(bool ok, string detail)> CheckAsync()
    {
        var gh = FindGh();
        if (gh is null) return (false, "GitHub CLI not installed (winget install GitHub.cli), then: gh auth login");
        var (code, _, err) = await RunAsync(gh, new[] { "auth", "status", "--hostname", "github.com" }, TimeSpan.FromSeconds(15));
        return code == 0 ? (true, "GitHub CLI logged in") : (false, "GitHub CLI not logged in — run: gh auth login" + (err.Length > 0 ? $" ({FirstLine(err)})" : ""));
    }

    /// <summary>Creates the contribution files in the repository. Returns the folder created, or an error.</summary>
    public async Task<(bool ok, string message, string? repoFolder)> SubmitAsync(
        LearnedSignature s, byte[]? platePng, ContributionSettings c, string appVersion, CancellationToken ct = default)
    {
        var gh = FindGh();
        if (gh is null) return (false, "GitHub CLI not installed", null);

        var files = ContributionPayload.Build(s, platePng, c.Folder, c.Branch, appVersion);
        Directory.CreateDirectory(_workDir);
        foreach (var f in files)
        {
            var bodyFile = Path.Combine(_workDir, $"upload-{Guid.NewGuid():N}.json");
            try
            {
                await File.WriteAllTextAsync(bodyFile, f.RequestBodyJson, new UTF8Encoding(false), ct);
                var (code, _, err) = await RunAsync(gh,
                    new[] { "api", "--method", "PUT", $"repos/{c.Repository}/contents/{f.RepoPath}", "--input", bodyFile },
                    TimeSpan.FromSeconds(45), ct);
                if (code != 0) return (false, $"GitHub refused {Path.GetFileName(f.RepoPath)}: {FirstLine(err)}", null);
            }
            finally
            {
                try { File.Delete(bodyFile); } catch { /* best effort */ }
            }
        }
        var folder = files[0].RepoPath[..files[0].RepoPath.LastIndexOf('/')];
        return (true, "sent to " + c.Repository, folder);
    }

    private static string FirstLine(string s) => s.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";

    private static async Task<(int code, string stdout, string stderr)> RunAsync(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a); // no shell, no quoting tricks
        psi.Environment["GH_PROMPT_DISABLED"] = "1";      // never wait for interactive input
        psi.Environment["NO_COLOR"] = "1";

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start gh");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return (-1, "", "timed out");
        }
        return (p.ExitCode, await stdout, await stderr);
    }
}
