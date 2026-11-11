using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Media;
using Avalonia.Styling;
using Collector.Core;
using Collector.Core.Storage;
using Collector.Desktop;

AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .UseSkia().WithInterFont().SetupWithoutStarting();
var directory = Path.Combine(Path.GetTempPath(), "collector-ui-" + Guid.NewGuid());
try
{
    var music = new FakeMusic();
    var effects = new FakeEffects();
    var window = new MainWindow(directory, music, effects) { Width = 1440, Height = 920 };
    window.Show();
    Dispatcher.UIThread.RunJobs();
    if (!window.GetVisualDescendants().OfType<Button>().Any(b => b.Content as string == "⚙ Настройки"))
        throw new Exception("Не найдены настройки оформления");
    if (window.GetVisualDescendants().OfType<Button>().First(b => b.Content is StackPanel).Width != 206)
        throw new Exception("Мастерская не выбрана как основной дизайн");
    void SelectDesign(int index)
    {
        window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "⚙ Настройки")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        var dialog = window.OwnedWindows.Single();
        var picker = dialog.GetVisualDescendants().OfType<ComboBox>().Single(c => c.ItemsSource is IEnumerable<string> values && values.Count() == 5);
        picker.SelectedIndex = index;
        Dispatcher.UIThread.RunJobs();
        dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Применить оформление")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var expectedWidth = index switch { 0 => 238, 1 => 258, 2 => 206, 3 => 250, _ => 222 };
        for (var attempt = 0; attempt < 50; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            if (window.GetVisualDescendants().OfType<Button>().Any(b => b.Content is StackPanel && b.Width == expectedWidth)) break;
            Thread.Sleep(5);
        }
        window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
    }
    SelectDesign(0);
    if (File.ReadAllText(Path.Combine(directory, "ui.json")).Contains("atlas") != true
        || window.GetVisualDescendants().OfType<Button>().First(b => b.Content is StackPanel).Width != 238)
        throw new Exception("Оформление Атлас не применяется и не сохраняется");
    SelectDesign(2);
    if (!File.ReadAllText(Path.Combine(directory, "ui.json")).Contains("workshop")
        || window.GetVisualDescendants().OfType<Button>().First(b => b.Content is StackPanel).Width != 206)
        throw new Exception("Не удаётся вернуться к Мастерской");
    Console.WriteLine("PASS: настройки показывают все 5 вариантов, меняют интерфейс сразу и сохраняют выбор.");
    if (!music.IsPlaying || music.Starts != 1) throw new Exception("Музыка не включилась при открытии окна");
    music.Fail();
    Dispatcher.UIThread.RunJobs();
    if (music.IsPlaying || !window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("Музыка недоступна") == true))
        throw new Exception("Не обработана недоступность аудиоустройства");
    window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Музыка: выключена")
        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    Dispatcher.UIThread.RunJobs();
    window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Музыка: включена")
        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    Dispatcher.UIThread.RunJobs();
    if (music.IsPlaying || AudioPreferences.Load(Path.Combine(directory, "audio.json")).Enabled)
        throw new Exception("Выключение музыки не сохранено");
    var search = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Watermark != null);
    search.Text = "дракон";
    Dispatcher.UIThread.RunJobs();
    if (!window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("Найдено: 1") == true))
        throw new Exception("Не работает поиск в интерфейсе");
    search.Text = "";
    Dispatcher.UIThread.RunJobs();
    var card = window.GetVisualDescendants().OfType<Button>().First(b => b.Content is StackPanel);
    card.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    Dispatcher.UIThread.RunJobs();
    if (effects.Clicks == 0) throw new Exception("Нажатия карточек не запускают звуковые эффекты");
    var beforeHover = effects.Hovers;
    window.UpdateLayout();
    var currentCard = window.GetVisualDescendants().OfType<Button>().First(b => b.Content is StackPanel);
    var hoverPoint = currentCard.TranslatePoint(new Point(30, 30), window) ?? throw new Exception("Не найдены координаты карточки");
    window.MouseMove(hoverPoint);
    Dispatcher.UIThread.RunJobs();
    if (effects.Hovers != beforeHover + 1) throw new Exception("Наведение на карточку не запускает звук");
    var save = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Сохранить изменения");
    if (save.IsEnabled) throw new Exception("REGRESSION: открытие карточки без редактирования помечено как изменение и блокирует кнопки");
    var second = window.GetVisualDescendants().OfType<Button>().Where(b => b.Content is StackPanel).Skip(1).First();
    second.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    Dispatcher.UIThread.RunJobs();
    if (!window.GetVisualDescendants().OfType<TextBox>().Any(t => t.Text == "Лесной дракон"))
        throw new Exception("REGRESSION: переход ко второй карточке заблокирован");
    window.GetVisualDescendants().OfType<Button>().First(b => b.Content is StackPanel).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    Dispatcher.UIThread.RunJobs();
    var name = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Text == "Страж древних руин");
    name.Text = "Временное изменение";
    Dispatcher.UIThread.RunJobs();
    if (!save.IsEnabled) throw new Exception("Редактирование не включает сохранение");
    window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Музыка: выключена")
        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    Dispatcher.UIThread.RunJobs();
    window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Звуки: включены")
        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    Dispatcher.UIThread.RunJobs();
    if (effects.Enabled || AudioPreferences.Load(Path.Combine(directory, "audio.json")).EffectsEnabled || !save.IsEnabled)
        throw new Exception("Отключение эффектов не сохраняется или мешает редактированию");
    if (!music.IsPlaying || !save.IsEnabled || name.Text != "Временное изменение")
        throw new Exception("Музыка не переключается независимо от несохранённых правок");
    window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Музыка: включена")
        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    Dispatcher.UIThread.RunJobs();
    name.Text = "Страж древних руин";
    Dispatcher.UIThread.RunJobs();
    if (save.IsEnabled) throw new Exception("REGRESSION: возврат исходного текста оставляет навигацию заблокированной");
    name.Text = "Страж древних руин · проверено";
    Dispatcher.UIThread.RunJobs();
    window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Сохранить изменения")
        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    Dispatcher.UIThread.RunJobs();
    if (!new CatalogStore(directory).Load().Models.Any(m => m.Name.EndsWith("· проверено")))
        throw new Exception("Не работает сохранение из интерфейса");
    if (save.IsEnabled) throw new Exception("REGRESSION: после сохранения остаётся блокировка навигации");
    void Click(string label)
    {
        window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == label)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }
    Click("Скрыть карточку");
    if (!new CatalogStore(directory).Load().Models.Single(m => m.Name.EndsWith("· проверено")).Hidden)
        throw new Exception("Кнопка скрытия не сработала");
    Click("Скрытые модели");
    window.GetVisualDescendants().OfType<Button>().First(b => b.Content is StackPanel)
        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    Dispatcher.UIThread.RunJobs();
    Click("Восстановить карточку");
    Click("Все модели");
    for (var i = 0; i < 12; i++)
    {
        window.GetVisualDescendants().OfType<Button>().Where(b => b.Content is StackPanel).ElementAt(i % 8)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        if (save.IsEnabled) throw new Exception("REGRESSION: переключение карточек блокирует кнопки");
    }
    Click("+ Новая карточка");
    var newName = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Text == "Новая модель");
    newName.Text = "Создано проверкой";
    Dispatcher.UIThread.RunJobs();
    Click("Отменить изменения");
    if (save.IsEnabled) throw new Exception("Кнопка отмены не снимает блокировку");
    Click("Все модели");
    if (new CatalogStore(directory).Load().Models.Count != 9) throw new Exception("Новая карточка не создана");
    window.UpdateLayout();
    Dispatcher.UIThread.RunJobs();
    using var frame = window.CaptureRenderedFrame() ?? throw new Exception("Не создан кадр окна");
    var output = args.FirstOrDefault() ?? Path.Combine(directory, "preview.png");
    frame.Save(output);
    if (!File.Exists(Path.Combine(directory, "catalog.json"))) throw new Exception("Не сохранён каталог");
    window.Close();
    if (!effects.Disposed) throw new Exception("Эффекты не освобождены при закрытии");
    if (!music.Disposed || music.IsPlaying) throw new Exception("Музыка не остановлена при закрытии");
    var reopenedMusic = new FakeMusic();
    var reopenedEffects = new FakeEffects();
    var reopened = new MainWindow(directory, reopenedMusic, reopenedEffects);
    reopened.Show(); Dispatcher.UIThread.RunJobs();
    if (reopenedEffects.Enabled) throw new Exception("Настройка отключённых эффектов не восстановлена");
    if (reopenedMusic.Starts != 0) throw new Exception("После перезапуска не учтена настройка выключенной музыки");
    reopened.Close();
    void Pump(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
        if (!task.IsCompleted) throw new Exception("3D-просмотр завис во время загрузки/отрисовки");
        task.GetAwaiter().GetResult();
    }
    void CheckDialogContrast(Window dialog)
    {
        if (dialog.ActualThemeVariant != ThemeVariant.Dark)
            throw new Exception($"Тёмное окно {dialog.Title} унаследовало светлую тему каталога");
        var surface = ((ISolidColorBrush)dialog.Background!).Color;
        double Luminance(Color color)
        {
            double Linear(byte channel) => channel / 255d <= .04045 ? channel / 3294.6 : Math.Pow((channel / 255d + .055) / 1.055, 2.4);
            return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        }
        Color Over(Color color, Color background) => Color.FromRgb(
            (byte)Math.Round((color.R * color.A + background.R * (255 - color.A)) / 255d),
            (byte)Math.Round((color.G * color.A + background.G * (255 - color.A)) / 255d),
            (byte)Math.Round((color.B * color.A + background.B * (255 - color.A)) / 255d));
        foreach (var button in dialog.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyEnabled && b.Content is string))
        {
            var background = Over(((ISolidColorBrush)button.Background!).Color, surface);
            var foreground = Over(((ISolidColorBrush)button.Foreground!).Color, background);
            var first = Luminance(background); var second = Luminance(foreground);
            var contrast = (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
            if (contrast < 4.5) throw new Exception($"Текст кнопки «{button.Content}» сливается с фоном ({contrast:F2}:1)");
        }
    }
    var viewer = new ModelViewerWindow(demo: true);
    viewer.Show(); Dispatcher.UIThread.RunJobs();
    CheckDialogContrast(viewer);
    Pump(viewer.LoadingTask ?? throw new Exception("Не запустилась 3D-демонстрация"));
    var initialYaw=viewer.Viewport.Yaw;
    Thread.Sleep(120);Dispatcher.UIThread.RunJobs();Pump(viewer.Viewport.RefreshFrameAsync());
    if (viewer.Viewport.Yaw==initialYaw) throw new Exception("Автоматическое вращение не работает");
    viewer.Viewport.AutoRotate = false;
    viewer.UpdateLayout();
    Pump(viewer.Viewport.RefreshFrameAsync());
    if (viewer.Viewport.Scene == null || viewer.Viewport.RenderedFrames == 0)
        throw new Exception("Не отображена 3D-модель на пьедестале");
    using (var modelFrame = viewer.CaptureRenderedFrame() ?? throw new Exception("Нет кадра 3D"))
        modelFrame.Save(Path.Combine(Path.GetDirectoryName(output)!, "preview-3d.png"));
    var center=viewer.Viewport.TranslatePoint(new Point(viewer.Viewport.Bounds.Width/2,viewer.Viewport.Bounds.Height/2),viewer)!.Value;
    var yawBeforeDrag=viewer.Viewport.Yaw;
    viewer.MouseMove(center);viewer.MouseDown(center,MouseButton.Left);
    viewer.MouseMove(center+new Vector(35,12));viewer.MouseUp(center+new Vector(35,12),MouseButton.Left);
    Dispatcher.UIThread.RunJobs();
    if (viewer.Viewport.Yaw==yawBeforeDrag || viewer.Viewport.AutoRotate || viewer.Viewport.IsDragging)
        throw new Exception("Поворот мышью или завершение перетаскивания не работает");
    viewer.Width = 1100;viewer.Height=820;viewer.UpdateLayout();
    viewer.Viewport.AutoRotate=false;Pump(viewer.Viewport.RefreshFrameAsync());
    var staticPixels=viewer.Viewport.RenderPixelSize;
    if(staticPixels<=320) throw new Exception("Остановленная модель остаётся в размытом разрешении");
    viewer.Viewport.AutoRotate=true;viewer.Viewport.PreferSharp=true;Pump(viewer.Viewport.RefreshFrameAsync());
    var sharpTarget=(int)Math.Clamp(Math.Min(viewer.Viewport.Bounds.Width,viewer.Viewport.Bounds.Height)*viewer.RenderScaling,128,1024);
    var sharpDeadline=DateTime.UtcNow.AddSeconds(5);
    while(viewer.Viewport.RenderPixelSize!=sharpTarget && DateTime.UtcNow<sharpDeadline)
    { Dispatcher.UIThread.RunJobs();Thread.Sleep(5); }
    if(viewer.Viewport.RenderPixelSize!=sharpTarget) throw new Exception($"Режим чёткости не восстановил полный кадр: ожидалось {sharpTarget}px, получено {viewer.Viewport.RenderPixelSize}px (до включения: {staticPixels}px)");
    viewer.Viewport.PreferSharp=false;viewer.Viewport.AutoRotate=false;
    viewer.Width = 740; viewer.Height = 560; viewer.UpdateLayout();
    var closeViewer=viewer.GetVisualDescendants().OfType<Button>().Single(b=>b.Content as string=="Закрыть");
    var closeBottom=closeViewer.TranslatePoint(new Point(0,closeViewer.Bounds.Height),viewer);
    if(closeBottom==null || closeViewer.Bounds.Height<=0 || closeBottom.Value.Y>viewer.Bounds.Height)
        throw new Exception("Кнопка закрытия 3D-просмотра скрывается при уменьшении окна");
    viewer.Viewport.SetZoom(9); Pump(viewer.Viewport.RefreshFrameAsync());
    if (viewer.Viewport.Zoom > 1) throw new Exception("Масштаб может обрезать модель");
    var malformed = Path.Combine(directory, "broken.stl"); File.WriteAllText(malformed, "not a mesh");
    Pump(viewer.LoadFileAsync(malformed));
    if (viewer.Viewport.Scene != null || !viewer.StatusText.Contains("Не удалось"))
        throw new Exception("Повреждённый STL не обработан");
    var valid = Path.Combine(directory, "triangle.obj");
    File.WriteAllText(valid, "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");
    var oldLoad = viewer.LoadFileAsync(malformed);
    var newLoad = viewer.LoadFileAsync(valid);
    Pump(Task.WhenAll(oldLoad, newLoad));
    if (viewer.Viewport.Scene == null || !viewer.StatusText.Contains("triangle.obj"))
        throw new Exception("Старый результат загрузки заменяет новый");
    var closingLoad = viewer.LoadFileAsync(valid); viewer.Close(); Pump(closingLoad);
    var reopenedViewer = new ModelViewerWindow(demo: true); reopenedViewer.Show();
    Dispatcher.UIThread.RunJobs(); Pump(reopenedViewer.LoadingTask!); reopenedViewer.Close();
    Console.WriteLine("PASS: 3D-окно, отрисовка, изменение размера, ограничение масштаба, ошибка файла, быстрая смена файлов, закрытие во время загрузки и повторное открытие.");
    if(args.Length>1)
    {
        var heavy=new ModelViewerWindow([args[1]]) { Width=1100,Height=820 };heavy.Show();Dispatcher.UIThread.RunJobs();Pump(heavy.LoadingTask!);
        var timing=System.Diagnostics.Stopwatch.StartNew();var before=heavy.Viewport.RenderedFrames;
        while(timing.Elapsed.TotalSeconds<3) { Dispatcher.UIThread.RunJobs();Thread.Sleep(1); }
        var fps=(heavy.Viewport.RenderedFrames-before)/timing.Elapsed.TotalSeconds;
        if(heavy.Viewport.RenderedFrames-before<3) throw new Exception("Тяжёлая модель не обновляет кадры");
        Console.WriteLine($"MEASURE: тяжёлая модель {heavy.Viewport.Scene!.ModelTriangleCount:N0} граней: {fps:F1} FPS, {heavy.Viewport.RenderPixelSize}px, последний кадр {heavy.Viewport.LastRenderMilliseconds:F1} мс (headless)");
        heavy.Viewport.AutoRotate=false;
        timing.Restart();while(timing.Elapsed.TotalSeconds<.4) { Dispatcher.UIThread.RunJobs();Thread.Sleep(1); }
        Pump(heavy.Viewport.RefreshFrameAsync());
        if(heavy.Viewport.RenderPixelSize<640) throw new Exception("Тяжёлая модель не повышает чёткость на паузе");
        Thread.Sleep(30);Dispatcher.UIThread.RunJobs();
        using(var heavyFrame=heavy.CaptureRenderedFrame()!) heavyFrame.Save(Path.Combine(Path.GetDirectoryName(output)!,"heavy-model-quality.png"));
        heavy.Close();
    }
    var storageDirectory=Path.Combine(directory,"library-ui");Directory.CreateDirectory(storageDirectory);
    var storageStore=new CatalogStore(storageDirectory);var bulkCatalog=storageStore.Load();
    var bulkCollection=new CollectionEntry { Name="Большая коллекция" };bulkCatalog.Collections.Add(bulkCollection);
    for(var i=0;i<120;i++) bulkCatalog.Models.Add(new ModelEntry { Name="Модель "+i.ToString("D3"),Category="Набор",CollectionId=bulkCollection.Id });
    storageStore.Save(bulkCatalog);
    var bulkWindow=new MainWindow(storageDirectory,new FakeMusic(),new FakeEffects());
    bulkWindow.Show();Dispatcher.UIThread.RunJobs();
    if(bulkWindow.GetVisualDescendants().OfType<Button>().Count(b=>b.Content is StackPanel)!=48)
        throw new Exception("Большая библиотека создаёт больше 48 карточек одновременно");
    var chooser=bulkWindow.GetVisualDescendants().OfType<ComboBox>().Single(c=>c.ItemsSource is System.Collections.IEnumerable items && items.Cast<object>().Any(item=>item.ToString()?.Contains("Большая коллекция")==true));
    chooser.SelectedIndex=2;Dispatcher.UIThread.RunJobs();
    if(!bulkWindow.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text?.Contains("Найдено: 120")==true))
        throw new Exception("Не работает фильтр коллекции");
    bulkWindow.GetVisualDescendants().OfType<Button>().Single(b=>b.Content as string=="Следующая").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    Dispatcher.UIThread.RunJobs();
    if(!bulkWindow.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="49–96 из 120")) throw new Exception("Не работает следующая страница");
    var bulkSearch=bulkWindow.GetVisualDescendants().OfType<TextBox>().Single(t=>t.Watermark!=null);
    bulkSearch.Text="109";Dispatcher.UIThread.RunJobs();
    if(!bulkWindow.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="1–1 из 1")) throw new Exception("Поиск не сбрасывает страницу");
    bulkSearch.Text="";Dispatcher.UIThread.RunJobs();
    if(chooser.SelectedItem?.ToString()?.Contains("Большая коллекция")!=true) throw new Exception("Выбранная коллекция теряется при обновлении интерфейса");
    Thread.Sleep(30);Dispatcher.UIThread.RunJobs();
    using(var storageFrame=bulkWindow.CaptureRenderedFrame()!) storageFrame.Save(Path.Combine(Path.GetDirectoryName(output)!,"library-storage.png"));
    bulkWindow.Close();
    var editable=storageStore.Load();var manager=new CollectionsWindow(editable,storageStore,bulkCollection.Id);manager.Show();Dispatcher.UIThread.RunJobs();
    CheckDialogContrast(manager);
    manager.GetVisualDescendants().OfType<Button>().Single(b=>b.Content as string=="+ Новая коллекция").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();
    manager.GetVisualDescendants().OfType<TextBox>().Single(t=>t.Watermark=="Название коллекции").Text="Рабочая библиотека";
    var managedRoot=Path.Combine(storageDirectory,"managed-root");Directory.CreateDirectory(managedRoot);
    manager.GetVisualDescendants().OfType<TextBox>().Single(t=>t.Watermark?.Contains("Необязательно")==true).Text=managedRoot;
    manager.GetVisualDescendants().OfType<Button>().Single(b=>b.Content as string=="Сохранить коллекцию").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();
    var created=editable.Collections.SingleOrDefault(c=>c.Name=="Рабочая библиотека")??throw new Exception("Коллекция не создаётся в интерфейсе");
    if(storageStore.Load().Collections.All(c=>c.Id!=created.Id)) throw new Exception("Коллекция не сохранена");manager.Close();
    var wizard=new LibraryImportWindow(editable,storageStore,created.Id,"Фэнтези");wizard.Show();wizard.SetSources([valid],false);Dispatcher.UIThread.RunJobs();
    CheckDialogContrast(wizard);
    var initialRequest=wizard.BuildRequest();
    if(initialRequest.CollectionId!=created.Id || initialRequest.Category!="Фэнтези" || initialRequest.Storage!=StorageKind.Linked)
        throw new Exception("Мастер добавления потерял коллекцию, категорию или безопасный режим хранения");
    var mode=wizard.GetVisualDescendants().OfType<ComboBox>().Single(c=>c.ItemsSource is IEnumerable<string> values && values.FirstOrDefault()=="Оставить файлы на месте");
    mode.SelectedIndex=1;Dispatcher.UIThread.RunJobs();
    if(wizard.BuildRequest().Storage!=StorageKind.Managed) throw new Exception("Не выбирается режим копирования");
    using(var wizardFrame=wizard.CaptureRenderedFrame()!) wizardFrame.Save(Path.Combine(Path.GetDirectoryName(output)!,"library-import.png"));
    wizard.Close();
    Console.WriteLine("PASS: контраст кнопок в 3D-просмотре, коллекциях и импорте при светлой теме каталога (не менее 4,5:1).");
    Console.WriteLine("PASS: коллекции, сохранение папки, мастер добавления/копирования, фильтр, пагинация 120 карточек и сброс страницы поиска.");
    using var missing = new BackgroundMusic(Path.Combine(directory, "missing.wav"));
    var rejected = false;
    try { missing.Start(); } catch (FileNotFoundException) { rejected = true; }
    if (!rejected || missing.IsPlaying) throw new Exception("Не обработан отсутствующий аудиофайл");
    Console.WriteLine("PASS: эффекты (наведение, нажатие, выключатель, перезапуск), музыка (переключение, сохранение, перезапуск, закрытие), переключение 12 карточек, редактирование, возврат текста, сохранение, скрытие/восстановление, создание и отмена. Снимок: " + output);
}
finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

sealed class FakeMusic : IBackgroundMusic
{
    public bool IsPlaying { get; private set; }
    public int Starts { get; private set; }
    public bool Disposed { get; private set; }
    public event Action<string>? PlaybackFailed;
    public void Fail() { IsPlaying = false; PlaybackFailed?.Invoke("Тестовая недоступность аудиоустройства"); }
    public void Start() { IsPlaying = true; Starts++; }
    public void Stop() => IsPlaying = false;
    public void Dispose() { Stop(); Disposed = true; }
}

sealed class FakeEffects : IUiSoundEffects
{
    public bool Enabled { get; set; } = true;
    public int Clicks { get; private set; }
    public int Hovers { get; private set; }
    public bool Disposed { get; private set; }
    public event Action<string>? PlaybackFailed { add { } remove { } }
    public void Hover() { if (Enabled) Hovers++; }
    public void Click() { if (Enabled) Clicks++; }
    public void Dispose() { Enabled = false; Disposed = true; }
}
