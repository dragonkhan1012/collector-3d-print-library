using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Collector.Core.Storage;

public enum ImportLayout { FilesSeparate, FilesTogether, FolderAsModel, SubfoldersAsModels }
public sealed record LibraryImportRequest(Guid CollectionId,string Category,StorageKind Storage,string[] Paths,ImportLayout Layout,string? GroupName=null);
public sealed record LibraryProgress(string Message,int Completed,int Total,bool Finalizing=false);
public sealed class PreparedImport
{
    public Guid Id { get; set; }=Guid.NewGuid();
    public string State { get; set; }="Copying";
    public string Root { get; set; }="";
    public List<ModelEntry> Models { get; set; }=[];
    public List<string> Warnings { get; set; }=[];
    public DateTimeOffset CreatedUtc { get; set; }=DateTimeOffset.UtcNow;
}

public sealed class LibraryStorage(string dataDirectory)
{
    private static readonly JsonSerializerOptions Json=new() { WriteIndented=true,Converters={new JsonStringEnumConverter()} };
    private string Operations=>Path.Combine(dataDirectory,"imports");
    private FileStream LockOperation()
    {
        Directory.CreateDirectory(Operations);
        try { return new FileStream(Path.Combine(Operations,"import.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None); }
        catch(IOException e) { throw new IOException("Импорт уже выполняется в другом окне. Дождитесь его завершения.",e); }
    }
    private string Journal(PreparedImport operation)=>Path.Combine(Operations,operation.Id.ToString("N")+".json");
    private void WriteJournal(PreparedImport operation)
    {
        Directory.CreateDirectory(Operations);var path=Journal(operation);var temporary=path+".tmp";
        using(var file=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None))
        { JsonSerializer.Serialize(file,operation,Json);file.Flush(true); }
        File.Move(temporary,path,true);
        if(operation.State is "Committed" or "Failed" or "Cancelled") File.Move(path,Path.ChangeExtension(path,".done"),true);
    }
    public bool HasPending => Directory.Exists(Operations) && Directory.EnumerateFiles(Operations,"*.json").Any();
    public List<PreparedImport> GetPending()
    {
        var result=new List<PreparedImport>();
        if(!Directory.Exists(Operations)) return result;
        foreach(var file in Directory.EnumerateFiles(Operations,"*.json"))
        {
            try
            {
                var op=JsonSerializer.Deserialize<PreparedImport>(File.ReadAllText(file),Json);
                if(op!=null && op.State is "Ready" or "Finalizing" or "Copying") result.Add(op);
            }
            catch(Exception e) when(e is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return result.OrderBy(o=>o.CreatedUtc).ToList();
    }
    public async Task<PreparedImport> PrepareAsync(Catalog catalog,LibraryImportRequest request,IProgress<LibraryProgress>? progress=null,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();
        using var operationLock=LockOperation();
        var collection=catalog.Collections.SingleOrDefault(c=>c.Id==request.CollectionId)
            ??throw new InvalidDataException("Коллекция не найдена.");
        var category=string.IsNullOrWhiteSpace(request.Category)?"Без категории":request.Category.Trim();
        var operation=new PreparedImport { Root=request.Storage==StorageKind.Managed && collection.RootPath.Length>0?LibraryPaths.Full(collection.RootPath):"" };
        if(request.Storage==StorageKind.Managed)
        {
            if(string.IsNullOrWhiteSpace(collection.RootPath)) throw new InvalidDataException("Сначала выберите папку хранения для коллекции.");
            LibraryPaths.ValidateRoot(catalog,collection.RootPath,collection.Id);
        }
        if(request.Storage==StorageKind.Managed && request.Layout is ImportLayout.FolderAsModel or ImportLayout.SubfoldersAsModels)
            foreach(var source in request.Paths)
                if(LibraryPaths.Contains(source,operation.Root) || LibraryPaths.Contains(operation.Root,source))
                    throw new InvalidDataException("Источник и папка хранения пересекаются. Выберите другую папку или подключение на месте.");
        var models=Plan(request,category,operation.Warnings,token);
        var existing=catalog.Models.Where(m=>m.CollectionId==collection.Id).Select(SourceKey).Where(s=>s.Length>0).ToHashSet(StringComparer.Ordinal);
        foreach(var pending in GetPending())
            foreach(var model in pending.Models.Where(m=>m.CollectionId==collection.Id)) existing.Add(model.SourceIdentity);
        foreach(var model in models)
        {
            token.ThrowIfCancellationRequested();
            model.CollectionId=collection.Id;model.StorageKind=request.Storage;
            if(!existing.Add(model.SourceIdentity)) { operation.Warnings.Add("Уже добавлено: "+model.Name);continue; }
            operation.Models.Add(model);
        }
        WriteJournal(operation);
        var stageBase=operation.Root.Length==0?"":Path.Combine(operation.Root,".collector-staging",operation.Id.ToString("N"));
        try
        {
            var completed=0;var total=operation.Models.Sum(m=>m.Files.Count);
            var hashes=catalog.Models.Where(m=>m.CollectionId==collection.Id)
                .Select(m=>m.Files.Where(f=>LibraryScanner.IsModel(f.RelativePath)).ToList())
                .Where(files=>files.Count==1 && files[0].Sha256!=null).Select(files=>files[0].Sha256!).ToHashSet(StringComparer.Ordinal);
            var duplicates=new List<ModelEntry>();
            foreach(var model in operation.Models)
            {
                token.ThrowIfCancellationRequested();
                if(request.Storage==StorageKind.Linked) { completed+=model.Files.Count;continue; }
                var sourceRoot=LibraryPaths.Full(model.FolderPath);
                if(model.TrackFolder && (LibraryPaths.Contains(sourceRoot,operation.Root) || LibraryPaths.Contains(operation.Root,sourceRoot)))
                    throw new InvalidDataException("Источник и папка хранения пересекаются. Для файлов внутри библиотеки используйте подключение на месте.");
                var stage=Path.Combine(stageBase,model.Id.ToString("N"));LibraryPaths.EnsureDestination(operation.Root,stage);Directory.CreateDirectory(stage);
                var target=Path.Combine(operation.Root,LibraryPaths.SafeName(category),LibraryPaths.SafeName(model.Name)+"-"+model.Id.ToString("N")[..10]);
                LibraryPaths.EnsureDestination(operation.Root,target);
                var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var copied=new List<ModelFile>();string? preview=null;
                foreach(var file in model.Files)
                {
                    token.ThrowIfCancellationRequested();
                    var source=LibraryPaths.Resolve(model,file);LibraryPaths.EnsureNoLinks(source);
                    var segments=file.RelativePath.Split(Path.DirectorySeparatorChar);
                    var fileName=Path.GetFileNameWithoutExtension(segments[^1]);var extension=Path.GetExtension(segments[^1]);
                    var relative=Path.Combine(segments[..^1].Select(LibraryPaths.SafeName).Append(LibraryPaths.SafeName(fileName)+extension).ToArray());
                    var original=relative;var number=2;
                    while(!names.Add(relative)) relative=Path.Combine(Path.GetDirectoryName(original)??"",Path.GetFileNameWithoutExtension(original)+$" ({number++})"+Path.GetExtension(original));
                    var destination=Path.Combine(stage,relative);Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    progress?.Report(new LibraryProgress("Копирование: "+file.RelativePath,completed,total));
                    var hash=await CopyVerifiedAsync(source,destination,token);
                    copied.Add(new ModelFile { RelativePath=relative,Size=new FileInfo(destination).Length,Sha256=hash });
                    if(model.PreviewPath!=null && LibraryPaths.Comparer.Equals(LibraryPaths.Full(model.PreviewPath),source)) preview=Path.Combine(target,relative);
                    completed++;
                }
                var primaryFiles=copied.Where(f=>LibraryScanner.IsModel(f.RelativePath)).ToList();
                if(primaryFiles.Count==1 && !hashes.Add(primaryFiles[0].Sha256!))
                { duplicates.Add(model);operation.Warnings.Add("Точная копия уже есть в коллекции: "+model.Name);continue; }
                model.FolderPath=target;model.Files=copied;model.PreviewPath=preview;model.TrackFolder=true;
            }
            foreach(var duplicate in duplicates) operation.Models.Remove(duplicate);
            token.ThrowIfCancellationRequested();
            // From this point only same-volume renames and journal writes remain; finalization is not interrupted.
            progress?.Report(new LibraryProgress("Завершение импорта…",completed,total,true));
            operation.State="Finalizing";WriteJournal(operation);
            foreach(var model in operation.Models.Where(m=>m.StorageKind==StorageKind.Managed))
            {
                LibraryPaths.EnsureNoLinks(operation.Root);
                LibraryPaths.EnsureDestination(operation.Root,model.FolderPath);
                Directory.CreateDirectory(Path.GetDirectoryName(model.FolderPath)!);
                if(Directory.Exists(model.FolderPath) || File.Exists(model.FolderPath)) throw new IOException("Папка назначения уже существует. Ничего не перезаписано.");
                Directory.Move(Path.Combine(stageBase,model.Id.ToString("N")),model.FolderPath);
            }
            operation.State="Ready";WriteJournal(operation);
            CleanupStage(stageBase);
            return operation;
        }
        catch
        {
            // Once finalization started, preserve completed copies and the journal for recovery.
            if(operation.State!="Finalizing")
            {
                operation.State=token.IsCancellationRequested?"Cancelled":"Failed";
                try { WriteJournal(operation); } catch(IOException) { } catch(UnauthorizedAccessException) { }
                CleanupStage(stageBase);
            }
            throw;
        }
    }
    public int Commit(Catalog catalog,CatalogStore store,PreparedImport operation)
    {
        if(operation.State=="Committed") return 0;
        using var operationLock=LockOperation();
        if(operation.State!="Ready") throw new InvalidOperationException("Импорт ещё не подготовлен.");
        foreach(var model in operation.Models)
            if(!catalog.Collections.Any(c=>c.Id==model.CollectionId)) throw new InvalidDataException("Коллекция импорта не найдена.");
        var added=operation.Models.Where(m=>!catalog.Models.Any(old=>old.Id==m.Id)).ToList();
        var categories=catalog.Collections.ToDictionary(c=>c.Id,c=>c.Categories.ToList());
        catalog.Models.AddRange(added);
        try { if(added.Count>0) store.Save(catalog); }
        catch
        {
            foreach(var model in added) catalog.Models.Remove(model);
            foreach(var collection in catalog.Collections) collection.Categories=categories[collection.Id];
            throw;
        }
        operation.State="Committed";WriteJournal(operation);return added.Count;
    }
    public async Task<PreparedImport> RecoverAsync(Catalog catalog,PreparedImport operation,CancellationToken token=default)
    {
        using var operationLock=LockOperation();
        if(operation.State=="Copying") throw new InvalidOperationException("Копирование было прервано. Уберите временные копии и повторите добавление; исходники сохранены.");
        foreach(var model in operation.Models)
        {
            token.ThrowIfCancellationRequested();
            var collection=catalog.Collections.SingleOrDefault(c=>c.Id==model.CollectionId)??throw new InvalidDataException("Коллекция восстановления не найдена.");
            if(model.StorageKind!=StorageKind.Managed) continue;
            if(!LibraryPaths.Comparer.Equals(LibraryPaths.Full(collection.RootPath),LibraryPaths.Full(operation.Root))
                || !LibraryPaths.Contains(operation.Root,model.FolderPath)) throw new InvalidDataException("Небезопасный путь восстановления.");
            if(!Directory.Exists(model.FolderPath))
            {
                var stage=Path.Combine(operation.Root,".collector-staging",operation.Id.ToString("N"),model.Id.ToString("N"));
                if(!Directory.Exists(stage)) throw new DirectoryNotFoundException("Подготовленные файлы не найдены: "+model.Name);
                LibraryPaths.EnsureNoLinks(stage);LibraryPaths.EnsureDestination(operation.Root,model.FolderPath);Directory.CreateDirectory(Path.GetDirectoryName(model.FolderPath)!);Directory.Move(stage,model.FolderPath);
            }
            foreach(var file in model.Files)
            {
                var path=LibraryPaths.Resolve(model,file);LibraryPaths.EnsureNoLinks(path);
                await using var stream=File.OpenRead(path);
                var hash=Convert.ToHexString(await SHA256.HashDataAsync(stream,token));
                if(hash!=file.Sha256) throw new InvalidDataException("Копия изменилась: "+file.RelativePath+". Автоматическое восстановление остановлено.");
            }
        }
        operation.State="Ready";WriteJournal(operation);
        if(operation.Root.Length>0) CleanupStage(Path.Combine(operation.Root,".collector-staging",operation.Id.ToString("N")));
        return operation;
    }
    public void DiscardInterrupted(Catalog catalog,PreparedImport operation)
    {
        using var operationLock=LockOperation();
        if(operation.State!="Copying") throw new InvalidOperationException("Готовые файлы нельзя убрать как временные. Используйте восстановление.");
        if(operation.Root.Length>0)
        {
            if(!catalog.Collections.Any(c=>c.RootPath.Length>0 && LibraryPaths.Comparer.Equals(LibraryPaths.Full(c.RootPath),LibraryPaths.Full(operation.Root))))
                throw new InvalidDataException("Не удалось подтвердить папку коллекции.");
            LibraryPaths.EnsureNoLinks(operation.Root);
            var stage=Path.Combine(operation.Root,".collector-staging",operation.Id.ToString("N"));
            LibraryPaths.EnsureDestination(operation.Root,stage);
            if(!CleanupStage(stage)) throw new IOException("Не удалось убрать временные копии. Исходники сохранены; повторите попытку.");
        }
        operation.State="Cancelled";WriteJournal(operation);
    }
    private static bool CleanupStage(string stage)
    {
        if(stage.Length==0) return true;
        try
        {
            if(!Directory.Exists(stage)) return true;
            var root=Path.GetDirectoryName(Path.GetDirectoryName(stage))!;
            LibraryPaths.EnsureDestination(root,stage);
            Directory.Delete(stage,true);return true;
        }
        catch(IOException) { return false; }catch(UnauthorizedAccessException) { return false; }
    }
    private static string SourceKey(ModelEntry model)
    {
        if(model.SourceIdentity.Length>0) return model.SourceIdentity;
        if(model.FolderPath.Length==0 || model.Files.Count==0) return "";
        try { return model.TrackFolder?LibraryPaths.Identity([model.FolderPath]):LibraryPaths.Identity(model.Files.Where(f=>LibraryScanner.IsModel(f.RelativePath)).Select(f=>LibraryPaths.Resolve(model,f))); }
        catch(ArgumentException) { return ""; }
    }
    private static List<ModelEntry> Plan(LibraryImportRequest request,string category,List<string> warnings,CancellationToken token)
    {
        var paths=request.Paths.Select(LibraryPaths.Full).Distinct(LibraryPaths.Comparer).ToArray();var result=new List<ModelEntry>();
        void Folder(string path)
        {
            token.ThrowIfCancellationRequested();var snapshot=LibraryScanner.ReadFolder(path,token);
            if(!snapshot.Files.Any(f=>LibraryScanner.IsModel(f.RelativePath))) { warnings.Add("Нет файлов моделей: "+path);return; }
            result.Add(new ModelEntry { Name=Path.GetFileName(path),Category=category,FolderPath=path,Files=snapshot.Files,PreviewPath=snapshot.PreviewPath,SourceIdentity=LibraryPaths.Identity([path]) });
        }
        if(request.Layout is ImportLayout.FolderAsModel or ImportLayout.SubfoldersAsModels)
        {
            if(paths.Length!=1) throw new InvalidDataException("Выберите одну исходную папку.");
            if(request.Layout==ImportLayout.FolderAsModel) Folder(paths[0]);
            else
                foreach(var path in Directory.EnumerateDirectories(paths[0]).Where(p=>!Path.GetFileName(p).StartsWith('.')).Order(StringComparer.OrdinalIgnoreCase)) Folder(path);
        }
        else
        {
            foreach(var path in paths) { token.ThrowIfCancellationRequested();LibraryPaths.EnsureNoLinks(path);if(!File.Exists(path)) throw new FileNotFoundException("Исходный файл недоступен.",path); }
            var groups=request.Layout==ImportLayout.FilesTogether?new[]{paths}:paths.Where(LibraryScanner.IsModel).Select(p=>new[]{p});
            foreach(var group in groups)
            {
                if(group.Length==0 || !group.Any(LibraryScanner.IsModel)) continue;
                var primary=group.First(LibraryScanner.IsModel);
                var model=new ModelEntry { Name=request.Layout==ImportLayout.FilesTogether && !string.IsNullOrWhiteSpace(request.GroupName)?request.GroupName.Trim():Path.GetFileNameWithoutExtension(primary),
                    Category=category,FolderPath=Path.GetDirectoryName(primary)!,TrackFolder=false,SourceIdentity=LibraryPaths.Identity(group.Where(LibraryScanner.IsModel)) };
                model.Files=group.Select(path=>new ModelFile { RelativePath=Path.GetFileName(path),ExternalPath=path,Size=new FileInfo(path).Length }).ToList();
                model.PreviewPath=group.FirstOrDefault(LibraryScanner.IsImage);result.Add(model);
            }
        }
        if(result.Count==0) warnings.Add("Не найдены файлы моделей поддерживаемых форматов.");
        if(request.Layout==ImportLayout.FilesSeparate && paths.Any(p=>!LibraryScanner.IsModel(p)))
            warnings.Add("Картинки и документы отдельно не добавлены: объедините их с моделью в одну карточку или используйте «Прикрепить файлы».");
        return result;
    }
    private static async Task<string> CopyVerifiedAsync(string source,string target,CancellationToken token)
    {
        var before=new FileInfo(source);var length=before.Length;var stamp=before.LastWriteTimeUtc;
        string expected;
        await using(var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read,65536,true))
        await using(var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true))
        using(var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer=new byte[65536];int read;
            while((read=await input.ReadAsync(buffer,token))>0) { hash.AppendData(buffer,0,read);await output.WriteAsync(buffer.AsMemory(0,read),token); }
            await output.FlushAsync(token);output.Flush(true);expected=Convert.ToHexString(hash.GetHashAndReset());
        }
        before.Refresh();if(before.Length!=length || before.LastWriteTimeUtc!=stamp) throw new IOException("Исходный файл изменился во время копирования: "+source);
        await using(var copy=File.OpenRead(target))
            if(copy.Length!=length || Convert.ToHexString(await SHA256.HashDataAsync(copy,token))!=expected) throw new IOException("Проверка копии не пройдена: "+source);
        return expected;
    }
}
