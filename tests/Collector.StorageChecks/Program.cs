using System.Security.Cryptography;
using System.Text.Json;
using Collector.Core;
using Collector.Core.Storage;

var root=Path.Combine(Path.GetTempPath(),"collector-storage-"+Guid.NewGuid());Directory.CreateDirectory(root);
var checks=0;
void Check(bool condition,string message) { if(!condition) throw new Exception(message);checks++;Console.WriteLine("PASS: "+message); }
string Dir(string name) { var path=Path.Combine(root,name);Directory.CreateDirectory(path);return path; }
string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
try
{
    var data=Dir("data");var store=new CatalogStore(data);var catalog=store.Load();
    var a=new CollectionEntry { Name="Ссылки",RootPath=Dir("library-a") };
    var b=new CollectionEntry { Name="Копии",RootPath=Dir("library-b") };
    catalog.Collections.AddRange([a,b]);store.Save(catalog);var library=new LibraryStorage(data);
    var source=Dir("source/Dragon");File.WriteAllText(Path.Combine(source,"dragon.stl"),"solid original model content");
    File.WriteAllText(Path.Combine(source,"license.txt"),"keep this license");Directory.CreateDirectory(Path.Combine(source,"textures"));
    File.WriteAllBytes(Path.Combine(source,"textures","preview.png"),[1,2,3,4,5]);File.WriteAllText(Path.Combine(source,".DS_Store"),"ignored");
    var hashes=Directory.GetFiles(source,"*",SearchOption.AllDirectories).ToDictionary(p=>p,Hash);
    var linked=await library.PrepareAsync(catalog,new(a.Id,"Драконы",StorageKind.Linked,[source],ImportLayout.FolderAsModel));
    Check(library.GetPending().Count==1 && linked.Models.Single().Files.Count==3,"папка подключается с моделями, лицензией и текстурами");
    Check(library.Commit(catalog,store,linked)==1 && library.GetPending().Count==0,"подключение сохраняется; завершённый журнал не мешает работе");
    Check(hashes.All(pair=>Hash(pair.Key)==pair.Value),"подключение сохраняет исходные файлы побайтово");
    var linkedAgain=await library.PrepareAsync(catalog,new(a.Id,"Драконы",StorageKind.Linked,[source],ImportLayout.FolderAsModel));
    Check(linkedAgain.Models.Count==0 && linkedAgain.Warnings.Count>0 && library.Commit(catalog,store,linkedAgain)==0,"повторное подключение не создаёт карточку-дубль");
    var copied=await library.PrepareAsync(catalog,new(b.Id,"Фэнтези",StorageKind.Managed,[source],ImportLayout.FolderAsModel));
    var copy=copied.Models.Single();
    Check(copy.Files.Count==3 && copy.Files.All(f=>f.Sha256!=null && Hash(LibraryPaths.Resolve(copy,f))==f.Sha256),"управляемые копии проверены по SHA-256");
    Check(copy.PreviewPath!=null && File.Exists(copy.PreviewPath) && File.Exists(Path.Combine(copy.FolderPath,"license.txt")),"превью и сопутствующие файлы сохранены в копии");
    Check(hashes.All(pair=>Hash(pair.Key)==pair.Value),"копирование не изменяет исходники");
    library.Commit(catalog,store,copied);
    Check(CatalogQuery.Run(catalog,collectionId:a.Id).Count==1 && CatalogQuery.Run(catalog,collectionId:b.Id).Count==1,"поиск разделён по коллекциям");
    var solo=Path.Combine(Dir("source/files"),"model.stl");File.WriteAllText(solo,"unique STL single content");
    var single=await library.PrepareAsync(catalog,new(b.Id,"CON",StorageKind.Managed,[solo],ImportLayout.FilesSeparate));
    library.Commit(catalog,store,single);
    Check(single.Models[0].FolderPath.Contains("_CON"),"имена папок совместимы с зарезервированными именами Windows");
    var twin=Path.Combine(Dir("source/twin"),"same.stl");File.Copy(solo,twin);
    var duplicate=await library.PrepareAsync(catalog,new(b.Id,"Дубликаты",StorageKind.Managed,[twin],ImportLayout.FilesSeparate));
    Check(duplicate.Models.Count==0 && duplicate.Warnings.Any(w=>w.Contains("Точная копия")),"одинаковое содержимое из другого места распознаётся как дубль");library.Commit(catalog,store,duplicate);
    var other=Path.Combine(Dir("source/other"),"model.stl");File.WriteAllText(other,"another geometry");
    var grouped=await library.PrepareAsync(catalog,new(b.Id,"Наборы",StorageKind.Managed,[solo,other],ImportLayout.FilesTogether,"Две детали"));
    Check(grouped.Models.Count==1 && grouped.Models[0].Files.Select(f=>f.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count()==2,"одинаковые имена файлов в наборе не перезаписывают друг друга");library.Commit(catalog,store,grouped);
    var extra=Path.Combine(Dir("extra"),"note.txt");File.WriteAllText(extra,"attachment");
    var attachments=LibraryMaintenance.PrepareAttachments(copy,[extra,extra]);
    Check(attachments.Count==1 && LibraryPaths.Resolve(copy,attachments[0])==LibraryPaths.Full(extra),"дополнительный файл прикрепляется ссылкой без дубля");
    copy.Files.AddRange(attachments);store.Save(catalog);
    var verified=LibraryMaintenance.Verify(copy);
    Check(verified.Files.Any(f=>f.ExternalPath!=null && LibraryPaths.Resolve(copy,f)==LibraryPaths.Full(extra)),"проверка папки сохраняет внешние вложения");
    File.Delete(Path.Combine(source,"dragon.stl"));
    var missing=LibraryMaintenance.Verify(linked.Models[0]);
    Check(missing.Files.Count==3 && missing.Files.Count(f=>f.Missing)==1,"пропавший файл отмечается, а его описание не удаляется");
    var moved=Path.Combine(root,"temporarily-offline");Directory.Move(source,moved);
    var offline=LibraryMaintenance.Verify(linked.Models[0]);
    Check(offline.FolderUnavailable && offline.Files.Count==3 && offline.Files.All(f=>!f.Missing),"недоступный диск не превращается в массовое удаление файлов");Directory.Move(moved,source);
    var folderCount=Directory.GetDirectories(b.RootPath,"*",SearchOption.AllDirectories).Count(p=>!p.Contains(".collector-staging"));
    using(var cancel=new CancellationTokenSource())
    {
        var progress=new InlineProgress(_=>cancel.Cancel());var cancelled=false;
        try { await library.PrepareAsync(catalog,new(b.Id,"Отмена",StorageKind.Managed,[extra,other],ImportLayout.FilesTogether),progress,cancel.Token); }catch(OperationCanceledException) { cancelled=true; }
        Check(cancelled && File.Exists(extra) && File.Exists(other) && library.GetPending().Count==0,"отмена очищает временные копии, сохраняя исходники и каталог");
    }
    var unsafeInput=false;
    try { await library.PrepareAsync(catalog,new(b.Id,"Вложение",StorageKind.Managed,[root],ImportLayout.FolderAsModel)); }catch(InvalidDataException) { unsafeInput=true; }
    Check(unsafeInput,"копирование библиотеки внутрь самой себя запрещено до обхода дерева");
    var recoverySource=Path.Combine(Dir("source/recovery"),"recover.obj");File.WriteAllText(recoverySource,"v 0 0 0");
    var recovery=await library.PrepareAsync(catalog,new(b.Id,"Восстановление",StorageKind.Managed,[recoverySource],ImportLayout.FilesSeparate));
    var countBefore=catalog.Models.Count;var failData=Dir("fail-data");Directory.CreateDirectory(Path.Combine(failData,"catalog.json"));var failed=false;
    try { library.Commit(catalog,new CatalogStore(failData),recovery); }catch(IOException) { failed=true; }
    Check(failed && catalog.Models.Count==countBefore && library.GetPending().Count==1 && File.Exists(LibraryPaths.Resolve(recovery.Models[0],recovery.Models[0].Files[0])),"сбой записи каталога сохраняет готовые копии и журнал для восстановления");
    var pending=library.GetPending().Single();
    var stage=Path.Combine(pending.Root,".collector-staging",pending.Id.ToString("N"),pending.Models[0].Id.ToString("N"));Directory.CreateDirectory(Path.GetDirectoryName(stage)!);Directory.Move(pending.Models[0].FolderPath,stage);
    pending.State="Finalizing";File.WriteAllText(Path.Combine(data,"imports",pending.Id.ToString("N")+".json"),JsonSerializer.Serialize(pending));
    var recovered=await library.RecoverAsync(catalog,library.GetPending().Single());
    Check(library.Commit(catalog,store,recovered)==1 && library.GetPending().Count==0 && library.Commit(catalog,store,recovered)==0,"прерванное завершение восстанавливается идемпотентно");
    var legacyData=Dir("legacy");var legacyId=Guid.NewGuid();var legacyFile=Path.Combine(legacyData,"catalog.json");
    File.WriteAllText(legacyFile,JsonSerializer.Serialize(new { SchemaVersion=1,Models=new[]{new { Id=legacyId,Name="Старая модель",Category="Редкости",Rarity=2,PrintState=1,Author="Мастер",Notes="сохранить",Tags=new[]{"tag"},FolderPath="",Files=Array.Empty<object>(),Hidden=true }} }));
    var legacyHash=Hash(legacyFile);var legacyStore=new CatalogStore(legacyData);var upgraded=legacyStore.Load();
    Check(upgraded.SchemaVersion==2 && upgraded.Models[0].Id==legacyId && upgraded.Models[0].Hidden && upgraded.Models[0].Tags.Single()=="tag" && upgraded.Collections.Count==1 && Hash(legacyFile)==legacyHash,"миграция v1 сохраняет ID, теги, скрытие и исходный файл до сохранения");
    legacyStore.Save(upgraded);Check(Hash(legacyFile+".bak")==legacyHash && legacyStore.Load().Models[0].CollectionId==upgraded.Collections[0].Id,"после миграции остаётся резервная копия старого формата");
    var stale=store.Load();var current=store.Load();current.Models[0].Notes="из другого окна";store.Save(current);
    var conflict=false;try { stale.Models[0].Notes="устаревшее";store.Save(stale); }catch(IOException) { conflict=true; }
    Check(conflict && store.Load().Models[0].Notes=="из другого окна","два окна не затирают изменения друг друга");
    var boundary=Path.Combine(Dir("boundaries"),"child");Directory.CreateDirectory(boundary);
    var nested=Path.Combine(b.RootPath,"child");Directory.CreateDirectory(nested);
    var overlap=false;try { LibraryPaths.ValidateRoot(catalog,nested); }catch(InvalidDataException) { overlap=true; }
    Check(overlap,"вложенные корни коллекций не принимаются");
    var outside=Dir("outside");var link=Path.Combine(b.RootPath,"Symlink");Directory.CreateSymbolicLink(link,outside);
    var protectedTarget=false;
    try { await library.PrepareAsync(catalog,new(b.Id,"Symlink",StorageKind.Managed,[other],ImportLayout.FilesSeparate)); }catch(IOException) { protectedTarget=true; }
    Check(protectedTarget && Directory.GetFiles(outside,"*",SearchOption.AllDirectories).Length==0,"символическая ссылка назначения не выводит копии за библиотеку");
    var interrupted=new PreparedImport { Root=LibraryPaths.Full(b.RootPath),State="Copying" };
    var interruptedStage=Path.Combine(interrupted.Root,".collector-staging",interrupted.Id.ToString("N"));Directory.CreateDirectory(interruptedStage);
    File.WriteAllText(Path.Combine(interruptedStage,"partial.tmp"),"partial copy");
    File.WriteAllText(Path.Combine(data,"imports",interrupted.Id.ToString("N")+".json"),JsonSerializer.Serialize(interrupted));
    var otherHash=Hash(other);library.DiscardInterrupted(catalog,library.GetPending().Single());
    Check(!Directory.Exists(interruptedStage) && Hash(other)==otherHash && !library.HasPending,"прерванное копирование очищается только в собственной временной папке");
    using(var held=new FileStream(Path.Combine(data,"imports","import.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
    {
        var locked=false;try { await library.PrepareAsync(catalog,new(a.Id,"Locked",StorageKind.Linked,[other],ImportLayout.FilesSeparate)); }catch(IOException) { locked=true; }
        Check(locked,"параллельный импорт в другом окне блокируется до изменения файлов");
    }
    Console.WriteLine($"Итого: {checks} проверок хранения.");
}
finally { Directory.Delete(root,true); }

sealed class InlineProgress(Action<LibraryProgress> report) : IProgress<LibraryProgress>
{
    public void Report(LibraryProgress value)=>report(value);
}
