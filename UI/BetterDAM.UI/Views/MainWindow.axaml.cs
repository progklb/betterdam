using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using BetterDAM.Core.Models;
using BetterDAM.UI.Services;
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

            viewModel.RecentWorkspaces.CollectionChanged -= OnRecentChanged;
            viewModel.RecentWorkspaces.CollectionChanged += OnRecentChanged;
            RebuildRecentMenu();

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

    private void OnRecentChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildRecentMenu();

    /// <summary>
    /// Walks the declared menu for the Open Recent placeholder. Necessary because x:Name generates
    /// no field for a NativeMenuItem — it is not part of the visual tree.
    /// </summary>
    private NativeMenu? FindRecentMenu()
        => NativeMenu.GetMenu(this)?
            .Items.OfType<NativeMenuItem>()
            .SelectMany(top => top.Menu?.Items.OfType<NativeMenuItem>() ?? [])
            .FirstOrDefault(item => item.Header == MenuConventions.OpenRecentHeader)?
            .Menu;

    /// <summary>
    /// Rebuilds Open Recent from the ViewModel. Built in code rather than bound because
    /// <see cref="NativeMenuItem"/> has no DataContext, so an ItemsSource-style binding has nothing
    /// to resolve against.
    /// </summary>
    private void RebuildRecentMenu()
    {
        if (FindRecentMenu() is not { } menu || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        menu.Items.Clear();

        foreach (var path in viewModel.RecentWorkspaces)
        {
            var item = new NativeMenuItem { Header = WorkspaceLabel.ForMenu(path), ToolTip = path };

            var target = path;
            item.Click += (_, _) => _ = viewModel.OpenPathAsync(target);
            menu.Items.Add(item);
        }

        // An always-empty submenu looks broken, so say why it is empty.
        if (menu.Items.Count == 0)
        {
            menu.Items.Add(new NativeMenuItem { Header = "No recent workspaces", IsEnabled = false });
        }
    }

    private void OnCloseWorkspace(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            _ = viewModel.CloseWorkspaceCommand.ExecuteAsync(null);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // Ignored while typing: "f" belongs to the search box when it has focus.
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.None && FocusManager?.GetFocusedElement() is not TextBox)
        {
            OpenFullscreen();
            e.Handled = true;
        }
    }

    private void OnPreviewDoubleTapped(object? sender, TappedEventArgs e) => OpenFullscreen();

    private void OnFullscreen(object? sender, RoutedEventArgs e) => OpenFullscreen();

    private void OnTransportFullscreen(object? sender, EventArgs e) => OpenFullscreen();

    /// <summary>
    /// Opens the current selection for inspection. Video keeps playing into the fullscreen surface
    /// because the player pushes frames to whoever is listening, rather than owning one view.
    /// </summary>
    private void OpenFullscreen()
    {
        if (DataContext is not MainWindowViewModel viewModel || viewModel.SelectedItem is null)
        {
            return;
        }

        // Shown without an owner: macOS refuses to take a child window fullscreen, so an owned
        // viewer silently stays a normal window.
        var window = new MediaViewerWindow();
        window.Show();
        window.Attach(viewModel);
    }

    private void OnOpenFolder(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel && viewModel.OpenFolderCommand.CanExecute(null))
        {
            viewModel.OpenFolderCommand.Execute(null);
        }
    }

    private void OnOpenSettings(object? sender, EventArgs e) => _ = OpenSettingsAsync();

    /// <summary>
    /// Public because the macOS application menu lives on <see cref="App"/>, which has no other way
    /// to reach the window that must own the dialog.
    /// </summary>
    public async Task OpenSettingsAsync()
    {
        if (SettingsViewModelFactory is not { } factory)
        {
            return;
        }

        var window = new SettingsWindow { DataContext = factory() };
        await window.ShowDialog(this);
    }
}
