using System.Text.Json;

namespace DaniOrganizer.Core;

public static class ExclusionStore
{
    public static IReadOnlyList<string> Load(string path)
    {
        if (!File.Exists(path)) return Array.Empty<string>();
        var paths = JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) ?? Array.Empty<string>();
        return paths.Where(item => !string.IsNullOrWhiteSpace(item) && Path.IsPathFullyQualified(item))
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public static void Save(string path, IEnumerable<string> paths)
    {
        var normalized = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(normalized, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
    }
}
