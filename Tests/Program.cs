using DaniOrganizer.Core;
using System.Text.Json;

string scratch = Path.Combine(Path.GetTempPath(), "DaniOrganizer-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch); int passed = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Test(string name, Action<string> action)
{
    string path = Path.Combine(scratch, passed.ToString()); Directory.CreateDirectory(path);
    action(path); passed++; Console.WriteLine("PASS " + name);
}
string Write(string directory, string name, string content)
{ string path = Path.Combine(directory, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content); return path; }
Options Opt(string path, bool recursive = true, Grouping grouping = Grouping.Type) => new(path, Path.Combine(path, "_Organizado"), grouping, recursive);
void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
try
{
    Test("preview changes no files and categorizes extensions case-insensitively", path =>
    {
        string file = Write(path, "foto.JPG", "abc"); var plan = Organizer.Preview(Opt(path));
        Check(plan.Moves.Count == 1 && plan.Moves[0].Destination.EndsWith(Path.Combine("Imagenes", "foto.JPG")), "Wrong category");
        Check(File.Exists(file) && !Directory.Exists(Opt(path).Destination), "Preview changed files");
    });
    Test("colliding names receive deterministic suffixes", path =>
    {
        Write(path, "a/data.txt", "one"); Write(path, "b/data.txt", "two"); Write(path, "_Organizado/Documentos/data.txt", "existing");
        var plan = Organizer.Preview(Opt(path)); Check(plan.Moves.Count == 2, "Destination was scanned");
        Check(plan.Moves.Select(move => move.Destination).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2, "Collision");
        Check(plan.Moves.All(move => !move.Destination.EndsWith(Path.DirectorySeparatorChar + "data.txt")), "Existing destination reused");
    });
    Test("nonrecursive mode leaves nested files outside the plan", path =>
    { Write(path, "a.txt", "a"); Write(path, "nested/b.txt", "b"); Check(Organizer.Preview(Opt(path, false)).Moves.Count == 1, "Recursion ignored"); });
    Test("grouping uses local modification year and month", path =>
    {
        string file = Write(path, "a.pdf", "a"); File.SetLastWriteTime(file, new DateTime(2024, 3, 15));
        Check(Organizer.Preview(Opt(path, grouping: Grouping.Date)).Moves[0].Destination.EndsWith(Path.Combine("2024-03", "a.pdf")), "Wrong date");
        Check(Organizer.Preview(Opt(path, grouping: Grouping.TypeAndDate)).Moves[0].Destination.EndsWith(Path.Combine("Documentos", "2024-03", "a.pdf")), "Wrong grouping");
    });
    Test("apply persists history and undo restores original content", path =>
    {
        string file = Write(path, "a.txt", "original"); var plan = Organizer.Preview(Opt(path)); string journal = Path.Combine(scratch, "journal-basic");
        var operation = Organizer.Apply(plan, journal); Check(!File.Exists(file) && File.ReadAllText(plan.Moves[0].Destination) == "original", "Move failed");
        var restored = Organizer.History(journal).Single(); Organizer.Undo(restored, journal);
        Check(File.ReadAllText(file) == "original" && !File.Exists(plan.Moves[0].Destination), "Undo failed");
        Check(Organizer.History(journal).Single().Entries.All(entry => entry.State == "Undone"), "Undo not persisted");
    });
    Test("changed preview source stops all moves before execution", path =>
    {
        string a = Write(path, "a.txt", "a"), b = Write(path, "b.txt", "b"); var plan = Organizer.Preview(Opt(path)); File.WriteAllText(b, "changed");
        Throws<IOException>(() => Organizer.Apply(plan, Path.Combine(scratch, "journal-stale")));
        Check(File.Exists(a) && File.Exists(b), "Partial move despite stale preview");
    });
    Test("destination race never overwrites an existing file", path =>
    {
        string a = Write(path, "a.txt", "a"); var plan = Organizer.Preview(Opt(path)); string destination = plan.Moves[0].Destination;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.WriteAllText(destination, "do not overwrite");
        Throws<IOException>(() => Organizer.Apply(plan, Path.Combine(scratch, "journal-conflict")));
        Check(File.ReadAllText(destination) == "do not overwrite" && File.Exists(a), "Overwrote a file");
    });
    Test("undo refuses to overwrite a newly created original", path =>
    {
        string file = Write(path, "a.txt", "original"); string journal = Path.Combine(scratch, "journal-occupied"); var plan = Organizer.Preview(Opt(path)); var op = Organizer.Apply(plan, journal);
        File.WriteAllText(file, "new original"); Organizer.Undo(op, journal);
        Check(File.ReadAllText(file) == "new original" && File.Exists(plan.Moves[0].Destination) && op.Errors.Count == 1, "Undo overwrote a file");
    });
    Test("undo refuses to move a modified organized file", path =>
    {
        string file = Write(path, "a.txt", "original"); string journal = Path.Combine(scratch, "journal-modified"); var plan = Organizer.Preview(Opt(path)); var op = Organizer.Apply(plan, journal);
        File.WriteAllText(plan.Moves[0].Destination, "edited"); Organizer.Undo(op, journal);
        Check(!File.Exists(file) && File.ReadAllText(plan.Moves[0].Destination) == "edited" && op.Errors.Count == 1, "Modified file moved");
    });
    Test("undo recovers an interrupted pending move", path =>
    {
        Write(path, "a.txt", "original"); string journal = Path.Combine(scratch, "journal-pending"); var op = Organizer.Apply(Organizer.Preview(Opt(path)), journal);
        op.Entries[0].State = "Pending";
        File.WriteAllText(Path.Combine(journal, op.Id + ".json"), JsonSerializer.Serialize(op));
        Organizer.Undo(Organizer.History(journal).Single(), journal); Check(File.ReadAllText(Path.Combine(path, "a.txt")) == "original", "Pending recovery failed");
    });
    Test("duplicates compare content rather than names or sizes alone", path =>
    {
        Write(path, "a.txt", "same"); Write(path, "b.bin", "same"); Write(path, "c.txt", "diff");
        var groups = Organizer.Duplicates(Opt(path), new()); Check(groups.Count == 1 && groups[0].Files.Count == 2 && groups[0].Length == 4, "False duplicate");
        Check(Directory.GetFiles(path).Length == 3, "Duplicate scan changed files");
    });
    Test("hidden files and development metadata are excluded", path =>
    {
        string hidden = Write(path, "hidden.txt", "secret"); File.SetAttributes(hidden, FileAttributes.Hidden);
        Write(path, ".git/config", "config"); Write(path, "node_modules/pkg/index.js", "library"); Write(path, "visible.txt", "visible");
        var plan = Organizer.Preview(Opt(path)); Check(plan.Moves.Count == 1 && plan.Warnings.Count == 3, "Excluded files planned");
    });
    Test("source, equal destination and parent destination are rejected", path =>
    { Throws<ArgumentException>(() => Organizer.Preview(new(path, path, Grouping.Type, true))); Throws<ArgumentException>(() => Organizer.Preview(new(path, scratch, Grouping.Type, true))); });
    Test("external destination is supported", path =>
    { Write(path, "a.txt", "a"); string destination = Path.Combine(scratch, "external"); var plan = Organizer.Preview(new(path, destination, Grouping.Type, true)); Check(plan.Moves.Count == 1 && Organizer.Within(plan.Moves[0].Destination, destination), "External destination failed"); });
    Test("cancelled preview and apply leave files in place", path =>
    {
        string file = Write(path, "a.txt", "a"); var plan = Organizer.Preview(Opt(path)); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Throws<OperationCanceledException>(() => Organizer.Preview(Opt(path), cancel.Token));
        Throws<OperationCanceledException>(() => Organizer.Apply(plan, Path.Combine(scratch, "journal-cancel"), token: cancel.Token)); Check(File.Exists(file), "Cancelled operation moved files");
    });
    Test("corrupt history entry does not hide valid operations", path =>
    {
        Write(path, "a.txt", "a"); string journal = Path.Combine(scratch, "journal-corrupt"); Organizer.Apply(Organizer.Preview(Opt(path)), journal);
        Write(journal, "broken.json", "{broken"); Check(Organizer.History(journal).Count == 1, "Valid history lost");
    });
    Test("undo recognizes a restore interrupted before the journal update", path =>
    {
        string file = Write(path, "a.txt", "original"); string journal = Path.Combine(scratch, "journal-restored"); var op = Organizer.Apply(Organizer.Preview(Opt(path)), journal);
        File.Move(op.Entries[0].Destination, file); Organizer.Undo(op, journal);
        Check(op.Errors.Count == 0 && op.Entries[0].State == "Undone" && File.ReadAllText(file) == "original", "Interrupted restore not recognized");
    });
    Test("cancellation between moves keeps a usable undo journal", path =>
    {
        Write(path, "a.txt", "a"); Write(path, "b.txt", "b"); string journal = Path.Combine(scratch, "journal-partial"); using var cancellation = new CancellationTokenSource();
        var op = Organizer.Apply(Organizer.Preview(Opt(path)), journal, new ImmediateProgress(_ => cancellation.Cancel()), cancellation.Token);
        Check(op.Entries.Count == 1 && op.Errors.Count == 1, "Cancellation did not stop between moves");
        Organizer.Undo(Organizer.History(journal).Single(), journal);
        Check(File.ReadAllText(Path.Combine(path, "a.txt")) == "a" && File.ReadAllText(Path.Combine(path, "b.txt")) == "b", "Partial operation cannot undo");
    });
    Test("invalid structured history is ignored without crashing the application", path =>
    {
        Write(path, "invalid.json", "{\"Id\":\"" + Guid.NewGuid().ToString("N") + "\",\"Entries\":null}");
        Check(Organizer.History(path).Count == 0, "Invalid structured history was accepted");
    });
    Test("only selected files are validated, moved and recorded for undo", path =>
    {
        string a = Write(path, "a.txt", "a"), b = Write(path, "b.txt", "b"); var plan = Organizer.Preview(Opt(path));
        var selected = Organizer.Select(plan, new[] { a }); File.WriteAllText(b, "unselected changed");
        string journal = Path.Combine(scratch, "journal-selected"); var op = Organizer.Apply(selected, journal);
        Check(!File.Exists(a) && File.ReadAllText(b) == "unselected changed" && op.Entries.Count == 1, "Unselected file affected");
        Organizer.Undo(op, journal); Check(File.ReadAllText(a) == "a" && File.ReadAllText(b) == "unselected changed", "Undo affected unselected file");
    });
    Test("selection rejects unplanned files and supports an empty selection", path =>
    {
        Write(path, "a.txt", "a"); var plan = Organizer.Preview(Opt(path));
        Throws<ArgumentException>(() => Organizer.Select(plan, new[] { Path.Combine(path, "unknown.txt") }));
        Check(Organizer.Select(plan, Array.Empty<string>()).Moves.Count == 0, "Empty selection changed plan");
    });
    Test("file and recursive folder exclusions keep similarly named siblings", path =>
    {
        string excluded = Write(path, "keep.txt", "same"); Write(path, "Privado/nested/a.txt", "same"); string sibling = Write(path, "Privado2/b.txt", "same");
        var options = Opt(path) with { ExcludedPaths = new[] { excluded, Path.Combine(path, "Privado") } };
        var plan = Organizer.Preview(options); Check(plan.Moves.Count == 1 && plan.Moves[0].Source == Path.GetFullPath(sibling), "Exclusion boundary failed");
        Check(Organizer.Duplicates(options, new()).Count == 0, "Excluded files entered duplicate scan");
        Check(Organizer.Preview(options with { ExcludedPaths = new[] { path } }).Moves.Count == 0, "Excluded root was scanned");
    });
    Test("exclusions persist and can be removed", path =>
    {
        string file = Path.Combine(path, "settings", "exclusions.json"), excluded = Path.Combine(path, "Privado");
        ExclusionStore.Save(file, new[] { excluded, excluded.ToUpperInvariant() });
        Check(ExclusionStore.Load(file).Count == 1, "Exclusions not restored or deduplicated");
        ExclusionStore.Save(file, Array.Empty<string>()); Check(ExclusionStore.Load(file).Count == 0, "Exclusions cannot be removed");
    });
    Test("drive root exclusions contain descendants without a doubled separator", path =>
    {
        Write(path, "a.txt", "a"); string root = Path.GetPathRoot(path)!;
        Check(Organizer.Within(path, root) && !Organizer.Within(path, path), "Root containment is incorrect");
        Check(Organizer.Preview(Opt(path) with { ExcludedPaths = new[] { root } }).Moves.Count == 0, "Drive root exclusion failed");
    });
    Console.WriteLine($"{passed} tests passed."); return 0;
}
finally { Directory.Delete(scratch, recursive: true); }

sealed class ImmediateProgress(Action<string> report) : IProgress<string> { public void Report(string value) => report(value); }
