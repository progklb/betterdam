using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using BetterDAM.UI.ViewModels;

namespace BetterDAM.UI.Views;

public partial class MetadataInspectorView : UserControl
{
    public MetadataInspectorView()
    {
        InitializeComponent();

        // The cap depends on the viewport, so it has to be re-applied when the sidebar is resized.
        GeneralScroll.SizeChanged += (_, _) => UpdateLibraryRow();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is MetadataInspectorViewModel viewModel)
            {
                viewModel.PropertyChanged -= OnViewModelChanged;
                viewModel.PropertyChanged += OnViewModelChanged;
                UpdateLibraryRow();
            }
        };
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MetadataInspectorViewModel.IsKeywordLibraryOpen))
        {
            UpdateLibraryRow();
        }
    }

    /// <summary>
    /// Gives the keyword library the leftover height when it is open, and none when it is not.
    ///
    /// Set from here rather than bound: a <c>RowDefinition</c> is not a control, so
    /// <c>RowDefinitions</c> cannot take a binding in compiled XAML and <c>x:Name</c> on a row
    /// generates no field — the same constraint as native menu items and transforms. The grid itself
    /// is a control, so its rows are reachable by index.
    ///
    /// Collapsed leaves every row Auto, which puts the fields back at the top with the slack beneath
    /// them; expanded hands row 1 the star so the tick list fills the sidebar, which is what makes it
    /// usable for working through a folder one file at a time.
    /// </summary>
    private void UpdateLibraryRow()
    {
        if (DataContext is not MetadataInspectorViewModel viewModel)
        {
            return;
        }

        var expanded = viewModel.IsKeywordLibraryOpen;

        GeneralGrid.RowDefinitions[1].Height = expanded
            ? new GridLength(1, GridUnitType.Star)
            : GridLength.Auto;

        // A star row inside a ScrollViewer is measured against infinite height, so it reports the
        // full height of the tick list and the grid simply grows past the viewport — the star does
        // nothing and the fields below the library scroll out of reach. Capping the grid at the
        // viewport is what gives the star something to divide up.
        //
        // Only while expanded: capped all the time, a panel with every field showing would clip on a
        // short window with no way to scroll to the rest.
        GeneralGrid.MaxHeight = expanded && GeneralScroll.Bounds.Height > 0
            ? GeneralScroll.Bounds.Height
            : double.PositiveInfinity;
    }

    /// <summary>
    /// Opens Settings at the user's request rather than on its own.
    ///
    /// The panel could open Settings the moment it noticed an empty library, but that would take the
    /// window away from whoever was in the middle of something else — most sessions are not tagging
    /// sessions. Offering the door is enough.
    /// </summary>
    private async void OnSetUpKeywords(object? sender, RoutedEventArgs e)
    {
        if (this.FindAncestorOfType<MainWindow>() is { } window)
        {
            await window.OpenSettingsAsync();
        }
    }
}
