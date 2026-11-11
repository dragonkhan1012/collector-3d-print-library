namespace Collector.Core.Storage;

public sealed record VerificationResult(List<ModelFile> Files,string? PreviewPath,bool FolderUnavailable,DateTimeOffset CheckedUtc);
public static class LibraryMaintenance
{
    public static VerificationResult Verify(ModelEntry model,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();
        var now=DateTimeOffset.UtcNow;
        if(string.IsNullOrWhiteSpace(model.FolderPath) || !Directory.Exists(model.FolderPath))
            return new(model.Files.ToList(),model.PreviewPath,true,now);
        var files=model.TrackFolder?LibraryScanner.ReadFolder(model.FolderPath,token).Files:new List<ModelFile>();
        var known=files.Select(f=>LibraryPaths.Resolve(model,f)).ToHashSet(LibraryPaths.Comparer);
        foreach(var old in model.Files)
        {
            token.ThrowIfCancellationRequested();var path=LibraryPaths.Resolve(model,old);
            if(known.Contains(path))
            {
                // A refreshed size does not prove an old content hash is current.
                continue;
            }
            if(!model.TrackFolder || old.ExternalPath!=null || !File.Exists(path))
            {
                files.Add(new ModelFile { RelativePath=old.RelativePath,ExternalPath=old.ExternalPath,
                    Size=File.Exists(path)?new FileInfo(path).Length:old.Size,Missing=!File.Exists(path) });
                known.Add(path);
            }
        }
        var preview=model.PreviewPath;
        if(preview==null || !File.Exists(preview))
            preview=files.Where(f=>!f.Missing && LibraryScanner.IsImage(f.RelativePath)).Select(f=>LibraryPaths.Resolve(model,f)).FirstOrDefault();
        return new(files,preview,false,now);
    }
    public static List<ModelFile> PrepareAttachments(ModelEntry model,IEnumerable<string> paths,CancellationToken token=default)
    {
        var known=model.Files.Select(f=>LibraryPaths.Resolve(model,f)).ToHashSet(LibraryPaths.Comparer);
        var names=model.Files.Select(f=>f.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result=new List<ModelFile>();
        foreach(var input in paths)
        {
            token.ThrowIfCancellationRequested();var path=LibraryPaths.Full(input);
            if(!known.Add(path)) continue;
            LibraryPaths.EnsureNoLinks(path);var info=new FileInfo(path);if(!info.Exists) throw new FileNotFoundException("Файл недоступен.",path);
            var alias=Path.GetFileName(path);var i=2;
            while(!names.Add(alias)) alias=Path.GetFileNameWithoutExtension(path)+$" ({i++})"+Path.GetExtension(path);
            result.Add(new ModelFile { RelativePath=alias,ExternalPath=path,Size=info.Length });
        }
        return result;
    }
}
