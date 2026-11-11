using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Collector.Desktop;

public sealed partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (!Design.IsDesignMode && ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? [];
            var flag = Array.IndexOf(args, "--data-dir");
            var directory = flag >= 0 && flag + 1 < args.Length ? args[flag + 1]
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Collector3D");
            desktop.MainWindow = new MainWindow(Path.GetFullPath(directory));
        }
        base.OnFrameworkInitializationCompleted();
    }
}
