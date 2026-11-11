using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Collector.Core;

namespace Collector.Desktop;

public sealed partial class ModelDetailsView : UserControl
{
    public T Element<T>(string name) where T : Control => this.FindControl<T>(name)!;
    public ModelDetailsView()
    {
        AvaloniaXamlLoader.Load(this);
        Element<ComboBox>("RarityInput").ItemsSource = Labels.Rarities;
        Element<ComboBox>("RarityInput").SelectedIndex = 2;
        Element<ComboBox>("StateInput").ItemsSource = Labels.States;
        Element<ComboBox>("StateInput").SelectedIndex = 0;
    }
}
