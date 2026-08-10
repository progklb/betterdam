using Avalonia;
using BetterDAM.Core.Services;
using Serilog;

namespace BetterDAM.UI;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var paths = new AppPaths();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(paths.LogRoot, "betterdam-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14)
            .CreateLogger();

        try
        {
            App.Paths = paths;
            App.StartupFolder = args.FirstOrDefault(a => !a.StartsWith('-'));
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "BetterDAM terminated unexpectedly");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    // Referenced by the Avalonia XAML previewer/designer as well as Main.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
