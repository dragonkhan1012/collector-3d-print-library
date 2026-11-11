using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Collector.Desktop;

public sealed partial class DesignSettingsWindow : Window
{
    public DesignSettingsWindow() : this("workshop") { }
    public DesignSettingsWindow(string selectedId)
    {
        AvaloniaXamlLoader.Load(this);
        var picker = this.FindControl<ComboBox>("DesignPicker")!;
        var description = this.FindControl<TextBlock>("Description")!;
        picker.ItemsSource = DesignCatalog.All.Select(d => d.Name).ToArray();
        picker.SelectedIndex = Array.FindIndex(DesignCatalog.All, d => d.Id == selectedId);
        void Describe() => description.Text = DesignCatalog.All[Math.Max(0, picker.SelectedIndex)].Description;
        picker.SelectionChanged += (_, _) => Describe(); Describe();
        this.FindControl<Button>("CancelButton")!.Click += (_, _) => Close();
        this.FindControl<Button>("ApplyButton")!.Click += (_, _) => { if (picker.SelectedIndex >= 0) Close(DesignCatalog.All[picker.SelectedIndex].Id); };
    }
}
