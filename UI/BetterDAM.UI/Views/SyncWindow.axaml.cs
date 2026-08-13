using Avalonia.Controls;
using Avalonia.Interactivity;
using BetterDAM.UI.ViewModels;

namespace BetterDAM.UI.Views;

public partial class SyncWindow : Window
{
    public SyncWindow()
    {
        InitializeComponent();

        Opened += async (_, _) =>
        {
            if (DataContext is SyncViewModel viewModel)
            {
                await viewModel.PrepareAsync();
            }
        };
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
