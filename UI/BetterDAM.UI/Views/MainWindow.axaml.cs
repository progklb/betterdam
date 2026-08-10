using Avalonia.Controls;
using BetterDAM.UI.ViewModels;

namespace BetterDAM.UI.Views;

public partial class MainWindow : Window
{
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
}
