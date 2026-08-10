using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using BetterDAM.UI.ViewModels;

namespace BetterDAM.UI.Controls;

/// <summary>
/// Triggers thumbnail generation only once its container is realized by the virtualizing panel.
/// This is what keeps opening a folder of tens of thousands of files cheap.
/// </summary>
public sealed class LazyThumbnail : Image
{
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RequestThumbnail();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (this.GetVisualRoot() is not null)
        {
            RequestThumbnail();
        }
    }

    private void RequestThumbnail()
    {
        if (DataContext is MediaItemViewModel item)
        {
            _ = item.EnsureThumbnailAsync();
        }
    }
}
