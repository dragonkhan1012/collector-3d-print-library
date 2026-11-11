using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Collector.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => AppBuilder.Configure<App>().UsePlatformDetect()
        .WithInterFont().LogToTrace().StartWithClassicDesktopLifetime(args);
}

