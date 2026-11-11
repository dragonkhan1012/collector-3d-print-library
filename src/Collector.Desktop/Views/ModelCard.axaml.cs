using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace Collector.Desktop;

public sealed class CardPresentation
{
    public string Name { get; init; } = "Лесной дракон";
    public string Symbol { get; init; } = "✧";
    public string StorageLabel { get; init; } = "ДЕМО · БЕЗ ФАЙЛОВ";
    public string RarityLabel { get; init; } = "ЭПИЧЕСКАЯ";
    public IBrush RarityBrush { get; init; } = Brush.Parse("#BE9AF3");
    public string CategoryAndState { get; init; } = "Существа · Хочу напечатать";
    public string Tags { get; init; } = "#фэнтези  #дракон";
    public bool IsSelected { get; init; }
}

public sealed partial class ModelCard : UserControl
{
    public Button Button => this.FindControl<Button>("CardButton")!;
    public ModelCard() { AvaloniaXamlLoader.Load(this); DataContext = new CardPresentation(); }
    public ModelCard(CardPresentation model) : this() { DataContext = model; }
}
