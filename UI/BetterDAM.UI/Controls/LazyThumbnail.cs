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
    /// <summary>The item this control last asked for, so its work can be cancelled on recycle.</summary>
    private MediaItemViewModel? _requested;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RequestThumbnail();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // The tile scrolled out of view or its container was recycled; stop generating for it.
        CancelOutstanding();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        // A recycled container is reused for a different file: abandon the old one's work first.
        if (!ReferenceEquals(_requested, DataContext))
        {
            CancelOutstanding();
        }

        if (this.GetVisualRoot() is not null)
        {
            RequestThumbnail();
        }
    }

    private void RequestThumbnail()
    {
        if (DataContext is MediaItemViewModel item)
        {
            _requested = item;
            _ = item.EnsureThumbnailAsync();
        }
    }

    private void CancelOutstanding()
    {
        _requested?.CancelPendingThumbnail();
        _requested = null;
    }
}
