using Avalonia.Controls;
using Avalonia.Interactivity;
using BetterDAM.UI.ViewModels;

namespace BetterDAM.UI.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();

        Opened += async (_, _) =>
        {
            if (DataContext is SettingsViewModel viewModel)
            {
                viewModel.StorageProvider = StorageProvider;
                await viewModel.RefreshAsync();
            }
        };
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
