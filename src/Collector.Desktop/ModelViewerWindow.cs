using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Collector.Core.Preview;

namespace Collector.Desktop;

public sealed class ModelViewerWindow : Window
{
    public ModelViewport Viewport { get; }=new();
    private readonly TextBlock _status=new() { Text="Выберите STL или OBJ",TextWrapping=TextWrapping.Wrap,Foreground=Brush.Parse("#A4B3C8") };
    private readonly TextBlock _performance=new() { TextWrapping=TextWrapping.Wrap,FontSize=12,Foreground=Brush.Parse("#A4B3C8") };
    private readonly ComboBox _quality=new() { ItemsSource=new[]{"Баланс: плавность / чёткость","Максимальная чёткость"},SelectedIndex=0,HorizontalAlignment=HorizontalAlignment.Stretch };
    private readonly ComboBox _files=new() { HorizontalAlignment=HorizontalAlignment.Stretch };
    private readonly ComboBox _upAxis=new() { ItemsSource=new[] { "Z — для 3D-печати","Y — для OBJ" },SelectedIndex=0,HorizontalAlignment=HorizontalAlignment.Stretch };
    private readonly Button _rotation=new() { Content="Остановить вращение" };
    private CancellationTokenSource? _loading;
    private Mesh3D? _mesh;
    private string _currentName = "Модель";
    private bool _currentDemo;
    private bool _closed,_initializing=true;
    private readonly bool _demo;
    public Task? LoadingTask { get; private set; }
    public string StatusText=>_status.Text??"";
    public bool IsLoading { get; private set; }

