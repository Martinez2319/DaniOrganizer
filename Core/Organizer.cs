using System.Security.Cryptography;
using System.Text.Json;

namespace DaniOrganizer.Core;

public enum Grouping { Type, Date, TypeAndDate }
public record Options(string Source, string Destination, Grouping Grouping, bool Recursive);
public record PlannedMove(string Source, string Destination, long Length, DateTime ModifiedUtc);
public record Plan(Options Options, IReadOnlyList<PlannedMove> Moves, IReadOnlyList<string> Warnings);
public record DuplicateSet(string Hash, long Length, IReadOnlyList<string> Files);
public sealed class Entry
{
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
    public string Hash { get; set; } = "";
    public string State { get; set; } = "Pending";
}
public sealed class Operation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public List<Entry> Entries { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}

public static class Organizer
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static string DefaultJournal => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DaniOrganizer", "operations");
    public static bool Within(string path, string directory) =>
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool Same(string a, string b) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    public static Plan Preview(Options options, CancellationToken token = default)
    {
        options = options with { Source = Path.GetFullPath(options.Source), Destination = Path.GetFullPath(options.Destination) };
        if (!Directory.Exists(options.Source)) throw new DirectoryNotFoundException("La carpeta de origen no existe.");
        if (Same(options.Source, options.Destination) || Within(options.Source, options.Destination))
            throw new ArgumentException("El destino debe ser una subcarpeta nueva o una carpeta separada, nunca el origen ni una carpeta que lo contenga.");
        RejectLinkedAncestors(options.Source); RejectLinkedAncestors(options.Destination);
        var warnings = new List<string>(); var moves = new List<PlannedMove>();
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Enumerate(options, warnings, token).Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var file = new FileInfo(path);
                string category = Category(file.Extension);
                string date = file.LastWriteTime.ToString("yyyy-MM");
                string folder = options.Grouping switch { Grouping.Date => date, Grouping.TypeAndDate => Path.Combine(category, date), _ => category };
                string target = Path.Combine(options.Destination, folder, file.Name);
                int suffix = 1;
                while (File.Exists(target) || Directory.Exists(target) || !reserved.Add(target))
                    target = Path.Combine(options.Destination, folder, $"{Path.GetFileNameWithoutExtension(file.Name)} ({suffix++}){file.Extension}");
                moves.Add(new(path, target, file.Length, file.LastWriteTimeUtc));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warnings.Add(path + ": " + ex.Message); }
        }
        return new(options, moves, warnings);
    }

    public static string Category(string extension) => extension.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".svg" or ".heic" or ".tif" or ".tiff" => "Imagenes",
        ".pdf" or ".doc" or ".docx" or ".txt" or ".md" or ".odt" or ".rtf" => "Documentos",
        ".xls" or ".xlsx" or ".csv" or ".ods" => "Hojas de calculo",
        ".ppt" or ".pptx" or ".odp" => "Presentaciones",
        ".mp4" or ".mkv" or ".avi" or ".mov" or ".webm" => "Videos",
        ".mp3" or ".wav" or ".flac" or ".ogg" or ".m4a" => "Audio",
        ".zip" or ".rar" or ".7z" or ".gz" or ".tar" => "Comprimidos",
        ".exe" or ".msi" or ".msix" => "Instaladores",
        ".cs" or ".py" or ".js" or ".ts" or ".html" or ".css" or ".json" or ".xml" or ".yml" or ".yaml" or ".sql" => "Codigo",
        "" => "Sin extension", _ => "Otros"
    };

    private static IEnumerable<string> Enumerate(Options options, List<string> warnings, CancellationToken token)
    {
        var pending = new Stack<string>(); pending.Push(Path.GetFullPath(options.Source));
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested(); string directory = pending.Pop();
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warnings.Add(directory + ": " + ex.Message); continue; }
            foreach (var path in entries)
            {
                token.ThrowIfCancellationRequested();
                FileAttributes attributes;
                try { attributes = File.GetAttributes(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warnings.Add(path + ": " + ex.Message); continue; }
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) != 0) { warnings.Add("Omitido (enlace, oculto o sistema): " + path); continue; }
                if (Same(path, options.Destination) || Within(path, options.Destination)) continue;
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    if (new[] { ".git", ".svn", ".hg", "node_modules", ".venv", "venv", "bin", "obj" }.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
                    { warnings.Add("Carpeta de desarrollo omitida: " + path); continue; }
                    if (options.Recursive) pending.Push(path);
                }
                else yield return path;
            }
        }
    }

    public static string Fingerprint(string path, CancellationToken token = default)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024]; int count;
        while ((count = file.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, count); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static IReadOnlyList<DuplicateSet> Duplicates(Options options, List<string> warnings, CancellationToken token = default)
    {
        RejectLinkedAncestors(options.Source);
        var sizes = new Dictionary<long, List<string>>();
        foreach (var path in Enumerate(options, warnings, token))
        {
            try { long size = new FileInfo(path).Length; if (!sizes.TryGetValue(size, out var files)) sizes[size] = files = new(); files.Add(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warnings.Add(path + ": " + ex.Message); }
        }
        var result = new List<DuplicateSet>();
        foreach (var group in sizes.Where(pair => pair.Value.Count > 1))
        {
            var hashes = new Dictionary<string, List<string>>();
            foreach (var path in group.Value)
            {
                token.ThrowIfCancellationRequested();
                try { string hash = Fingerprint(path, token); if (!hashes.TryGetValue(hash, out var files)) hashes[hash] = files = new(); files.Add(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warnings.Add(path + ": " + ex.Message); }
            }
            foreach (var pair in hashes.Where(pair => pair.Value.Count > 1)) result.Add(new(pair.Key, group.Key, pair.Value));
        }
        return result.OrderByDescending(set => set.Length * (set.Files.Count - 1)).ToArray();
    }

    public static Operation Apply(Plan plan, string journalDirectory, IProgress<string>? progress = null, CancellationToken token = default)
    {
        // Validate the complete preview before the first move.
        foreach (var move in plan.Moves)
        {
            token.ThrowIfCancellationRequested(); Validate(move);
        }
        var operation = new Operation(); Save(operation, journalDirectory);
        foreach (var move in plan.Moves)
        {
            if (token.IsCancellationRequested) { operation.Errors.Add("Organización cancelada; se pueden deshacer los movimientos completados."); break; }
            try
            {
                Validate(move);
                string hash = Fingerprint(move.Source, token);
                Validate(move);
                var entry = new Entry { Source = move.Source, Destination = move.Destination, Hash = hash };
                operation.Entries.Add(entry); Save(operation, journalDirectory);
                Directory.CreateDirectory(Path.GetDirectoryName(move.Destination)!);
                RejectLinkedAncestors(move.Destination);
                File.Move(move.Source, move.Destination, overwrite: false);
                entry.State = "Moved"; Save(operation, journalDirectory);
                progress?.Report(Path.GetFileName(move.Source));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { operation.Errors.Add(move.Source + ": " + ex.Message); break; }
        }
        Save(operation, journalDirectory); return operation;
    }

    private static void Validate(PlannedMove move)
    {
        RejectLinkedAncestors(move.Source); RejectLinkedAncestors(move.Destination);
        var file = new FileInfo(move.Source);
        if (!file.Exists || file.Length != move.Length || file.LastWriteTimeUtc != move.ModifiedUtc)
            throw new IOException("El archivo cambió desde la vista previa: " + move.Source + ". Genera una nueva vista previa.");
        if (File.Exists(move.Destination) || Directory.Exists(move.Destination)) throw new IOException("El destino ya existe: " + move.Destination + ". Genera una nueva vista previa.");
    }
    private static void RejectLinkedAncestors(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("No se admiten enlaces ni carpetas redirigidas: " + current);
    }

    public static Operation Undo(Operation operation, string journalDirectory, IProgress<string>? progress = null, CancellationToken token = default)
    {
        operation.Errors.Clear();
        foreach (var entry in operation.Entries.AsEnumerable().Reverse())
        {
            if (token.IsCancellationRequested) break;
            if (entry.State == "Undone") continue;
            // Pending can mean the application stopped between moving and recording success.
            try
            {
                RejectLinkedAncestors(entry.Source); RejectLinkedAncestors(entry.Destination);
                if (File.Exists(entry.Source) && !File.Exists(entry.Destination) && Fingerprint(entry.Source, token) == entry.Hash)
                { entry.State = "Undone"; Save(operation, journalDirectory); continue; }
                if (File.Exists(entry.Source) || Directory.Exists(entry.Source)) throw new IOException("El origen está ocupado; se conserva el archivo organizado.");
                if (!File.Exists(entry.Destination) || Fingerprint(entry.Destination, token) != entry.Hash) throw new IOException("El archivo organizado cambió o ya no existe; se omite.");
                Directory.CreateDirectory(Path.GetDirectoryName(entry.Source)!);
                File.Move(entry.Destination, entry.Source, overwrite: false);
                entry.State = "Undone"; Save(operation, journalDirectory); progress?.Report(Path.GetFileName(entry.Source));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { operation.Errors.Add(entry.Destination + ": " + ex.Message); }
        }
        Save(operation, journalDirectory); return operation;
    }

    public static IReadOnlyList<Operation> History(string directory)
    {
        if (!Directory.Exists(directory)) return Array.Empty<Operation>();
        string[] paths;
        try { paths = Directory.GetFiles(directory, "*.json"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<Operation>(); }
        return paths.Select(path =>
        { try { return JsonSerializer.Deserialize<Operation>(File.ReadAllText(path)); } catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; } })
            .Where(item => item != null && Guid.TryParseExact(item.Id, "N", out _) && item.Entries != null && item.Errors != null &&
                item.Entries.All(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Source) && !string.IsNullOrWhiteSpace(entry.Destination) &&
                    Path.IsPathFullyQualified(entry.Source) && Path.IsPathFullyQualified(entry.Destination) && entry.Hash?.Length == 64 && entry.Hash.All(Uri.IsHexDigit) && entry.State is "Pending" or "Moved" or "Undone"))
            .Cast<Operation>().OrderByDescending(item => item.CreatedUtc).ToArray();
    }
    private static void Save(Operation operation, string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, operation.Id + ".json"); string temporary = path + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { JsonSerializer.Serialize(file, operation, Json); file.Flush(flushToDisk: true); }
        File.Move(temporary, path, overwrite: true);
    }
}
