namespace Collector.Core;

public sealed record ScanResult(List<ModelEntry> Models, List<string> Warnings);

public static class FolderScanner
{
    private static readonly HashSet<string> Formats = new(StringComparer.OrdinalIgnoreCase)
        { ".stl", ".3mf", ".obj", ".ply", ".step", ".stp", ".zip", ".rar", ".7z" };
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg" };

    // Files remain in place. Links are excluded to avoid loops and escapes from the chosen tree.
    public static ScanResult Scan(string root, CancellationToken cancellationToken = default)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Папка недоступна.");
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Выберите исходную папку, а не символическую ссылку.");
        var warnings = new List<string>();
        var candidates = new List<string>();
        if (Directory.EnumerateFiles(root).Any(f => Formats.Contains(Path.GetExtension(f)))) candidates.Add(root);
        else candidates.AddRange(Directory.EnumerateDirectories(root)
            .Where(d => !Path.GetFileName(d).StartsWith('.') && Path.GetFileName(d) != "разобрать"
                && (File.GetAttributes(d) & FileAttributes.ReparsePoint) == 0));
        var models = new List<ModelEntry>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var found = new List<ModelFile>();
            var previews = new List<string>();
            var pending = new Stack<(string Path, int Depth)>(); pending.Push((candidate, 0));
            while (pending.TryPop(out var current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (current.Depth > 30) { warnings.Add($"Слишком глубокая папка: {current.Path}"); continue; }
                try
                {
                    foreach (var path in Directory.EnumerateFileSystemEntries(current.Path))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var attr = File.GetAttributes(path);
                        if ((attr & FileAttributes.ReparsePoint) != 0 || Path.GetFileName(path).StartsWith('.')) continue;
                        if ((attr & FileAttributes.Directory) != 0)
                        {
                            if (Path.GetFileName(path) != "разобрать") pending.Push((path, current.Depth + 1));
                        }
                        else if (Formats.Contains(Path.GetExtension(path)))
                            found.Add(new ModelFile { RelativePath = Path.GetRelativePath(candidate, path), Size = new FileInfo(path).Length });
                        else if (Images.Contains(Path.GetExtension(path))) previews.Add(path);
                        if (found.Count > 100_000) throw new InvalidDataException("В одной карточке более 100 000 файлов. Выберите папку меньшего размера.");
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { warnings.Add($"Не прочитано: {current.Path} — {e.Message}"); }
            }
            if (found.Count == 0) continue;
            var name = Path.GetFileName(candidate);
            models.Add(new ModelEntry
            {
                Name = name, Category = "Без категории", FolderPath = candidate, Files = found,
                PreviewPath = previews.OrderByDescending(p => string.Equals(Path.GetFileNameWithoutExtension(p), name, StringComparison.OrdinalIgnoreCase))
                    .ThenBy(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
            });
        }
        return new ScanResult(models, warnings);
    }
}
