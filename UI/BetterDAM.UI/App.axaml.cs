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

    /// <summary>
    /// Settings sits in the macOS application menu, which is application-scoped and so cannot bind
    /// to the window's ViewModel. Forward to the window, which owns the dialog.
    /// </summary>
    private void OnOpenSettings(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow window })
        {
            _ = window.OpenSettingsAsync();
        }
    }

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
                PrepareWorkspaceViewModelFactory = services.GetRequiredService<PrepareWorkspaceViewModel>,
                SyncViewModelFactory = services.GetRequiredService<SyncViewModel>
            };

            // A folder on the command line wins over the remembered one; otherwise reopen the last
            // workspace so the application starts where it was left.
            if ((StartupFolder ?? settings.Current.LastWorkspacePath) is { } folder)
            {
                _ = viewModel.OpenPathAsync(folder);
            }

            desktop.ShutdownRequested += (_, _) => services.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
