using MiningAssistant.Core.Matching;
using MiningAssistant.Core.Materials;

namespace MiningAssistant.Tests;

internal static class TestData
{
    public static string DataRoot => Path.Combine(AppContext.BaseDirectory, "data");

    private static readonly Lazy<MaterialDatabase> _db =
        new(() => MaterialDatabaseLoader.LoadForVersion(DataRoot, "default"));

    /// <summary>The real shipped materials.json, so tests catch bad edits to it.</summary>
    public static MaterialDatabase Database => _db.Value;

    public static SignatureMatcher Matcher(SignatureMatcherOptions? o = null) => new(Database, o);
}
