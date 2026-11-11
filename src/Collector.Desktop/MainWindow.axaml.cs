using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Collector.Core;
using Collector.Core.Preview;
using Collector.Core.Storage;

namespace Collector.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly CatalogStore _store = null!;
    private readonly IBackgroundMusic _music = null!;
    private readonly IUiSoundEffects _soundEffects = null!;
    private Button _effectsButton = null!;
    private readonly AudioPreferences _audioPreferences = null!;
    private readonly string _audioPreferencesPath = "";
    private readonly string _designPreferencesPath = "";
    private readonly DesignPreferences _designPreferences = new();
    private Button _musicButton = null!;
    private bool _closed;
    private Catalog _catalog = new();
    private WrapPanel _cards = null!;
    private StackPanel _categories = null!;
    private StackPanel _detail = null!;
    private TextBlock _count = null!;
    private TextBlock _status = null!;
    private TextBox _search = null!;
    private ComboBox _rarity = null!;
    private ComboBox _sort = null!;
    private Button _saveButton = null!;
    private Border _editNotice = null!;
    private Button _cancelScanButton = null!;
    private CancellationTokenSource? _scanCancellation;
    private ModelEntry? _selected;
    private string? _category;
    private bool _hidden;
    private bool _planned;
    private bool _busy;
    private bool _ready;
    private string? _loadError;
    private Bitmap? _preview;
    private TextBox? _name, _categoryEdit, _author, _tags, _notes;
    private ComboBox? _rarityEdit, _stateEdit;
    private bool _dirty;
    private bool _buildingDetail;
    private int _pageIndex;
    private const int PageSize = 48;
    private TextBlock _pageInfo = null!;
    private Button _previousPage = null!;
    private Button _nextPage = null!;

    private DesignOption CurrentDesign => DesignCatalog.Get(_designPreferences.DesignId);
    private IBrush Brush(string color)
    {
        var key = color switch
        {
            "#121720" => "CatalogBackgroundBrush", "#191F2A" => "SidebarBackgroundBrush",
            "#1C2431" => "CardBackgroundBrush", "#242B39" or "#252E3D" => "ArtworkBackgroundBrush",
            "#10151D" => "FooterBackgroundBrush", "#E9EDF5" => "PrimaryTextBrush",
            "#929BAD" or "#A4AFBE" => "MutedTextBrush", "#788399" => "SubtleTextBrush",
            "#303A4A" => "OutlineBrush", "#B7D979" or "#C8E6A4" => "AccentBrush",
            "#15200E" => "AccentTextBrush", _ => null
        };
        return key != null && this.TryFindResource(key, out var value) && value is IBrush brush
            ? brush : Avalonia.Media.Brush.Parse(color);
    }
    private TextBlock Text(string value, double size = 14, string color = "#E9EDF5") => new()
        { Text = value, FontSize = size, Foreground = Brush(color), TextWrapping = TextWrapping.Wrap };
    private Button Button(string label, Action action, bool accent = false)
    {
        var button = new Button { Content = label };
        button.Classes.Add(accent ? "accent" : "action");
        WireSounds(button); button.Click += (_, _) => action(); return button;
    }

    private static string RarityColor(Rarity rarity) => rarity switch
    {
        Rarity.Common => "#9DA9B9", Rarity.Uncommon => "#85BC8D", Rarity.Rare => "#80AFF5",
        Rarity.Epic => "#BE9AF3", _ => "#E6BE70"
    };

    public MainWindow() : this("") { }
    public MainWindow(string directory, IBackgroundMusic? music = null, IUiSoundEffects? soundEffects = null)
    {
        LoadView();
        if (Design.IsDesignMode) { ShowDesignPreview(); return; }
        if (string.IsNullOrWhiteSpace(directory))
            directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Collector3D");
        _store = new CatalogStore(directory);
        _library = new LibraryStorage(directory);
        _audioPreferencesPath = Path.Combine(directory, "audio.json");
        _designPreferencesPath = Path.Combine(directory, "ui.json");
        _designPreferences = DesignPreferences.Load(_designPreferencesPath);
        _audioPreferences = AudioPreferences.Load(_audioPreferencesPath);
        _music = music ?? new BackgroundMusic(Path.Combine(AppContext.BaseDirectory, "Assets", "Audio", "moonlit-archive.wav"));
        _soundEffects = soundEffects ?? new UiSoundEffects(new SystemUiSoundPlayer(Path.Combine(AppContext.BaseDirectory, "Assets", "Audio")));
        _soundEffects.Enabled = _audioPreferences.EffectsEnabled;
        UpdateEffectsButton();
        ToolTip.SetTip(_effectsButton, "Тихие звуки наведения и нажатия. Настройка независима от фоновой музыки.");
        _soundEffects.PlaybackFailed += message => Dispatcher.UIThread.Post(() =>
        {
            if (_closed) return;
            UpdateEffectsButton();
            _status.Text = "Звуки интерфейса недоступны: " + message;
        });
        ToolTip.SetTip(_musicButton, "Лунный архив · оригинальная фоновая композиция. Нажмите, чтобы включить или выключить.");
        _music.PlaybackFailed += message => Dispatcher.UIThread.Post(() =>
        {
            if (_closed) return;
            UpdateMusicButton();
            _status.Text = "Музыка недоступна: " + message;
        });
        _cancelScanButton.IsVisible = false;
        InitializeCollectionChooser();
        try { _catalog = _store.Load(); _store.Save(_catalog); }
        catch (Exception e) { _loadError = e.Message; }
        WireView();
        ApplyDesignResources();
        _search.TextChanged += (_, _) => { _pageIndex=0;RefreshCards(); };
        WireSounds(_rarity); WireSounds(_sort);
        _rarity.SelectionChanged += (_, _) => { _pageIndex=0;RefreshCards(); };
        _sort.SelectionChanged += (_, _) => { _pageIndex=0;RefreshCards(); };
        _ready = true;
        Refresh();
        if (_loadError != null)
        {
            _detail.Children.Clear();
            _detail.Children.Add(Text("Не удалось открыть каталог", 22));
            _detail.Children.Add(Text(_loadError, 14, "#F0A599"));
            _detail.Children.Add(Text("Исходный файл не заменён. Закройте приложение и восстановите catalog.json из catalog.json.bak. Папка данных: " + directory));
        }
        Closing += (_, e) =>
        {
            if (_busy)
            {
                _scanCancellation?.Cancel();
                if (!_cancelScanButton.IsEnabled) { e.Cancel = true; _status.Text = "Завершается сохранение. Повторите закрытие через секунду."; }
            }
            else if (_dirty) { e.Cancel = true; _status.Text = "Сохраните изменения или нажмите «Отменить»."; }
        };
        Opened += (_, _) =>
        {
            if (_audioPreferences.Enabled) StartMusic();
            UpdateMusicButton();
        };
        Closed += (_, _) => { _closed = true; _soundEffects.Dispose(); _music.Dispose(); _preview?.Dispose(); };
    }

    private void LoadView()
    {
        AvaloniaXamlLoader.Load(this);
        _effectsButton = this.FindControl<Button>("EffectsButton")!;
        _musicButton = this.FindControl<Button>("MusicButton")!;
        _cards = this.FindControl<WrapPanel>("Cards")!;
        _categories = this.FindControl<StackPanel>("Categories")!;
        _detail = this.FindControl<StackPanel>("Detail")!;
        _count = this.FindControl<TextBlock>("Count")!;
        _status = this.FindControl<TextBlock>("Status")!;
        _search = this.FindControl<TextBox>("Search")!;
        _rarity = this.FindControl<ComboBox>("RarityFilter")!;
        _sort = this.FindControl<ComboBox>("SortFilter")!;
        _saveButton = this.FindControl<Button>("SaveButton")!;
        _editNotice = this.FindControl<Border>("EditNotice")!;
        _cancelScanButton = this.FindControl<Button>("CancelScanButton")!;
        _pageInfo = this.FindControl<TextBlock>("PageInfo")!;
        _previousPage = this.FindControl<Button>("PreviousPage")!;
        _nextPage = this.FindControl<Button>("NextPage")!;
        _collectionChooser = this.FindControl<ComboBox>("CollectionChooser")!;
        _pendingImportsButton = this.FindControl<Button>("PendingImportsButton")!;
        _rarity.ItemsSource = new[] { "Любая редкость" }.Concat(Labels.Rarities).ToArray();
        _rarity.SelectedIndex = 0;
        _sort.ItemsSource = new[] { "Сначала новые", "По названию", "По редкости" }; _sort.SelectedIndex = 0;
    }
    private void ApplyDesignResources()
    {
        Resources.MergedDictionaries.Clear();
        Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Collector.Desktop/"))
            { Source = new Uri($"avares://Collector.Desktop/Themes/{CurrentDesign.ResourceFile}.axaml") });
    }
    private void WireView()
    {
        void Click(string name, Action action)
        {
            var button = this.FindControl<Button>(name)!;
            WireSounds(button); button.Click += (_, _) => action();
        }
        Click("CollectionsButton", () => _ = ManageCollectionsAsync());
        Click("AllModelsButton", () => SetScope(null, false, false));
        Click("PlannedButton", () => SetScope(null, false, true));
        Click("RecentButton", () => { if (!CanLeave()) return; _search.Text = ""; _sort.SelectedIndex = 0; SetScope(null, false, false); });
        Click("SettingsButton", () => _ = OpenDesignSettingsAsync());
        Click("NewCategoryButton", () => _ = NewCategoryAsync());
        Click("HiddenButton", () => SetScope(null, true, false));
        Click("DemoViewerButton", () => _ = new ModelViewerWindow(demo: true).ShowDialog(this));
        Click("ImportButton", () => _ = AddModelsAsync());
        Click("PendingImportsButton", () => _ = RecoverImportsAsync());
        Click("CancelScanButton", () => _scanCancellation?.Cancel());
        Click("MusicButton", ToggleMusic); Click("EffectsButton", ToggleEffects);
        Click("NewModelButton", NewModel); Click("SaveButton", SaveEdit);
        Click("CancelEditButton", () => { ShowDetails(); _status.Text = "Изменения отменены."; });
        Click("PreviousPage", () => { if (_pageIndex > 0) { _pageIndex--; RefreshCards(); } });
        Click("NextPage", () => { _pageIndex++; RefreshCards(); });
    }
    private void ShowDesignPreview()
    {
        _catalog = DemoCatalog.Create(); _selected = _catalog.Models[0];
        ApplyDesignResources();
        _collectionChooser.ItemsSource = new[] { "Демонстрация" }; _collectionChooser.SelectedIndex = 0;
        foreach (var model in _catalog.Models) _cards.Children.Add(BuildCard(model));
        foreach (var category in _catalog.Models.Select(m => m.Category).Distinct())
        { var button = new Button { Content = category }; button.Classes.Add("nav"); _categories.Children.Add(button); }
        _count.Text = "Демонстрация / Все модели / Найдено: " + _catalog.Models.Count;
        _pageInfo.Text = $"1–{_catalog.Models.Count} из {_catalog.Models.Count}";
        ShowDetails();
    }

    private void WireSounds(Control control)
    {
        control.PointerEntered += (_, _) => { if (_ready && control.IsEffectivelyEnabled) _soundEffects.Hover(); };
        if (control is Button button)
            button.Click += (_, _) => { if (_ready && button.IsEffectivelyEnabled) _soundEffects.Click(); };
        else
            control.AddHandler(InputElement.PointerPressedEvent,
                (_, _) => { if (_ready && control.IsEffectivelyEnabled) _soundEffects.Click(); }, RoutingStrategies.Tunnel);
    }

    private void UpdateEffectsButton() => _effectsButton.Content = _soundEffects.Enabled ? "Звуки: включены" : "Звуки: выключены";
    private void ToggleEffects()
    {
        _soundEffects.Enabled = !_soundEffects.Enabled;
        _audioPreferences.EffectsEnabled = _soundEffects.Enabled;
        UpdateEffectsButton();
        try { _audioPreferences.Save(_audioPreferencesPath); }
        catch (Exception e) { _status.Text = "Настройка звуков действует до закрытия: " + e.Message; }
    }

    private void UpdateMusicButton() => _musicButton.Content = _music.IsPlaying ? "Музыка: включена" : "Музыка: выключена";

    private void StartMusic()
    {
        try { _music.Start(); }
        catch (Exception e) { _status.Text = "Музыка недоступна: " + e.Message; }
    }

    private void ToggleMusic()
    {
        if (_music.IsPlaying) { _music.Stop(); _audioPreferences.Enabled = false; }
        else { StartMusic(); _audioPreferences.Enabled = _music.IsPlaying; }
        UpdateMusicButton();
        try { _audioPreferences.Save(_audioPreferencesPath); }
        catch (Exception e) { _status.Text = "Настройка музыки действует до закрытия: " + e.Message; }
    }

    private bool CanLeave()
    {
        if (_selected != null && !_buildingDetail) MarkDirty();
        if (_loadError != null) { _status.Text = "Каталог не открыт. Существующий файл защищён от перезаписи."; return false; }
        if (_busy) { _status.Text = "Идёт чтение папки…"; return false; }
        if (_dirty) { _status.Text = "Сначала сохраните карточку или нажмите «Отменить»."; return false; }
        return true;
    }
    private void SetScope(string? category, bool hidden, bool planned)
    {
        if (!CanLeave()) return;
        _pageIndex=0;
        _category = category; _hidden = hidden; _planned = planned; _selected = null; Refresh();
    }
    private void Refresh()
    {
        RefreshCollectionChoices();
        _pendingImportsButton.IsVisible = _library.HasPending;
        _categories.Children.Clear();
        var scope = _catalog.Models.Where(m => !m.Hidden && (_collectionId == null || m.CollectionId == _collectionId)).ToList();
        var categoryNames = _catalog.Collections.Where(c => _collectionId == null || c.Id == _collectionId)
            .SelectMany(c => c.Categories).Concat(scope.Select(m => m.Category)).Distinct().Order();
        foreach (var category in categoryNames)
        {
            var count = scope.Count(m => m.Category == category);
            var button = Button($"{category}   {count}", () => SetScope(category, false, false));
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            _categories.Children.Add(button);
        }
        RefreshCards(); ShowDetails();
    }
    private void RefreshCards()
    {
        if (!_ready) return;
        var result = CatalogQuery.Run(_catalog, _search.Text ?? "", _category,
            _rarity.SelectedIndex > 0 ? (Rarity)(_rarity.SelectedIndex - 1) : null,
            _hidden, _planned, _sort.SelectedIndex == 1 ? "name" : _sort.SelectedIndex == 2 ? "rarity" : "new", _collectionId);
        _count.Text = CollectionName() + " / " + $"{(_hidden ? "Скрытые" : _planned ? "План печати" : _category ?? "Все модели")}  /  Найдено: {result.Count}";
        _cards.Children.Clear();
        _pageIndex=Math.Clamp(_pageIndex,0,Math.Max(0,(result.Count-1)/PageSize));
        _previousPage.IsEnabled=_pageIndex>0;_nextPage.IsEnabled=(_pageIndex+1)*PageSize<result.Count;
        _pageInfo.Text=result.Count==0?"0 моделей":$"{_pageIndex*PageSize+1}–{Math.Min((_pageIndex+1)*PageSize,result.Count)} из {result.Count}";
        foreach (var model in result.Skip(_pageIndex*PageSize).Take(PageSize)) _cards.Children.Add(BuildCard(model));
        if (result.Count == 0)
        {
            var empty = new StackPanel { Spacing = 12, Margin = new Thickness(8, 40) };
            empty.Children.Add(Text("Здесь пока нет моделей", 24));
            empty.Children.Add(Text("Измените фильтры, подключите папку с STL/3MF\nили создайте карточку вручную.", 14, "#929BAD"));
            _cards.Children.Add(empty);
        }
    }
    private Control BuildCard(ModelEntry model)
    {
        var card = new ModelCard(new CardPresentation
        {
            Name = model.Name,
            Symbol = model.Category switch { "Существа" => "✧", "Террейн" => "▥", "Бюсты" => "♙", "Полезное" => "⬡", _ => "◇" },
            RarityBrush = Brush(RarityColor(model.Rarity)),
            RarityLabel = Labels.RarityName(model.Rarity).ToUpperInvariant(),
            StorageLabel = model.Demo ? "ДЕМО · БЕЗ ФАЙЛОВ" : model.FolderUnavailable ? "ПАПКА НЕДОСТУПНА"
                : $"{(model.StorageKind == StorageKind.Managed ? "КОПИИ" : "ССЫЛКИ")} · {model.Files.Count} ФАЙЛОВ",
            CategoryAndState = model.Category + " · " + Labels.StateName(model.PrintState),
            Tags = string.Join("  ", model.Tags.Take(3).Select(t => "#" + t)), IsSelected = _selected?.Id == model.Id
        });
        WireSounds(card.Button);
        card.Button.Click += (_, _) => { if (CanLeave()) { _selected = model; RefreshCards(); ShowDetails(); } };
        ToolTip.SetTip(card.Button, model.Name + "\n" + (model.Demo ? "Демонстрационная карточка" : model.FolderPath));
        return card;
    }

    private async Task OpenDesignSettingsAsync()
    {
        if (!CanLeave()) return;
        var dialog = new DesignSettingsWindow(CurrentDesign.Id);
        dialog.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Collector.Desktop/"))
            { Source = new Uri($"avares://Collector.Desktop/Themes/{CurrentDesign.ResourceFile}.axaml") });
        WireSounds(dialog.FindControl<ComboBox>("DesignPicker")!);
        WireSounds(dialog.FindControl<Button>("CancelButton")!); WireSounds(dialog.FindControl<Button>("ApplyButton")!);
        var selectedId = await dialog.ShowDialog<string?>(this);
        if (string.IsNullOrWhiteSpace(selectedId) || selectedId == CurrentDesign.Id) return;

        var previous = _designPreferences.DesignId;
        _designPreferences.DesignId = selectedId;
        var saved = true;
        try { _designPreferences.Save(_designPreferencesPath); }
        catch (Exception e)
        {
            _designPreferences.DesignId = previous;
            saved = false;
            _status.Text = "Не удалось сохранить выбранное оформление: " + e.Message;
        }

        ApplyDesignResources();
        Refresh();
        if (saved) _status.Text = $"Применено оформление: {CurrentDesign.Name}.";
    }

    private void MarkDirty()
    {
        if (_buildingDetail) return;
        if (_selected == null) return;
        // TextChanged may arrive after constructing the fields. Compare values rather than
        // treating every event (including initialization and reverting a field) as an edit.
        var wasDirty = _dirty;
        _dirty = (_name?.Text ?? "").Trim() != _selected.Name
            || (_categoryEdit?.Text ?? "").Trim() != _selected.Category
            || (_author?.Text ?? "").Trim() != _selected.Author
            || (_notes?.Text ?? "") != _selected.Notes
            || _rarityEdit?.SelectedIndex != (int)_selected.Rarity
            || _stateEdit?.SelectedIndex != (int)_selected.PrintState
            || !ParseTags(_tags?.Text).SequenceEqual(ParseTags(string.Join(",", _selected.Tags)), StringComparer.OrdinalIgnoreCase);
        _saveButton.IsEnabled = _dirty;
        _editNotice.IsVisible = _dirty;
        if (_dirty) _status.Text = "Есть несохранённые изменения. Сохраните или отмените их вверху карточки.";
        else if (wasDirty) _status.Text = "Все изменения отменены. Можно перейти к другой модели.";
    }
    private void ShowDetails()
    {
        _buildingDetail = true; _dirty = false; _saveButton.IsEnabled = false; _editNotice.IsVisible = false;
        _detail.Children.Clear(); _preview?.Dispose(); _preview = null;
        if (_selected == null) { _detail.Children.Add(new EmptyDetailsView()); _buildingDetail = false; return; }
        var model = _selected;
        var view = new ModelDetailsView(); _detail.Children.Add(view);
        _name = view.Element<TextBox>("NameInput"); _name.Text = model.Name;
        _categoryEdit = view.Element<TextBox>("CategoryInput"); _categoryEdit.Text = model.Category;
        _author = view.Element<TextBox>("AuthorInput"); _author.Text = model.Author;
        _tags = view.Element<TextBox>("TagsInput"); _tags.Text = string.Join(", ", model.Tags);
        _notes = view.Element<TextBox>("NotesInput"); _notes.Text = model.Notes;
        foreach (var field in new[] { _name, _categoryEdit, _author, _tags, _notes })
        { WireSounds(field); field.TextChanged += (_, _) => MarkDirty(); }
        _rarityEdit = view.Element<ComboBox>("RarityInput"); _rarityEdit.SelectedIndex = (int)model.Rarity;
        _stateEdit = view.Element<ComboBox>("StateInput"); _stateEdit.SelectedIndex = (int)model.PrintState;
        WireSounds(_rarityEdit); WireSounds(_stateEdit);
        _rarityEdit.SelectionChanged += (_, _) => MarkDirty(); _stateEdit.SelectionChanged += (_, _) => MarkDirty();
        view.Element<TextBlock>("ModelKind").Text = model.Demo ? "ДЕМОНСТРАЦИОННАЯ МОДЕЛЬ" : "КАРТОЧКА МОДЕЛИ";
        void Click(string name, Action action)
        { var button = view.Element<Button>(name); WireSounds(button); button.Click += (_, _) => action(); }
        view.Element<Button>("Open3DButton").Content = model.Demo ? "3D-демонстрация" : "3D-просмотр";
        Click("Open3DButton", () => Open3D(model)); Click("ResourcesButton", () => _ = ShowResourcesAsync(model));
        Click("AttachButton", () => _ = AttachFilesAsync(model)); Click("VerifyButton", () => _ = VerifyFilesAsync(model));
        Click("FolderButton", () => OpenFolder(model.FolderPath)); Click("HideButton", ToggleHidden);
        view.Element<Button>("HideButton").Content = model.Hidden ? "Восстановить карточку" : "Скрыть карточку";
        view.Element<TextBlock>("StorageInfo").Text = _catalog.Collections.First(c => c.Id == model.CollectionId).Name + " · " +
            (model.StorageKind == StorageKind.Managed ? "копии в коллекции" : "ссылки на исходные файлы");
        var missing = model.Files.Count(f => f.Missing);
        var warning = view.Element<TextBlock>("StorageWarning");
        warning.Text = model.FolderUnavailable ? "Папка недоступна. Проверьте подключение диска и нажмите «Проверить файлы»."
            : missing > 0 ? $"Не найдены при последней проверке: {missing}" : "";
        warning.IsVisible = !string.IsNullOrEmpty(warning.Text);
        var checkedAt = view.Element<TextBlock>("LastChecked"); checkedAt.IsVisible = model.StorageCheckedUtc != null;
        checkedAt.Text = model.StorageCheckedUtc is { } time ? $"Последняя проверка: {time.ToLocalTime():g}" : "";
        view.Element<TextBlock>("FileSummary").Text = model.Files.Count == 0 ? "Файлы не подключены" : $"{model.Files.Count} файлов · {FormatSize(model.Files.Sum(f => f.Size))}";
        view.Element<ItemsControl>("FilesList").ItemsSource = model.Files.Take(12).ToList();
        view.Element<TextBlock>("MoreFiles").IsVisible = model.Files.Count > 12;
        view.Element<TextBlock>("MoreFiles").Text = $"Ещё {model.Files.Count - 12} файлов в папке";
        view.Element<TextBlock>("FolderPath").Text = model.FolderPath;
        view.Element<Button>("FolderButton").IsVisible = !string.IsNullOrEmpty(model.FolderPath);
        if (model.PreviewPath != null)
        { var host = view.Element<Border>("PreviewHost"); host.IsVisible = true; _ = LoadModelPreviewAsync(model, host); }
        _buildingDetail = false;
    }
    private static List<string> ParseTags(string? text) => (text ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(t => t.TrimStart('#')).Where(t => t.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string FormatSize(long bytes) => bytes >= 1_073_741_824 ? $"{bytes / 1073741824d:F1} ГБ" : bytes >= 1_048_576 ? $"{bytes / 1048576d:F1} МБ" : bytes >= 1024 ? $"{bytes / 1024d:F1} КБ" : $"{bytes} Б";
    private bool Persist()
    {
        var categories = _catalog.Collections.ToDictionary(c => c.Id, c => c.Categories.ToList());
        try { _store.Save(_catalog); return true; }
        catch (Exception e)
        {
            foreach (var collection in _catalog.Collections) collection.Categories = categories[collection.Id];
            _status.Text = "Не удалось сохранить: " + e.Message; return false;
        }
    }
    private void SaveEdit()
    {
        if (_selected == null || _loadError != null) return;
        if (_busy) { _status.Text = "Дождитесь завершения операции хранения."; return; }
        if (string.IsNullOrWhiteSpace(_name?.Text) || string.IsNullOrWhiteSpace(_categoryEdit?.Text))
        { _status.Text = "Укажите название и категорию."; return; }
        var model = _selected;
        var old = (model.Name, model.Category, model.Author, model.Tags, model.Notes, model.Rarity, model.PrintState);
        model.Name = _name!.Text!.Trim(); model.Category = _categoryEdit!.Text!.Trim(); model.Author = _author?.Text?.Trim() ?? "";
        model.Tags = ParseTags(_tags?.Text);
        model.Notes = _notes?.Text ?? ""; model.Rarity = (Rarity)_rarityEdit!.SelectedIndex; model.PrintState = (PrintState)_stateEdit!.SelectedIndex;
        if (!Persist())
        {
            (model.Name, model.Category, model.Author, model.Tags, model.Notes, model.Rarity, model.PrintState) = old;
            return;
        }
        _dirty = false; Refresh(); _status.Text = "Карточка сохранена. Исходные файлы не изменялись.";
    }
    private void NewModel()
    {
        if (!CanLeave()) return;
        var model = new ModelEntry { Category = _category ?? "Без категории", CollectionId = _collectionId ?? _catalog.Collections[0].Id };
        _catalog.Models.Add(model);
        if (!Persist()) { _catalog.Models.Remove(model); return; }
        _hidden = false; _planned = false; _category = null; _search.Text = ""; _rarity.SelectedIndex = 0;
        _selected = model; Refresh(); _status.Text = "Новая карточка создана. Заполните название, категорию и теги.";
    }
    private void ToggleHidden()
    {
        if (!CanLeave() || _selected == null) return;
        _selected.Hidden = !_selected.Hidden;
        if (!Persist()) { _selected.Hidden = !_selected.Hidden; return; }
        _selected = null; Refresh(); _status.Text = "Видимость изменена. Файлы на диске сохранены.";
    }
    private void Open3D(ModelEntry model)
    {
        var paths = new List<string>();
        try
        {
            foreach (var file in model.Files.Where(f => MeshReader.IsSupported(f.RelativePath)))
                paths.Add(LibraryPaths.Resolve(model, file));
            _ = new ModelViewerWindow(paths, model.Demo).ShowDialog(this);
        }
        catch (Exception e) { _status.Text = "Не удалось открыть 3D-просмотр: " + e.Message; }
    }

    private void OpenFolder(string path) => _ = OpenFolderAsync(path);
    private async Task OpenFolderAsync(string path)
    {
        try
        {
            var message = await Task.Run(() =>
            {
                if (!Directory.Exists(path)) return "Папка недоступна. Проверьте подключение диска.";
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return "Папка открыта.";
            });
            if (!_closed) _status.Text = message;
        }
        catch (Exception e) { if (!_closed) _status.Text = "Не удалось открыть папку: " + e.Message; }
    }
    private async Task ShowWarnings(List<string> warnings)
    {
        var dialog = new Window { Title = "Замечания при подключении", Width = 620, Height = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 16 };
        panel.Children.Add(Text("Часть папок не удалось прочитать", 20));
        panel.Children.Add(Text(string.Join("\n\n", warnings.Take(30)), 12));
        panel.Children.Add(Button("Понятно", dialog.Close));
        dialog.Content = new ScrollViewer { Content = panel }; await dialog.ShowDialog(this);
    }
}
