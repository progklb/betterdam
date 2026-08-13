using Avalonia.Controls;
using Avalonia.Interactivity;
using BetterDAM.Core.Models;
using BetterDAM.UI.ViewModels;

namespace BetterDAM.UI.Views;

public partial class MainWindow : Window
{
    /// <summary>Creates the settings ViewModel on demand, so its state is fresh each time.</summary>
    public Func<SettingsViewModel>? SettingsViewModelFactory { get; set; }

    /// <summary>Likewise for sync: each dialog re-plans against the current pending changes.</summary>
    public Func<SyncViewModel>? SyncViewModelFactory { get; set; }

    public MainWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainWindowViewModel viewModel)
            {
                return;
            }

            viewModel.StorageProvider = StorageProvider;

            // Frames are pushed rather than bound: a binding would mean allocating a bitmap per
            // frame, where the surface reuses one and blits into it.
            viewModel.Player.FrameReady -= OnFrameReady;
            viewModel.Player.FrameReady += OnFrameReady;
            viewModel.Player.SurfaceCleared -= OnSurfaceCleared;
            viewModel.Player.SurfaceCleared += OnSurfaceCleared;
        };
    }

    private void OnGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox list && DataContext is MainWindowViewModel viewModel)
        {
            viewModel.UpdateSelection(list.SelectedItems?.OfType<MediaItemViewModel>().ToList() ?? []);
        }
    }

    private void OnFrameReady(VideoFrame frame) => VideoSurface.Present(frame);

    private void OnSurfaceCleared() => VideoSurface.Clear();

    private async void OnOpenSync(object? sender, RoutedEventArgs e)
    {
        if (SyncViewModelFactory is not { } factory)
        {
            return;
        }

        var window = new SyncWindow { DataContext = factory() };
        await window.ShowDialog(this);

        // Sync clears whatever it committed, so the grid's badges need refreshing.
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.RefreshAfterSync();
        }
    }

    private async void OnOpenSettings(object? sender, RoutedEventArgs e)
    {
        if (SettingsViewModelFactory is not { } factory)
        {
            return;
        }

        var window = new SettingsWindow { DataContext = factory() };
        await window.ShowDialog(this);
    }
}
