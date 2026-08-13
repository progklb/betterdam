using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using BetterDAM.Core.Interfaces;
using BetterDAM.UI.Services;
using BetterDAM.UI.ViewModels;
using BetterDAM.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace BetterDAM.UI;

public partial class App : Application
{
    /// <summary>
    /// Set by <see cref="Program"/> before the Avalonia app starts, so logging can be configured
    /// against the same cache location the rest of the application uses.
    /// </summary>
    internal static IAppPaths? Paths { get; set; }

    internal static ISettingsService? Settings { get; set; }

    /// <summary>Optional folder passed on the command line, opened once the window is up.</summary>
    internal static string? StartupFolder { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = Settings ?? new Core.Services.JsonSettingsService(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Core.Services.JsonSettingsService>.Instance);

            var services = new ServiceCollection()
                .AddBetterDam(Paths ?? new Core.Services.AppPaths(settings), settings)
                .BuildServiceProvider();

            // Apply any rolling-cache limit from a previous session before browsing adds more.
            var maintenance = services.GetRequiredService<ICacheMaintenance>();
            _ = Task.Run(() => maintenance.TrimAsync());

            var viewModel = services.GetRequiredService<MainWindowViewModel>();
            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel,
                SettingsViewModelFactory = services.GetRequiredService<SettingsViewModel>,
                SyncViewModelFactory = services.GetRequiredService<SyncViewModel>
            };

            if (StartupFolder is { } folder)
            {
                _ = viewModel.OpenPathAsync(folder);
            }

            desktop.ShutdownRequested += (_, _) => services.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
