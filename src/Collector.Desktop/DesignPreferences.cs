using System.Text.Json;

namespace Collector.Desktop;

internal sealed class DesignPreferences
{
    public string DesignId { get; set; } = "workshop";

    public static DesignPreferences Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new();
            var loaded = JsonSerializer.Deserialize<DesignPreferences>(File.ReadAllText(path)) ?? new();
            return DesignCatalog.All.Any(d => d.Id == loaded.DesignId) ? loaded : new();
        }
        catch { return new(); }
    }

    public void Save(string path)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this));
        File.Move(temporary, path, true);
    }
}

internal sealed record DesignOption(string Id, string Name, string Description, string ResourceFile);

internal static class DesignCatalog
{
    public static readonly DesignOption[] All =
    [
        new("atlas", "01 · Атлас", "Тёплый редакционный каталог для спокойного просмотра.", "Atlas"),
        new("showroom", "02 · Витрина", "Просторный вид, где крупные превью занимают главное место.", "Showroom"),
        new("workshop", "03 · Мастерская", "Плотный рабочий стол для большой библиотеки: каталог и сведения рядом.", "Workshop"),
        new("shelves", "04 · Полки", "Мягкая библиотека с более крупными карточками и акцентом на коллекциях.", "Shelves"),
        new("blueprint", "05 · Чертёж", "Строгий технический вид с холодными синими акцентами.", "Blueprint")
    ];
    public static DesignOption Get(string? id) => All.FirstOrDefault(d => d.Id == id) ?? All[2];
}
