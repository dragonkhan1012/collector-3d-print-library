using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

var rootFlag = Array.IndexOf(args, "--root");
var root = Path.GetFullPath(rootFlag >= 0 && rootFlag + 1 < args.Length ? args[rootFlag + 1] : Directory.GetCurrentDirectory());
if (!File.Exists(Path.Combine(root, "Collector.slnx"))) throw new ArgumentException("Choose the Collector project folder with --root.");
var backup = new ProjectBackup(root);
if (!args.Contains("--watch"))
{
    backup.Capture("manual checkpoint", true);
    return;
}
Console.WriteLine("BACKUP_WATCH_STARTING");
using var watchGate = new FileStream(Path.Combine(backup.StateDirectory, "watcher.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
backup.Capture("watcher startup", false);
var dirty = 0;
using var watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
void Changed(object? sender, FileSystemEventArgs e) { if (backup.IsTracked(e.FullPath)) Interlocked.Exchange(ref dirty, 1); }
watcher.Changed += Changed; watcher.Created += Changed; watcher.Deleted += Changed;
watcher.Renamed += (sender, e) => { Changed(sender, e); if (backup.IsTracked(e.OldFullPath)) Interlocked.Exchange(ref dirty, 1); };
watcher.Error += (_, _) => Interlocked.Exchange(ref dirty, 1);
watcher.EnableRaisingEvents = true;
Console.WriteLine("BACKUP_WATCH_READY");
var nextScan = DateTime.UtcNow.AddSeconds(10);
while (!shutdown.IsCancellationRequested)
{
    try { await Task.Delay(400, shutdown.Token); } catch (OperationCanceledException) { break; }
    if (Interlocked.Exchange(ref dirty, 0) == 0 && DateTime.UtcNow < nextScan) continue;
    try { backup.Capture("saved source changes", false); nextScan = DateTime.UtcNow.AddSeconds(10); }
    catch (Exception e) { Console.Error.WriteLine("Backup failed; previous snapshot preserved: " + e.Message); Interlocked.Exchange(ref dirty, 1); }
}

sealed class ProjectBackup
{
    private readonly string _root, _backups, _baseline;
    public string StateDirectory { get; }
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", ".git", ".local", ".local-data", "Backups", "artifacts", "__pycache__" };
    private static readonly HashSet<string> RootExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".csproj", ".slnx", ".sln", ".axaml", ".json", ".md", ".props", ".targets", ".command", ".cmd", ".code-workspace" };
    public ProjectBackup(string root)
    {
        _root = root; _backups = Path.Combine(root, "Backups");
        StateDirectory = Path.Combine(root, ".local", "backup-state");
        Directory.CreateDirectory(_backups); Directory.CreateDirectory(StateDirectory);
        _baseline = Path.Combine(StateDirectory, "current.zip");
    }
    public bool IsTracked(string path)
    {
        var relative = Path.GetRelativePath(_root, path);
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Any(p => Excluded.Contains(p)) || parts[0] == ".." || parts[^1] == ".DS_Store") return false;
        return parts.Length == 1 ? RootExtensions.Contains(Path.GetExtension(path)) || parts[0] == ".gitignore"
            : parts[0] is "src" or "tests" or "tools" or ".vscode" or "docs";
    }
    private Dictionary<string, byte[]> ReadProject()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        void ReadDirectory(string directory)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0 || Excluded.Contains(Path.GetFileName(path))) continue;
                if ((attributes & FileAttributes.Directory) != 0) { if (IsTracked(path + Path.DirectorySeparatorChar + "_")) ReadDirectory(path); continue; }
                if (!IsTracked(path)) continue;
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var length = input.Length; var written = File.GetLastWriteTimeUtc(path);
                using var data = new MemoryStream(); input.CopyTo(data);
                if (input.Length != length || File.GetLastWriteTimeUtc(path) != written) throw new IOException("File is being saved: " + path);
                files.Add(Path.GetRelativePath(_root, path).Replace('\\', '/'), data.ToArray());
            }
        }
        ReadDirectory(_root); return files;
    }
    private static Dictionary<string, byte[]> ReadSnapshot(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.Where(e => e.FullName != "backup-manifest.json").ToDictionary(e => e.FullName, e =>
        { using var stream = e.Open(); using var data = new MemoryStream(); stream.CopyTo(data); return data.ToArray(); }, StringComparer.Ordinal);
    }
    private static void WriteSnapshot(string path, Dictionary<string, byte[]> files, string reason, string[] changes)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, data) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
        { using var stream = zip.CreateEntry(name, CompressionLevel.Fastest).Open(); stream.Write(data); }
        using var manifest = zip.CreateEntry("backup-manifest.json").Open();
        JsonSerializer.Serialize(manifest, new { createdUtc = DateTimeOffset.UtcNow, reason, changedPaths = changes,
            files = files.ToDictionary(f => f.Key, f => Convert.ToHexString(SHA256.HashData(f.Value))) });
    }
    public void Capture(string reason, bool checkpoint)
    {
        using var gate = new FileStream(Path.Combine(StateDirectory, "snapshot.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var current = ReadProject();
        var previous = File.Exists(_baseline) ? ReadSnapshot(_baseline) : current;
        var changes = current.Keys.Union(previous.Keys).Where(p => !current.TryGetValue(p, out var a) || !previous.TryGetValue(p, out var b) || !a.AsSpan().SequenceEqual(b)).Order().ToArray();
        if (changes.Length == 0 && !checkpoint && File.Exists(_baseline)) return;
        var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ");
        var destination = Path.Combine(_backups, stamp + (changes.Length == 0 ? "-checkpoint.zip" : "-previous.zip"));
        var temporary = destination + ".tmp";
        WriteSnapshot(temporary, previous, reason, changes); File.Move(temporary, destination);
        var state = _baseline + ".tmp";
        if (File.Exists(state)) File.Delete(state);
        WriteSnapshot(state, current, "current baseline", []); File.Move(state, _baseline, true);
        Console.WriteLine($"BACKUP_SAVED {Path.GetFileName(destination)} ({previous.Count} files, {changes.Length} changes)");
    }
}
