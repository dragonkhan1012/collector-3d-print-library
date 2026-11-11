using System.Text.Json;
using System.Text.Json.Serialization;

namespace Collector.Core;

public enum Rarity { Common, Uncommon, Rare, Epic, Legendary }
public enum PrintState { Collected, Planned, Printed }
public enum StorageKind { Linked, Managed }

public sealed class CollectionEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Моя коллекция";
    public string RootPath { get; set; } = "";
    public List<string> Categories { get; set; } = [];
}

public sealed class ModelEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? CollectionId { get; set; }
    public StorageKind StorageKind { get; set; }
    public string SourceIdentity { get; set; } = "";
    public bool TrackFolder { get; set; } = true;
    public bool FolderUnavailable { get; set; }
    public DateTimeOffset? StorageCheckedUtc { get; set; }
    public string Name { get; set; } = "Новая модель";
    public string Category { get; set; } = "Без категории";
    public Rarity Rarity { get; set; }
    public PrintState PrintState { get; set; }
    public string Author { get; set; } = "";
    public string Notes { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public string FolderPath { get; set; } = "";
    public string? PreviewPath { get; set; }
    public List<ModelFile> Files { get; set; } = [];
    public bool Hidden { get; set; }
    public bool Demo { get; set; }
    public DateTimeOffset AddedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ModelFile
{
    public string RelativePath { get; set; } = "";
    public long Size { get; set; }
    public string? ExternalPath { get; set; }
    public string? Sha256 { get; set; }
    public bool Missing { get; set; }
}

public sealed class Catalog
{
    public int SchemaVersion { get; set; } = 2;
    public long Revision { get; set; }
    public List<CollectionEntry> Collections { get; set; } = [];
    public List<ModelEntry> Models { get; set; } = [];
}

public static class Labels
{
    public static readonly string[] Rarities = ["Обычная", "Необычная", "Редкая", "Эпическая", "Легендарная"];
    public static readonly string[] States = ["В коллекции", "Хочу напечатать", "Напечатана"];
    public static string RarityName(Rarity value) => Rarities[(int)value];
    public static string StateName(PrintState value) => States[(int)value];
}

public static class CatalogQuery
{
    public static List<ModelEntry> Run(Catalog catalog, string query = "", string? category = null,
        Rarity? rarity = null, bool hidden = false, bool planned = false, string sort = "new", Guid? collectionId = null)
    {
        IEnumerable<ModelEntry> result = catalog.Models.Where(m => m.Hidden == hidden);
        if (collectionId != null) result = result.Where(m => m.CollectionId == collectionId);
        if (category != null) result = result.Where(m => m.Category == category);
        if (rarity != null) result = result.Where(m => m.Rarity == rarity);
        if (planned) result = result.Where(m => m.PrintState == PrintState.Planned);
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        result = result.Where(m => words.All(word =>
            string.Join(' ', new[] { m.Name, m.Category, m.Author, m.Notes }.Concat(m.Tags))
                .Contains(word.TrimStart('#'), StringComparison.OrdinalIgnoreCase)));
        return (sort switch
        {
            "name" => result.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(m => m.Id),
            "rarity" => result.OrderByDescending(m => m.Rarity).ThenBy(m => m.Name).ThenBy(m => m.Id),
            _ => result.OrderByDescending(m => m.AddedUtc).ThenBy(m => m.Id)
        }).ToList();
    }
}

public sealed class CatalogStore(string directory)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    public string FilePath => Path.Combine(directory, "catalog.json");

    public Catalog Load()
    {
        if (!File.Exists(FilePath)) return DemoCatalog.Create();
        var catalog = JsonSerializer.Deserialize<Catalog>(File.ReadAllText(FilePath), Options)
            ?? throw new InvalidDataException("Каталог пуст. Существующий файл сохранён без изменений.");
        if (catalog.SchemaVersion is not (1 or 2)) throw new InvalidDataException("Неизвестная версия каталога. Файл не изменён.");
        if (catalog.Models == null || catalog.Models.Any(m => m == null || string.IsNullOrWhiteSpace(m.Name)
            || string.IsNullOrWhiteSpace(m.Category) || m.Tags == null || m.Files == null
            || !Enum.IsDefined(m.Rarity) || !Enum.IsDefined(m.PrintState))
            || catalog.Models.Select(m => m.Id).Distinct().Count() != catalog.Models.Count)
            throw new InvalidDataException("Нарушена структура каталога. Восстановите catalog.json из резервной копии.");
        Upgrade(catalog);
        return catalog;
    }

    public static void Upgrade(Catalog catalog)
    {
        if (catalog.Collections == null) throw new InvalidDataException("Нарушена структура коллекций.");
        if (catalog.Collections.Count == 0)
            catalog.Collections.Add(new CollectionEntry { Name = catalog.Models.Count > 0 && catalog.Models.All(m => m.Demo) ? "Демонстрация" : "Моя коллекция" });
        if (catalog.Collections.Any(c => c == null || c.Id == Guid.Empty || string.IsNullOrWhiteSpace(c.Name) || c.Categories == null || c.RootPath == null)
            || catalog.Collections.Select(c => c.Id).Distinct().Count() != catalog.Collections.Count)
            throw new InvalidDataException("Некорректные коллекции.");
        foreach (var model in catalog.Models)
        {
            if (model.CollectionId == null) model.CollectionId = catalog.Collections[0].Id;
            var collection = catalog.Collections.SingleOrDefault(c => c.Id == model.CollectionId)
                ?? throw new InvalidDataException("Карточка ссылается на неизвестную коллекцию.");
            if (!collection.Categories.Contains(model.Category)) collection.Categories.Add(model.Category);
            if (model.FolderPath == null || model.SourceIdentity == null || !Enum.IsDefined(model.StorageKind) || model.Files.Any(f => f == null || f.Size < 0 || string.IsNullOrWhiteSpace(f.RelativePath)))
                throw new InvalidDataException("Нарушена структура файлов модели.");
        }
        catalog.SchemaVersion = 2;
    }

    public void Save(Catalog catalog)
    {
        Directory.CreateDirectory(directory);
        using var gate=new FileStream(FilePath+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        long actual=0;
        if(File.Exists(FilePath))
        {
            using var current=JsonDocument.Parse(File.ReadAllText(FilePath));
            if(current.RootElement.TryGetProperty("Revision",out var revision)) actual=revision.GetInt64();
            if(actual!=catalog.Revision) throw new IOException("Каталог изменён в другом окне. Перезапустите приложение, чтобы не потерять изменения.");
        }
        var previous=catalog.Revision;
        try
        {
            Upgrade(catalog);catalog.Revision=actual+1;
            var temporary=FilePath+".tmp";
            using(var stream=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None))
            { JsonSerializer.Serialize(stream,catalog,Options);stream.Flush(true); }
            if(File.Exists(FilePath)) File.Copy(FilePath,FilePath+".bak",true);
            File.Move(temporary,FilePath,true);
        }
        catch { catalog.Revision=previous;throw; }
    }

}

