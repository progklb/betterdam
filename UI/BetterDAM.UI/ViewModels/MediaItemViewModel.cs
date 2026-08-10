using Avalonia.Media.Imaging;
using Avalonia.Threading;
using BetterDAM.Core.Interfaces;
using BetterDAM.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterDAM.UI.ViewModels;

public sealed partial class MediaItemViewModel : ObservableObject
{
    /// <summary>
    /// Grid thumbnails are always rendered and cached at this size regardless of the zoom slider,
    /// so changing the display size scales existing cache entries instead of regenerating them.
    /// </summary>
    public const int ThumbnailEdgePixels = 320;

    private readonly IThumbnailService _thumbnails;
    private int _thumbnailRequested;
    private CancellationTokenSource? _loadCts;

    public MediaItemViewModel(MediaFile file, IThumbnailService thumbnails)
    {
        File = file;
        _thumbnails = thumbnails;
    }

    public MediaFile File { get; }

    public string FileName => File.FileName;

    public bool IsVideo => File.MediaType == MediaType.Video;

    public string SizeDisplay => ByteSize.Format(File.SizeBytes);

    public string ModifiedDisplay => File.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    [ObservableProperty]
    private Bitmap? _thumbnail;

    [ObservableProperty]
    private bool _thumbnailUnavailable;

    /// <summary>Drives the "modified" badge in the grid. Kept in sync with the pending-change store.</summary>
    [ObservableProperty]
    private bool _hasPendingChanges;

    /// <summary>Embedded metadata and the sidecar disagree. Set when the item is inspected.</summary>
    [ObservableProperty]
    private bool _hasConflicts;

    /// <summary>An XMP sidecar exists next to this file.</summary>
    [ObservableProperty]
    private bool _hasSidecar;

    /// <summary>
    /// Requests the thumbnail once. Called when the item's container is realized, so opening a
    /// folder of 50,000 files only decodes the handful of tiles actually on screen.
    /// </summary>
    public async Task EnsureThumbnailAsync()
    {
        if (Thumbnail is not null || Interlocked.Exchange(ref _thumbnailRequested, 1) == 1)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _loadCts, cts);
        previous?.Dispose();

        try
        {
            var bytes = await _thumbnails
                .GetThumbnailAsync(File, ThumbnailEdgePixels, ThumbnailPriority.Background, cts.Token)
                .ConfigureAwait(false);

            if (bytes is null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => ThumbnailUnavailable = true);
                return;
            }

            using var stream = new MemoryStream(bytes);
            var bitmap = new Bitmap(stream);
            await Dispatcher.UIThread.InvokeAsync(() => Thumbnail = bitmap);
        }
        catch (OperationCanceledException)
        {
            // Scrolled out of view before it finished — allow a fresh attempt if it comes back.
            Interlocked.Exchange(ref _thumbnailRequested, 0);
        }
        catch (Exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() => ThumbnailUnavailable = true);
        }
    }

    /// <summary>
    /// Abandons in-flight thumbnail work because the tile scrolled out of view. Without this, a fast
    /// scroll through a large folder leaves every tile it passed still queued and competing for the
    /// generator, which is exactly the work the user no longer cares about.
    ///
    /// A thumbnail that already arrived is kept — it costs nothing and makes scrolling back instant.
    /// </summary>
    public void CancelPendingThumbnail()
    {
        if (Thumbnail is not null)
        {
            return;
        }

        var cts = Interlocked.Exchange(ref _loadCts, null);
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            cts.Dispose();
        }

        Interlocked.Exchange(ref _thumbnailRequested, 0);
    }

}
