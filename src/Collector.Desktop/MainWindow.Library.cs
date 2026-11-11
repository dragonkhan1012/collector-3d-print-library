using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Collector.Core;
using Collector.Core.Preview;
using Collector.Core.Storage;

namespace Collector.Desktop;

public sealed partial class MainWindow
{
    private readonly LibraryStorage _library = null!;
    private ComboBox _collectionChooser = null!;
    private Button _pendingImportsButton = null!;
    private Guid? _collectionId;
    private bool _changingCollection;
    private List<CollectionChoice> _collectionChoices=[];
    private sealed record CollectionChoice(Guid? Id,string Name);
    private string CollectionName()=>_catalog.Collections.FirstOrDefault(c=>c.Id==_collectionId)?.Name??"Все коллекции";
    private void InitializeCollectionChooser()
    {
        _collectionChooser.SelectionChanged+=(_,_)=>
        {
            if(_changingCollection || !_ready || _collectionChooser.SelectedIndex<0 || _collectionChooser.SelectedIndex>=_collectionChoices.Count) return;
            var choice=_collectionChoices[_collectionChooser.SelectedIndex];
            if(choice.Id==_collectionId) return;
            if(!CanLeave()) { RefreshCollectionChoices();return; }
            _collectionId=choice.Id;_category=null;_selected=null;Refresh();
        };
    }
    private void RefreshCollectionChoices()
    {
        _changingCollection=true;
        var choices=new List<CollectionChoice> { new(null,"Все коллекции") };
        choices.AddRange(_catalog.Collections.Select(c=>new CollectionChoice(c.Id,c.Name)));
        _collectionChoices=choices;
        var names=choices.Select(c=>c.Name).ToArray();
        if(_collectionChooser.ItemsSource is not IEnumerable<string> current || !current.SequenceEqual(names)) _collectionChooser.ItemsSource=names;
        _collectionChooser.SelectedIndex=choices.FindIndex(c=>c.Id==_collectionId);
        _changingCollection=false;
    }
    private async Task ManageCollectionsAsync()
    {
        if(!CanLeave()) return;
        var dialog=new CollectionsWindow(_catalog,_store,_collectionId);
        await dialog.ShowDialog(this);_collectionId=dialog.SelectedCollectionId;_category=null;_selected=null;Refresh();
    }
    private void StorageBusy(bool busy)
    {
        _busy=busy;_detail.IsEnabled=!busy;_collectionChooser.IsEnabled=!busy;
        _cancelScanButton.IsVisible=busy;_cancelScanButton.IsEnabled=true;
        if(!busy) { _scanCancellation?.Dispose();_scanCancellation=null; }
    }
    private async Task AddModelsAsync()
    {
        if(!CanLeave()) return;
        var dialog=new LibraryImportWindow(_catalog,_store,_collectionId,_category);
        var request=await dialog.ShowDialog<LibraryImportRequest?>(this);
        Refresh();if(request==null) return;
        StorageBusy(true);_scanCancellation=new CancellationTokenSource();var token=_scanCancellation.Token;
        try
        {
            var progress=new Progress<LibraryProgress>(p=>
            {
                if(_closed) return;
                _status.Text=$"{p.Message} ({p.Completed}/{p.Total})";
                if(p.Finalizing) _cancelScanButton.IsEnabled=false;
            });
            _status.Text="Подготовка списка файлов…";
            var prepared=await Task.Run(()=>_library.PrepareAsync(_catalog,request,progress,token),token);
            if(_closed) return;
            var count=_library.Commit(_catalog,_store,prepared);
            _collectionId=request.CollectionId;_category=null;_hidden=false;_planned=false;
            _selected=prepared.Models.LastOrDefault();_search.Text="";_rarity.SelectedIndex=0;Refresh();
            _status.Text=$"Добавлено: {count}. Замечаний: {prepared.Warnings.Count}. Исходники сохранены.";
            if(prepared.Warnings.Count>0) await ShowWarnings(prepared.Warnings);
        }
        catch(OperationCanceledException) { _status.Text="Добавление отменено. Исходные файлы сохранены."; }
        catch(Exception e) { _status.Text="Не удалось завершить добавление: "+e.Message; }
        finally { StorageBusy(false);_pendingImportsButton.IsVisible=_library.HasPending; }
    }
    private async Task RecoverImportsAsync()
    {
        if(!CanLeave()) return;
        var operations=_library.GetPending();if(operations.Count==0) { _pendingImportsButton.IsVisible=false;return; }
        var popup=new Window { Title="Незавершённые добавления",Width=660,Height=500,WindowStartupLocation=WindowStartupLocation.CenterOwner };
        var panel=new StackPanel { Margin=new Thickness(24),Spacing=14 };
        panel.Children.Add(Text("Незавершённые операции",23));
        foreach(var operation in operations)
        {
            var label=Text($"{operation.CreatedUtc.ToLocalTime():g} · {operation.Models.Count} моделей · "+(operation.State=="Copying"?"копирование прервано":"копии готовы"),13);
            panel.Children.Add(label);
            var action=Button(operation.State=="Copying"?"Убрать временные копии; исходники сохранятся":"Восстановить карточки",()=>{});
            action.Click+=async (_,_)=>await ResolveAsync(operation);
            panel.Children.Add(action);
            async Task ResolveAsync(PreparedImport op)
            {
                if(_busy) return;
                action.IsEnabled=false;StorageBusy(true);_scanCancellation=new CancellationTokenSource();var token=_scanCancellation.Token;
                try
                {
                    if(op.State=="Copying") await Task.Run(()=>_library.DiscardInterrupted(_catalog,op),token);
                    else
                    {
                        var recovered=await Task.Run(()=>_library.RecoverAsync(_catalog,op,token),token);
                        token.ThrowIfCancellationRequested();
                        if(_closed) return;
                        _library.Commit(_catalog,_store,recovered);
                    }
                    label.Text="Готово. Исходные файлы сохранены.";Refresh();
                }
                catch(Exception e) { label.Text=e.Message;action.IsEnabled=true; }
                finally { StorageBusy(false); }
            }
        }
        panel.Children.Add(Text("Если копирование прервано, можно убрать только созданные временные копии и повторить добавление. Готовые копии восстанавливаются в каталог без повторного копирования.",12));
        panel.Children.Add(Button("Отменить проверку",()=>_scanCancellation?.Cancel()));
        var close=Button("Закрыть",popup.Close);panel.Children.Add(close);popup.Content=new ScrollViewer { Content=panel };
        popup.Closing+=(_,_)=> { if(_busy) _scanCancellation?.Cancel(); };
        await popup.ShowDialog(this);_pendingImportsButton.IsVisible=_library.HasPending;
    }
    private async Task NewCategoryAsync()
    {
        if(!CanLeave()) return;
        if(_collectionId==null) { _status.Text="Сначала выберите конкретную коллекцию в списке слева.";return; }
        var dialog=new Window { Title="Новая категория",Width=430,Height=245,WindowStartupLocation=WindowStartupLocation.CenterOwner };
        var panel=new StackPanel { Margin=new Thickness(24),Spacing=14 };var input=new TextBox { Watermark="Название категории" };
        panel.Children.Add(Text("Новая категория",22));panel.Children.Add(input);
        panel.Children.Add(Button("Создать",()=> { if(!string.IsNullOrWhiteSpace(input.Text)) dialog.Close(input.Text.Trim()); }));
        panel.Children.Add(Button("Отмена",()=>dialog.Close(null)));dialog.Content=panel;
        var name=await dialog.ShowDialog<string?>(this);if(name==null) return;
        var collection=_catalog.Collections.First(c=>c.Id==_collectionId);
        if(collection.Categories.Contains(name)) { _status.Text="Такая категория уже есть.";return; }
        collection.Categories.Add(name);
        if(!Persist()) { collection.Categories.Remove(name);return; }
        _category=name;_selected=null;Refresh();_status.Text="Категория создана. Добавьте в неё модели.";
    }
    private async Task AttachFilesAsync(ModelEntry model)
    {
        if(!CanLeave()) return;
        try
        {
            var chosen=await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title="Прикрепить файлы на текущем месте",AllowMultiple=true });
            if(chosen.Count==0) return;
            StorageBusy(true);_scanCancellation=new CancellationTokenSource();var token=_scanCancellation.Token;
            var files=await Task.Run(()=>LibraryMaintenance.PrepareAttachments(model,chosen.Select(f=>f.TryGetLocalPath()).OfType<string>(),token),token);
            token.ThrowIfCancellationRequested();if(_closed) return;
            var previous=(model.Files,model.FolderPath,model.TrackFolder,model.Demo,model.PreviewPath);
            model.Files=model.Files.Concat(files).ToList();
            if(model.FolderPath.Length==0 && files.Count>0) { model.FolderPath=Path.GetDirectoryName(files[0].ExternalPath!)!;model.TrackFolder=false; }
            if(files.Count>0) model.Demo=false;
            model.PreviewPath??=files.Where(f=>LibraryScanner.IsImage(f.RelativePath)).Select(f=>f.ExternalPath).FirstOrDefault();
            if(!Persist()) { (model.Files,model.FolderPath,model.TrackFolder,model.Demo,model.PreviewPath)=previous;return; }
            Refresh();_status.Text=$"Прикреплено файлов: {files.Count}. Они остаются на текущем месте.";
        }
        catch(OperationCanceledException) { _status.Text="Прикрепление отменено."; }
        catch(Exception e) { _status.Text=e.Message; }
        finally { if(_busy) StorageBusy(false); }
    }
    private async Task VerifyFilesAsync(ModelEntry model)
    {
        if(!CanLeave()) return;
        StorageBusy(true);_scanCancellation=new CancellationTokenSource();var token=_scanCancellation.Token;
        try
        {
            _status.Text="Проверка файлов…";
            var result=await Task.Run(()=>LibraryMaintenance.Verify(model,token),token);
            token.ThrowIfCancellationRequested();if(_closed) return;
            var before=(model.Files,model.PreviewPath,model.FolderUnavailable,model.StorageCheckedUtc);
            (model.Files,model.PreviewPath,model.FolderUnavailable,model.StorageCheckedUtc)=(result.Files,result.PreviewPath,result.FolderUnavailable,result.CheckedUtc);
            if(!Persist()) { (model.Files,model.PreviewPath,model.FolderUnavailable,model.StorageCheckedUtc)=before;return; }
            Refresh();_status.Text=result.FolderUnavailable?"Папка недоступна; сведения о файлах сохранены.":$"Проверка завершена. Файлов: {model.Files.Count}, не найдены: {model.Files.Count(f=>f.Missing)}.";
        }
        catch(OperationCanceledException) { _status.Text="Проверка отменена."; }
        catch(Exception e) { _status.Text="Проверка не завершена: "+e.Message; }
        finally { StorageBusy(false); }
    }
    private async Task ShowResourcesAsync(ModelEntry model)
    {
        var dialog=new Window { Title="Файлы · "+model.Name,Width=800,Height=600,WindowStartupLocation=WindowStartupLocation.CenterOwner };
        var layout=new Grid { Margin=new Thickness(24),RowDefinitions=new RowDefinitions("Auto,*,Auto"),RowSpacing=14 };
        layout.Children.Add(Text(model.Name+" · "+model.Files.Count+" файлов",22));
        var list=new ListBox { ItemsSource=model.Files.OrderBy(f=>f.RelativePath).ToList() };
        list.ItemTemplate=new Avalonia.Controls.Templates.FuncDataTemplate<ModelFile>((file,_)=>new TextBlock
        { Text=file==null?"":$"{file.RelativePath}  ·  {FormatSize(file.Size)}"+(file.Missing?" · не найден":"")+(file.ExternalPath!=null?" · ссылка":""),TextWrapping=Avalonia.Media.TextWrapping.Wrap },true);
        Grid.SetRow(list,1);layout.Children.Add(list);var actions=new WrapPanel { Orientation=Orientation.Horizontal };
        actions.Children.Add(Button("Открыть в 3D",()=>
        {
            if(list.SelectedItem is ModelFile file && MeshReader.IsSupported(file.RelativePath))
            {
                try { _ = new ModelViewerWindow([LibraryPaths.Resolve(model,file)]).ShowDialog(dialog); }catch(Exception e) { _status.Text=e.Message; }
            }
        }));
        actions.Children.Add(Button("Показать папку файла",()=>
        {
            if(list.SelectedItem is ModelFile file)
                try { OpenFolder(Path.GetDirectoryName(LibraryPaths.Resolve(model,file))!); }catch(Exception e) { _status.Text=e.Message; }
        }));
        actions.Children.Add(Button("Закрыть",dialog.Close));Grid.SetRow(actions,2);layout.Children.Add(actions);dialog.Content=layout;await dialog.ShowDialog(this);
    }
    private async Task LoadModelPreviewAsync(ModelEntry model,Border host)
    {
        Bitmap? bitmap=null;
        try
        {
            bitmap=await Task.Run(()=>
            {
                var file=model.PreviewPath!;
                if(new FileInfo(file).Length>25_000_000) return null;
                using var input=File.OpenRead(file);return Bitmap.DecodeToWidth(input,600);
            });
            if(_closed || host.Parent==null || _selected?.Id!=model.Id) { bitmap?.Dispose();return; }
            if(bitmap!=null) { _preview?.Dispose();_preview=bitmap;host.Child=new Image { Source=bitmap,Height=170,Stretch=Avalonia.Media.Stretch.Uniform }; }
        }
        catch { bitmap?.Dispose();if(!_closed && host.Parent!=null) host.Child=Text("Превью недоступно",12,"#929BAD"); }
    }
}
