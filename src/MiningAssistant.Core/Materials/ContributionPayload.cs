using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MiningAssistant.Core.Materials;

/// <summary>One file to create in the repository through the GitHub "contents" API.</summary>
public sealed record RepoFile(string RepoPath, string RequestBodyJson);

/// <summary>
/// Builds the request bodies that put a learned signature into the repository:
///   contributions/YYYYMMDD_HHMMSS_rs{RS}_{name}/signature.json  (values, game version, context)
///   contributions/.../plate.png                                (crop of the RS plate only)
/// Pure (no I/O), so it is unit-tested; the app hands the bodies to the GitHub CLI as files.
/// </summary>
public static class ContributionPayload
{
    public static IReadOnlyList<RepoFile> Build(LearnedSignature s, byte[]? platePng, string folder, string branch, string appVersion)
    {
        var dir = $"{folder.Trim('/')}/{s.LearnedAt:yyyyMMdd_HHmmss}_rs{s.ObservedRS}_{Slug(s.Name)}";
        var files = new List<RepoFile>();

        var info = new
        {
            name = s.Name,
            kind = s.Kind.ToString(),
            observedRS = s.ObservedRS,
            count = s.Count,
            baseRS = s.BaseRS,
            notes = s.Notes,
            gameVersion = s.GameVersion,
            learnedAt = s.LearnedAt,
            appVersion,
        };
        var json = JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true });
        files.Add(new RepoFile($"{dir}/signature.json", Body($"Learned signature: RS {s.ObservedRS} = {s.Name} ×{s.Count}", Encoding.UTF8.GetBytes(json), branch)));

        if (platePng is { Length: > 0 })
            files.Add(new RepoFile($"{dir}/plate.png", Body($"Plate image for RS {s.ObservedRS}", platePng, branch)));

        return files;
    }

    private static string Body(string message, byte[] content, string branch) =>
        JsonSerializer.Serialize(new { message, content = Convert.ToBase64String(content), branch });

    /// <summary>Safe folder-name fragment: "Ouratite (raw)" → "ouratite-raw".</summary>
    public static string Slug(string name)
    {
        var s = Regex.Replace(name.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return s.Length == 0 ? "unnamed" : s.Length > 40 ? s[..40] : s;
    }
}
