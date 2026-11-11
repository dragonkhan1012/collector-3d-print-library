using Collector.Core;

var root = Path.Combine(Path.GetTempPath(), "collector-check-" + Guid.NewGuid());
Directory.CreateDirectory(root);
var checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception("FAIL: " + label);
    checks++; Console.WriteLine("PASS: " + label);
}
try
{
    var store = new CatalogStore(Path.Combine(root, "data"));
    var catalog = store.Load();
    Check(catalog.Models.Count == 8 && catalog.Models.All(m => m.Demo && m.Files.Count == 0), "демо явно без исходных файлов");
    var model = catalog.Models[0]; model.Tags.Add("уникальныйтег"); model.Rarity = Rarity.Legendary;
    store.Save(catalog);
    Check(store.Load().Models[0].Tags.Contains("уникальныйтег"), "сохранение и повторное открытие");
    Check(CatalogQuery.Run(catalog, "#уникальныйтег", rarity: Rarity.Legendary).Single().Id == model.Id, "поиск и фильтр редкости");
    model.Hidden = true; store.Save(catalog);
    Check(CatalogQuery.Run(catalog, "уникальныйтег").Count == 0 && CatalogQuery.Run(catalog, "уникальныйтег", hidden: true).Count == 1, "скрытие сохраняется и исключается из обычного поиска");
    Check(File.Exists(store.FilePath + ".bak"), "предыдущая версия сохранена в резервной копии");
    var collection = Path.Combine(root, "models"); var folder = Path.Combine(collection, "Дракон"); Directory.CreateDirectory(folder);
    var file = Path.Combine(folder, "dragon.stl"); File.WriteAllText(file, "solid demo\nendsolid demo");
    var scan = FolderScanner.Scan(collection);
    Check(scan.Models.Count == 1 && scan.Models[0].Files.Single().RelativePath == "dragon.stl", "подпапка с STL становится карточкой");
    Check(File.ReadAllText(file) == "solid demo\nendsolid demo", "чтение не меняет исходный файл");
    Check(FolderScanner.Scan(folder).Models.Count == 1, "одиночная папка модели подключается");
    Directory.CreateSymbolicLink(Path.Combine(folder, "loop"), collection);
    Check(FolderScanner.Scan(collection).Models.Count == 1, "ссылка-цикл не обходится");
    File.WriteAllText(store.FilePath, "{ broken json");
    var rejected = false; try { store.Load(); } catch { rejected = true; }
    Check(rejected && File.ReadAllText(store.FilePath) == "{ broken json", "повреждённый каталог не заменяется");
    Console.WriteLine($"Успешно: {checks} проверок.");
}
finally { Directory.Delete(root, true); }
