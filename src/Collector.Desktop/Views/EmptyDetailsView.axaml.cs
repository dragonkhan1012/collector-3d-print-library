using Avalonia.Controls;
using Avalonia.Markup.Xaml;
namespace Collector.Desktop;
public sealed partial class EmptyDetailsView : UserControl
{
    public EmptyDetailsView() => AvaloniaXamlLoader.Load(this);
}
