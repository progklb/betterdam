using Avalonia.Controls;
using Avalonia.Interactivity;
using BetterDAM.UI.ViewModels;

namespace BetterDAM.UI.Views;

public partial class MainWindow : Window
{
    /// <summary>Creates the settings ViewModel on demand, so its state is fresh each time.</summary>
    public Func<SettingsViewModel>? SettingsViewModelFactory { get; set; }

    public MainWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.StorageProvider = StorageProvider;
            }
        };
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
