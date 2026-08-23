using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace BetterDAM.UI.Views;

public partial class MetadataInspectorView : UserControl
{
    public MetadataInspectorView() => InitializeComponent();

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
