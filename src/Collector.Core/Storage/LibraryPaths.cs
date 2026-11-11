using System.Security.Cryptography;
using System.Text;

namespace Collector.Core.Storage;

public static class LibraryPaths
{
    public static StringComparer Comparer => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    public static string Full(string path)
    {
        var full=Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if(OperatingSystem.IsMacOS())
            foreach(var alias in new[]{"/var","/tmp","/etc"})
                if(full==alias || full.StartsWith(alias+"/",StringComparison.Ordinal)) return "/private"+full;
        return full;
    }
    public static bool Contains(string root,string path)
    {
        root=Full(root);path=Full(path);
        var prefix=Path.EndsInDirectorySeparator(root)?root:root+Path.DirectorySeparatorChar;
        return Comparer.Equals(root,path) || path.StartsWith(prefix,
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
    public static string Resolve(ModelEntry model,ModelFile file)
    {
        if(!string.IsNullOrWhiteSpace(file.ExternalPath)) return Full(file.ExternalPath);
        if(Path.IsPathRooted(file.RelativePath)) throw new InvalidDataException("Недопустимый путь ресурса.");
        var full=Full(Path.Combine(model.FolderPath,file.RelativePath));
        if(!Contains(model.FolderPath,full)) throw new InvalidDataException("Путь выходит за папку модели.");
        return full;
    }
    public static string SafeName(string name)
    {
        const string forbidden="<>:\"/\\|?*";
        var result=new string(name.Trim().Select(c=>c<32 || forbidden.Contains(c)?'_':c).ToArray()).Trim(' ','.');
        if(result.EnumerateRunes().Count()>50) result=string.Concat(result.EnumerateRunes().Take(50).Select(r=>r.ToString())).TrimEnd(' ','.');
        if(result.Length==0) result="Без названия";
        var stem=result.Split('.')[0].ToUpperInvariant();
        if(stem is "CON" or "PRN" or "AUX" or "NUL" || Enumerable.Range(1,9).Any(i=>stem=="COM"+i || stem=="LPT"+i)) result="_"+result;
        return result;
    }
    public static string Identity(IEnumerable<string> paths)
    {
        var normalized=paths.Select(Full).Select(p=>OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()?p.ToUpperInvariant():p).Order(StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(normalized.Select(p=>p.Length+":"+p)))));
    }
    public static void EnsureNoLinks(string path)
    {
        var current=Full(path);
        while(!string.IsNullOrEmpty(current))
        {
            if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0) throw new IOException("Символические ссылки не копируются: "+path);
            current=Path.GetDirectoryName(current)??"";
        }
    }
    public static void EnsureDestination(string root,string path)
    {
        root=Full(root);path=Full(path);
        if(!Contains(root,path)) throw new InvalidDataException("Назначение вне папки коллекции.");
        var current=root;
        EnsureNoLinks(root);
        foreach(var segment in Path.GetRelativePath(root,path).Split(Path.DirectorySeparatorChar))
        {
            if(segment==".") continue;
            current=Path.Combine(current,segment);
            if(File.Exists(current) || Directory.Exists(current))
                if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0) throw new IOException("Папка назначения содержит символическую ссылку: "+current);
        }
    }
    public static void ValidateRoot(Catalog catalog,string root,Guid? excluding=null)
    {
        root=Full(root);
        if(!Directory.Exists(root)) throw new DirectoryNotFoundException("Выбранная папка коллекции недоступна.");
        EnsureNoLinks(root);
        foreach(var collection in catalog.Collections.Where(c=>c.Id!=excluding && c.RootPath.Length>0))
            if(Contains(collection.RootPath,root) || Contains(root,collection.RootPath))
                throw new InvalidDataException("Папки разных коллекций не должны совпадать или вкладываться друг в друга.");
    }
}

public sealed record ResourceSnapshot(List<ModelFile> Files,string? PreviewPath);
public static class LibraryScanner
{
    public static readonly string[] ModelExtensions=[".stl",".3mf",".obj",".ply",".step",".stp",".zip",".rar",".7z"];
    public static bool IsModel(string path)=>ModelExtensions.Contains(Path.GetExtension(path),StringComparer.OrdinalIgnoreCase);
    public static bool IsImage(string path)=>new[]{".png",".jpg",".jpeg"}.Contains(Path.GetExtension(path),StringComparer.OrdinalIgnoreCase);
    public static ResourceSnapshot ReadFolder(string root,CancellationToken token=default)
    {
        root=LibraryPaths.Full(root);LibraryPaths.EnsureNoLinks(root);
        var files=new List<ModelFile>();var pending=new Stack<(string Path,int Depth)>();pending.Push((root,0));
        while(pending.TryPop(out var current))
        {
            token.ThrowIfCancellationRequested();
            if(current.Depth>30) throw new InvalidDataException("В папке больше 30 уровней вложенности.");
            foreach(var path in Directory.EnumerateFileSystemEntries(current.Path))
            {
                token.ThrowIfCancellationRequested();
                if(Path.GetFileName(path).StartsWith('.')) continue;
                var attr=File.GetAttributes(path);
                if((attr&FileAttributes.ReparsePoint)!=0) throw new IOException("В папке есть ссылка. Выберите файлы напрямую или уберите ссылку из импортируемой папки: "+path);
                if((attr&FileAttributes.Directory)!=0) pending.Push((path,current.Depth+1));
                else
                {
                    if(files.Count>=100_000) throw new InvalidDataException("В одной папке больше 100 000 файлов.");
                    files.Add(new ModelFile { RelativePath=Path.GetRelativePath(root,path),Size=new FileInfo(path).Length });
                }
            }
        }
        files=files.OrderBy(f=>f.RelativePath,StringComparer.OrdinalIgnoreCase).ToList();
        var images=files.Where(f=>IsImage(f.RelativePath));var name=Path.GetFileName(root);
        var preview=images.OrderByDescending(f=>Path.GetFileNameWithoutExtension(f.RelativePath).Equals(name,StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
        return new(files,preview==null?null:Path.Combine(root,preview.RelativePath));
    }
}
