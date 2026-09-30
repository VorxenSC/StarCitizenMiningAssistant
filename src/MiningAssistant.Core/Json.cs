using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiningAssistant.Core;

/// <summary>Shared JSON settings: camelCase, enums as strings, comments/trailing commas allowed (user-edited files).</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true,
        };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidDataException($"Empty JSON for {typeof(T).Name}");

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Loads a JSON file, or creates it with defaults when it does not exist.</summary>
    public static T LoadOrCreate<T>(string path) where T : new()
    {
        if (File.Exists(path))
            return Deserialize<T>(File.ReadAllText(path));

        var value = new T();
        Save(path, value);
        return value;
    }

    public static void Save<T>(string path, T value)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // Write to temp then move, so a crash never leaves a half-written config.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, Serialize(value));
        File.Move(tmp, path, overwrite: true);
    }
}