    public ModelViewerWindow(IEnumerable<string>? paths=null,bool demo=false)
    {
        // Keep controls consistent with the fixed dark viewport surface.
        RequestedThemeVariant=ThemeVariant.Dark;
        _demo=demo;
        Title=demo ? "3D-просмотр · демонстрационная скульптура" : "3D-просмотр · Коллекционер";
        Width=980;Height=720;MinWidth=720;MinHeight=540;Background=Brush.Parse("#121A26");
        var layout=new Grid { ColumnDefinitions=new ColumnDefinitions("*,260"),RowDefinitions=new RowDefinitions("*,Auto"),Margin=new Thickness(22),ColumnSpacing=22,RowSpacing=14 };
        layout.Children.Add(new Border { Background=Brush.Parse("#151F2D"),CornerRadius=new CornerRadius(16),Child=Viewport,ClipToBounds=true });
        var controls=new StackPanel { Spacing=14 };
        controls.Children.Add(new TextBlock { Text="Модель на пьедестале",FontSize=23,TextWrapping=TextWrapping.Wrap });
        controls.Children.Add(new TextBlock { Text="Мышью — поворот\nКолесом — масштаб до полного вписывания",TextWrapping=TextWrapping.Wrap,Foreground=Brush.Parse("#A4B3C8") });
        var open=new Button { Content="Открыть STL / OBJ",HorizontalAlignment=HorizontalAlignment.Stretch };
        open.Click+=async (_,_)=>await PickFileAsync();controls.Children.Add(open);
        var available=(paths??[]).Where(MeshReader.IsSupported).Distinct().ToList();
        _files.ItemsSource=available;_files.SelectedIndex=available.Count>0 ? 0 : -1;
        _files.ItemTemplate=new Avalonia.Controls.Templates.FuncDataTemplate<string>((value,_)=>new TextBlock
        { Text=Path.GetFileName(value),TextTrimming=TextTrimming.CharacterEllipsis },true);
        controls.Children.Add(_files);
        controls.Children.Add(new TextBlock { Text="Вертикальная ось" });controls.Children.Add(_upAxis);
        _rotation.HorizontalAlignment=HorizontalAlignment.Stretch;
        _rotation.Click+=(_,_)=> { Viewport.AutoRotate=!Viewport.AutoRotate;UpdateRotationButton(); };
        controls.Children.Add(_rotation);
        controls.Children.Add(new TextBlock { Text="Качество просмотра" });controls.Children.Add(_quality);
        _quality.SelectionChanged+=(_,_)=>Viewport.PreferSharp=_quality.SelectedIndex==1;
        controls.Children.Add(new TextBlock { Text="В режиме баланса при остановке вращения чёткость повышается автоматически.",TextWrapping=TextWrapping.Wrap,FontSize=12,Foreground=Brush.Parse("#A4B3C8") });
        controls.Children.Add(_performance);
        Viewport.FramePresented+=()=>_performance.Text=$"{(Viewport.AutoRotate || Viewport.IsDragging?$"{Viewport.FramesPerSecond:F1} FPS":"Пауза")} · {Viewport.RenderPixelSize}×{Viewport.RenderPixelSize} · кадр {Viewport.LastRenderMilliseconds:F0} мс";
        var fit=new Button { Content="Вписать модель",HorizontalAlignment=HorizontalAlignment.Stretch };
        fit.Click+=(_,_)=>Viewport.ResetView();controls.Children.Add(fit);
        var zoomOut=new Button { Content="Отдалить",HorizontalAlignment=HorizontalAlignment.Stretch };
        zoomOut.Click+=(_,_)=>Viewport.SetZoom(Viewport.Zoom-.1);controls.Children.Add(zoomOut);
        controls.Children.Add(new TextBlock { Text="Модель целиком помещается в кадр при любом угле. «Вписать» возвращает исходный вид.",TextWrapping=TextWrapping.Wrap,FontSize=12,Foreground=Brush.Parse("#A4B3C8") });
        var close=new Button { Content="Закрыть",HorizontalAlignment=HorizontalAlignment.Stretch };
        close.Click+=(_,_)=>Close();
        var panel=new ScrollViewer { Content=controls,HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var sidebar=new Grid { RowDefinitions=new RowDefinitions("*,Auto"),RowSpacing=12 };
        sidebar.Children.Add(panel);Grid.SetRow(close,1);sidebar.Children.Add(close);
        Grid.SetColumn(sidebar,1);layout.Children.Add(sidebar);
        Grid.SetRow(_status,1);Grid.SetColumnSpan(_status,2);layout.Children.Add(_status);Content=layout;
        _files.SelectionChanged+=(_,_)=>
        {
            if(!_initializing && _files.SelectedItem is string path)
            {
                _initializing=true;
                _upAxis.SelectedIndex=Path.GetExtension(path).Equals(".obj",StringComparison.OrdinalIgnoreCase)?1:0;
                _initializing=false;
                LoadingTask=LoadFileAsync(path);
            }
        };
        _upAxis.SelectionChanged+=(_,_)=>
        {
            if(!_initializing && !IsLoading && _mesh!=null)
            {
                var mesh=_mesh;LoadingTask=LoadAsync(_=>mesh,_currentName,_currentDemo);
            }
        };
        Viewport.PointerMoved+=(_,_)=>UpdateRotationButton();
        Viewport.RenderFailed+=message=>_status.Text="Не удалось отобразить модель: "+message;
        Opened+=(_,_)=>
        {
            _initializing=false;
            if(_files.SelectedItem is string path)
            {
                _initializing=true;
                _upAxis.SelectedIndex=Path.GetExtension(path).Equals(".obj",StringComparison.OrdinalIgnoreCase)?1:0;
                _initializing=false;
                LoadingTask=LoadFileAsync(path);
            }
            else if(_demo) LoadingTask=BuildSceneAsync(PreviewScene.Demo(),true);
        };
        Closed+=(_,_)=> { _closed=true;_loading?.Cancel();_mesh=null;Viewport.Dispose(); };
    }
    private void UpdateRotationButton()=>_rotation.Content=Viewport.AutoRotate ? "Остановить вращение":"Продолжить вращение";
    public Task LoadFileAsync(string path)=>LoadAsync(token=>MeshReader.Read(path,token),Path.GetFileName(path),false);
    private Task BuildSceneAsync(Mesh3D mesh,bool demo=false)=>LoadAsync(_=>mesh,demo ? "Демонстрационная скульптура" : "Модель",demo);
    private async Task LoadAsync(Func<CancellationToken,Mesh3D> read,string name,bool demo)
    {
        if(_closed) return;
        _loading?.Cancel();
        var current=new CancellationTokenSource();_loading=current;
        var token=current.Token;var axis=_upAxis.SelectedIndex==1 ? UpAxis.Y:UpAxis.Z;
        IsLoading=true;_mesh=null;Viewport.SetScene(null);_status.Text="Загрузка: "+name+"…";
        try
        {
            var result=await Task.Run(()=>
            {
                token.ThrowIfCancellationRequested();var mesh=read(token);
                var scene=PreviewScene.Create(mesh,axis,token);return(mesh,scene);
            },token);
            if(_closed || token.IsCancellationRequested || !ReferenceEquals(_loading,current)) return;
            _mesh=result.mesh;_currentName=name;_currentDemo=demo;Viewport.SetScene(result.scene);
            Title=demo ? "3D-просмотр · демонстрационная скульптура" : "3D-просмотр · "+name;
            _status.Text=$"{name} · {result.scene.ModelTriangleCount:N0} треугольников"+(demo ? " · демонстрация, не файл из коллекции":" · исходный файл не изменяется");
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested) { }
        catch(Exception e)
        {
            if(!_closed && ReferenceEquals(_loading,current)) _status.Text="Не удалось открыть модель: "+e.Message;
        }
        finally
        {
            if(ReferenceEquals(_loading,current)) { IsLoading=false;_loading=null; }
            current.Dispose();
        }
    }
    private async Task PickFileAsync()
    {
        try
        {
            var result=await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title="Модель для 3D-просмотра",AllowMultiple=false,
                FileTypeFilter=new[] { new FilePickerFileType("STL и OBJ") { Patterns=new[] { "*.stl","*.obj","*.STL","*.OBJ" } } }
            });
            if(_closed || result.Count==0) return;
            var path=result[0].TryGetLocalPath();if(path==null) { _status.Text="Нужен локально доступный файл.";return; }
            var paths=((IEnumerable<string>?)_files.ItemsSource??[]).ToList();
            if(!paths.Contains(path)) paths.Add(path);
            _initializing=true;_files.ItemsSource=paths;_files.SelectedItem=path;
            _upAxis.SelectedIndex=Path.GetExtension(path).Equals(".obj",StringComparison.OrdinalIgnoreCase)?1:0;_initializing=false;
            LoadingTask=LoadFileAsync(path);await LoadingTask;
        }
        catch(Exception e) { if(!_closed) _status.Text="Не удалось выбрать файл: "+e.Message; }
    }
}
