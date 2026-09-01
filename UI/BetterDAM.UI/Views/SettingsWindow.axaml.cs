using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Avalonia.LogicalTree;
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

    /// <summary>
    /// Opens on the Keywords tab rather than General. Used by the inspector's "Manage keywords",
    /// which is a request to edit the vocabulary — landing on General and leaving the user to find
    /// the tab would answer a question they did not ask.
    ///
    /// Named parts rather than a header string, so renaming the tab cannot quietly break this.
    /// </summary>
    public void ShowKeywords() => Tabs.SelectedItem = KeywordsTab;

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
    /// The move a click in the "move under" list has chosen, waiting for that click to finish.
    /// </summary>
    private (KeywordNodeViewModel Node, KeywordMoveTarget Target, FlyoutBase? Flyout)? _pendingMove;

    /// <summary>
    /// Records the destination picked in the flyout. The move itself waits for the button to come
    /// back up — see <see cref="OnMoveTargetReleased"/>.
    /// </summary>
    private void OnMoveTargetChosen(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox list ||
            list.SelectedItem is not KeywordMoveTarget target ||
            list.DataContext is not KeywordNodeViewModel node)
        {
            return;
        }

        // The flyout is declared as Button.Flyout, so FlyoutBase.GetAttachedFlyout does not find it.
        // Reached through the LOGICAL tree instead, which runs ListBox → FlyoutPresenter → Popup →
        // Button; the visual chain from inside a popup stops at its PopupRoot and never passes the
        // Popup at all, which is why the FindAncestorOfType<Popup> this used to rely on was always
        // null and the close it guarded never once ran.
        _pendingMove = (node, target, list.FindLogicalAncestorOfType<Button>()?.Flyout);

        // Re-enters this handler with a null selection, which falls out at the check above.
        list.SelectedItem = null;
    }

    /// <summary>
    /// Carries out the move once the click that chose it has finished.
    ///
    /// A ListBox selects on pointer <i>press</i>, so doing this from SelectionChanged closed the
    /// flyout half way through a click and the release then landed on a window that had gone. The
    /// symptom was that the wheel stopped reaching this window entirely — measured with a handler
    /// on the window, which logged nothing at all afterwards — so the keyword list would not scroll
    /// until the user clicked it again to end the abandoned gesture. Moving a keyword and then
    /// scrolling to see where it landed is one gesture and should not need a click in the middle.
    /// </summary>
    private void OnMoveTargetReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pendingMove is not { } move || DataContext is not SettingsViewModel viewModel)
        {
            return;
        }

        _pendingMove = null;

        move.Flyout?.Hide();
        viewModel.Keywords.Move(move.Node, move.Target);
        KeywordTree.Focus();
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