public static class DemoCatalog
{
    public static Catalog Create()
    {
        (string Name, string Category, Rarity Rarity, string Author, string Tags, PrintState State)[] examples =
        [
            ("Страж древних руин", "Миниатюры", Rarity.Legendary, "Демо-студия", "фэнтези, воин, 32мм", PrintState.Collected),
            ("Лесной дракон", "Существа", Rarity.Epic, "Демо-студия", "дракон, фэнтези, смола", PrintState.Planned),
            ("Орбитальный рейнджер", "Миниатюры", Rarity.Rare, "Демо-студия", "sci-fi, броня, 32мм", PrintState.Planned),
            ("Арка забытого храма", "Террейн", Rarity.Uncommon, "Демо-мастерская", "руины, архитектура, FDM", PrintState.Printed),
            ("Лунная жрица", "Бюсты", Rarity.Epic, "Демо-мастерская", "бюст, фэнтези, смола", PrintState.Collected),
            ("Механический жук", "Существа", Rarity.Rare, "Демо-студия", "механика, sci-fi, насекомое", PrintState.Collected),
            ("Башня алхимика", "Террейн", Rarity.Rare, "Демо-мастерская", "башня, фэнтези, FDM", PrintState.Planned),
            ("Лоток для инструментов", "Полезное", Rarity.Common, "Демо-мастерская", "органайзер, мастерская, FDM", PrintState.Printed)
        ];
        var catalog = new Catalog { Models = examples.Select((x, i) => new ModelEntry
        {
            Name = x.Name, Category = x.Category, Rarity = x.Rarity, Author = x.Author,
            Tags = x.Tags.Split(", ").ToList(), PrintState = x.State, Demo = true,
            Notes = "Демонстрационная карточка для проверки интерфейса. Файлы модели не прилагаются. Редкость назначается владельцем коллекции.",
            AddedUtc = DateTimeOffset.UtcNow.AddDays(-i)
        }).ToList() };
        CatalogStore.Upgrade(catalog);
        return catalog;
    }
}
