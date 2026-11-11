using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Collector.Core;
using Collector.Core.Storage;

namespace Collector.Desktop;

public sealed class CollectionsWindow : Window
{
    private readonly Catalog _catalog;private readonly CatalogStore _store;
    private readonly ComboBox _list=new() { HorizontalAlignment=HorizontalAlignment.Stretch };
    private readonly TextBox _name=new() { Watermark="Название коллекции" };
    private readonly TextBox _root=new() { Watermark="Необязательно для подключения на месте" };
    private readonly TextBlock _message=new() { TextWrapping=TextWrapping.Wrap };
    private Guid? _editing;private bool _updating;
    public Guid? SelectedCollectionId=>_list.SelectedItem is CollectionEntry c?c.Id:null;
    public CollectionsWindow(Catalog catalog,CatalogStore store,Guid? selected=null)
    {
        RequestedThemeVariant=ThemeVariant.Dark;
        _catalog=catalog;_store=store;Background=Brush.Parse("#1A202B");Title="Коллекции и папки хранения";Width=650;Height=520;MinWidth=540;MinHeight=450;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new StackPanel { Margin=new Thickness(26),Spacing=14 };
        panel.Children.Add(new TextBlock { Text="Коллекции и папки",FontSize=25 });
        _list.ItemTemplate=new Avalonia.Controls.Templates.FuncDataTemplate<CollectionEntry>((c,_)=>new TextBlock { Text=c?.Name },true);
        panel.Children.Add(_list);
        var add=new Button { Content="+ Новая коллекция" };add.Click+=(_,_)=> { _editing=null;_name.Text="";_root.Text="";_root.IsEnabled=true;_message.Text="Заполните название. Для копирования выберите отдельную папку хранения."; };
        panel.Children.Add(add);panel.Children.Add(new TextBlock { Text="Название" });panel.Children.Add(_name);
        panel.Children.Add(new TextBlock { Text="Папка для копирования моделей (исходники сохраняются)" });panel.Children.Add(_root);
        var browse=new Button { Content="Выбрать папку…" };browse.Click+=async (_,_)=>
        {
            if(!_root.IsEnabled) { _message.Text="В этой папке уже есть модели коллекции. Перенос библиотеки пока не поддерживается.";return; }
            try { var result=await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title="Отдельная папка коллекции" });if(result.Count>0) _root.Text=result[0].TryGetLocalPath(); }
            catch(Exception e) { _message.Text=e.Message; }
        };panel.Children.Add(browse);
        var save=new Button { Content="Сохранить коллекцию",Background=Brush.Parse("#B7D979"),Foreground=Brushes.Black };
        save.Click+=(_,_)=>Save();panel.Children.Add(save);panel.Children.Add(_message);
        var close=new Button { Content="Готово" };close.Click+=(_,_)=>Close();panel.Children.Add(close);
        Content=new ScrollViewer { Content=panel };
        _list.SelectionChanged+=(_,_)=> { if(!_updating) LoadSelected(); };
        Refresh(selected??catalog.Collections[0].Id);
    }
    private void Refresh(Guid id)
    {
        _updating=true;_list.ItemsSource=_catalog.Collections.ToList();_list.SelectedItem=_catalog.Collections.First(c=>c.Id==id);_updating=false;LoadSelected();
    }
    private void LoadSelected()
    {
        if(_list.SelectedItem is not CollectionEntry c) return;
        _editing=c.Id;_name.Text=c.Name;_root.Text=c.RootPath;
        _root.IsEnabled=!_catalog.Models.Any(m=>m.CollectionId==c.Id && m.StorageKind==StorageKind.Managed);
        _message.Text=_root.IsEnabled?"Файлы можно подключать на месте. Для копирования задайте папку коллекции.":"В коллекции уже есть управляемые копии. Папка защищена от случайной смены.";
    }
    private void Save()
    {
        try
        {
            var name=_name.Text?.Trim()??"";if(name.Length==0) throw new InvalidDataException("Введите название коллекции.");
            if(_catalog.Collections.Any(c=>c.Id!=_editing && c.Name.Equals(name,StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Такое название коллекции уже используется.");
            var root=string.IsNullOrWhiteSpace(_root.Text)?"":LibraryPaths.Full(_root.Text);
            if(root.Length>0) LibraryPaths.ValidateRoot(_catalog,root,_editing);
            var old=_catalog.Collections.SingleOrDefault(c=>c.Id==_editing);
            if(old!=null && !LibraryPaths.Comparer.Equals(old.RootPath,root) && _catalog.Models.Any(m=>m.CollectionId==old.Id && m.StorageKind==StorageKind.Managed))
                throw new InvalidDataException("Сначала нужно отдельно перенести существующие копии. Смена пути заблокирована.");
            var created=old==null;var collection=old??new CollectionEntry { Categories=["Без категории"] };
            var previous=(collection.Name,collection.RootPath);collection.Name=name;collection.RootPath=root;
            if(created) _catalog.Collections.Add(collection);
            try { _store.Save(_catalog); }
            catch { if(created) _catalog.Collections.Remove(collection);else (collection.Name,collection.RootPath)=previous;throw; }
            Refresh(collection.Id);_message.Text="Коллекция сохранена.";
        }
        catch(Exception e) { _message.Text=e.Message; }
    }
}

public sealed class LibraryImportWindow : Window
{
    private readonly Catalog _catalog;private readonly CatalogStore _store;
    private readonly ComboBox _collection=new() { HorizontalAlignment=HorizontalAlignment.Stretch };
    private readonly ComboBox _categories=new() { HorizontalAlignment=HorizontalAlignment.Stretch };
    private readonly TextBox _category=new() { Watermark="Например: Миниатюры" };
    private readonly ComboBox _mode=new() { ItemsSource=new[]{"Оставить файлы на месте","Скопировать в коллекцию"},SelectedIndex=0,HorizontalAlignment=HorizontalAlignment.Stretch };
    private readonly ComboBox _layout=new() { HorizontalAlignment=HorizontalAlignment.Stretch,SelectedIndex=0 };
    private readonly TextBox _name=new() { Watermark="Название общей карточки" };
    private readonly TextBlock _sources=new() { Text="Источник не выбран",TextWrapping=TextWrapping.Wrap };
    private readonly TextBlock _location=new() { TextWrapping=TextWrapping.Wrap };
    private readonly TextBlock _error=new() { Foreground=Brush.Parse("#F0A599"),TextWrapping=TextWrapping.Wrap };
    private string[] _paths=[];private bool _folder,_updating;
    public LibraryImportWindow(Catalog catalog,CatalogStore store,Guid? selectedCollection=null,string? category=null)
    {
        RequestedThemeVariant=ThemeVariant.Dark;
        _catalog=catalog;_store=store;Background=Brush.Parse("#1A202B");Title="Добавить модели";Width=680;Height=760;MinWidth=560;MinHeight=580;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new StackPanel { Margin=new Thickness(26),Spacing=12 };
        panel.Children.Add(new TextBlock { Text="Добавить модели",FontSize=26 });
        panel.Children.Add(new TextBlock { Text="Коллекция" });panel.Children.Add(_collection);
        _collection.ItemTemplate=new Avalonia.Controls.Templates.FuncDataTemplate<CollectionEntry>((c,_)=>new TextBlock { Text=c?.Name },true);
        var configure=new Button { Content="Коллекции и папки…" };configure.Click+=async (_,_)=>
        {
            var dialog=new CollectionsWindow(_catalog,_store,(_collection.SelectedItem as CollectionEntry)?.Id);
            await dialog.ShowDialog(this);RefreshCollections(dialog.SelectedCollectionId);
        };panel.Children.Add(configure);
        panel.Children.Add(new TextBlock { Text="Категория: выберите или введите новую" });panel.Children.Add(_categories);panel.Children.Add(_category);
        var sourceButtons=new WrapPanel { Orientation=Orientation.Horizontal };
        var files=new Button { Content="Выбрать файлы…",Margin=new Thickness(0,0,12,0) };files.Click+=async (_,_)=>await PickAsync(false);
        var folder=new Button { Content="Выбрать папку…" };folder.Click+=async (_,_)=>await PickAsync(true);
        sourceButtons.Children.Add(files);sourceButtons.Children.Add(folder);panel.Children.Add(sourceButtons);panel.Children.Add(_sources);
        panel.Children.Add(new TextBlock { Text="Группировка" });panel.Children.Add(_layout);panel.Children.Add(_name);
        panel.Children.Add(new TextBlock { Text="Хранение" });panel.Children.Add(_mode);panel.Children.Add(_location);
        panel.Children.Add(new TextBlock { Text="При копировании исходники остаются на месте; в коллекции создаются проверенные копии. При подключении программа хранит ссылки на выбранные файлы.",TextWrapping=TextWrapping.Wrap,FontSize=12 });
        var confirm=new Button { Content="Добавить в коллекцию",Background=Brush.Parse("#B7D979"),Foreground=Brushes.Black };
        confirm.Click+=(_,_)=> { try { Close(BuildRequest()); }catch(Exception e) { _error.Text=e.Message; } };panel.Children.Add(confirm);panel.Children.Add(_error);
        var cancel=new Button { Content="Отмена" };cancel.Click+=(_,_)=>Close(null);panel.Children.Add(cancel);Content=new ScrollViewer { Content=panel };
        _collection.SelectionChanged+=(_,_)=> { if(!_updating) RefreshCategories(); };
        _categories.SelectionChanged+=(_,_)=> { if(_categories.SelectedItem is string value) _category.Text=value; };
        _mode.SelectionChanged+=(_,_)=>UpdateLocation();
        _layout.SelectionChanged+=(_,_)=>_name.IsVisible=!_folder && _layout.SelectedIndex==1;
        RefreshCollections(selectedCollection);_category.Text=category??"Без категории";SetSources([],false);
    }
    private void RefreshCollections(Guid? id)
    {
        _updating=true;_collection.ItemsSource=_catalog.Collections.ToList();_collection.SelectedItem=_catalog.Collections.FirstOrDefault(c=>c.Id==id)??_catalog.Collections[0];_updating=false;RefreshCategories();
    }
    private void RefreshCategories()
    {
        _categories.ItemsSource=(_collection.SelectedItem as CollectionEntry)?.Categories.Order().ToList();UpdateLocation();
    }
    private void UpdateLocation()
    {
        var collection=_collection.SelectedItem as CollectionEntry;
        _location.Text=_mode.SelectedIndex==0?"Файлы останутся в исходных папках.":collection?.RootPath.Length>0?"Копии будут храниться в: "+collection.RootPath:"Для копирования сначала выберите папку через «Коллекции и папки…».";
    }
    public void SetSources(string[] paths,bool folder)
    {
        _paths=paths;_folder=folder;
        _sources.Text=paths.Length==0?"Источник не выбран":folder?paths[0]:$"Выбрано файлов: {paths.Length}\n"+string.Join("\n",paths.Take(4).Select(Path.GetFileName));
        _layout.ItemsSource=folder?new[]{"Одна карточка на всю папку","Отдельная карточка на каждую подпапку"}:new[]{"Отдельная карточка на файл модели","Объединить файлы в одну карточку"};
        _layout.SelectedIndex=0;_name.IsVisible=false;
    }
    public LibraryImportRequest BuildRequest()
    {
        if(_paths.Length==0) throw new InvalidDataException("Сначала выберите файлы или папку.");
        var collection=_collection.SelectedItem as CollectionEntry??throw new InvalidDataException("Выберите коллекцию.");
        if(_mode.SelectedIndex==1 && collection.RootPath.Length==0) throw new InvalidDataException("У коллекции не задана папка для копий.");
        var layout=_folder?(_layout.SelectedIndex==1?ImportLayout.SubfoldersAsModels:ImportLayout.FolderAsModel):(_layout.SelectedIndex==1?ImportLayout.FilesTogether:ImportLayout.FilesSeparate);
        return new(collection.Id,_category.Text??"Без категории",_mode.SelectedIndex==1?StorageKind.Managed:StorageKind.Linked,_paths,layout,_name.Text);
    }
    private async Task PickAsync(bool folder)
    {
        try
        {
            if(folder)
            {
                var result=await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title="Папка модели или папка с несколькими моделями" });
                if(result.Count>0 && result[0].TryGetLocalPath() is string path) SetSources([path],true);
            }
            else
            {
                var result=await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title="Модели и сопутствующие файлы",AllowMultiple=true });
                if(result.Count>0) SetSources(result.Select(f=>f.TryGetLocalPath()).OfType<string>().ToArray(),false);
            }
        }
        catch(Exception e) { _error.Text=e.Message; }
    }
}
