using Avalonia.Controls;
using Avalonia.Interactivity;
using BetterDAM.UI.ViewModels;

namespace BetterDAM.UI.Views;

public partial class PrepareWorkspaceWindow : Window
{
    public PrepareWorkspaceWindow()
    {
        InitializeComponent();

        // Counting happens once the dialog is up, so a large tree shows a window that is sizing
        // itself up rather than nothing at all.
        Opened += async (_, _) =>
        {
            if (DataContext is PrepareWorkspaceViewModel viewModel)
            {
                await viewModel.EstimateAsync();
            }
        };
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
