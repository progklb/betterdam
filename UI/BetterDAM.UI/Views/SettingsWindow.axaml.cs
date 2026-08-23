using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
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

    /// <summary>Re-sorts a keyword's level once its name has finished being edited.</summary>
    private void OnKeywordNameCommitted(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: KeywordNodeViewModel node } &&
            DataContext is SettingsViewModel viewModel)
        {
            viewModel.Keywords.SortSiblingsOf(node);
        }
    }

    /// <summary>
    /// Moves the selected keyword under the destination just picked, then closes the flyout and
    /// clears the list's selection so the same destination can be chosen again next time.
    /// </summary>
    private void OnMoveTargetChosen(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox list ||
            list.SelectedItem is not KeywordMoveTarget target ||
            list.DataContext is not KeywordNodeViewModel node ||
            DataContext is not SettingsViewModel viewModel)
        {
            return;
        }

        viewModel.Keywords.Move(node, target);

        list.SelectedItem = null;
        FlyoutBase.GetAttachedFlyout(list)?.Hide();

        if (list.FindAncestorOfType<Popup>() is { } popup)
        {
            popup.IsOpen = false;
        }
    }

    /// <summary>
    /// Flushes the keyword library on the way out. Edits are saved on a short delay, so closing the
    /// window immediately after typing would otherwise lose the last one.
    /// </summary>
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        if (DataContext is SettingsViewModel viewModel)
        {
            await viewModel.Keywords.SaveAsync();
        }
    }
}
