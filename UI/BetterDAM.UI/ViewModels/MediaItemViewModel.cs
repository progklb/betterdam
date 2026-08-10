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

    public MediaItemViewModel(MediaFile file, IThumbnailService thumbnails)
    {
        File = file;
        _thumbnails = thumbnails;
    }

    public MediaFile File { get; }

    public string FileName => File.FileName;

    public bool IsVideo => File.MediaType == MediaType.Video;

    public string SizeDisplay => FormatSize(File.SizeBytes);

    public string ModifiedDisplay => File.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    [ObservableProperty]
    private Bitmap? _thumbnail;

    [ObservableProperty]
    private bool _thumbnailUnavailable;

    /// <summary>
    /// Requests the thumbnail once. Called when the item's container is realized, so a scan of
    /// 50,000 files only decodes the handful of items actually on screen.
    /// </summary>
    public async Task EnsureThumbnailAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _thumbnailRequested, 1) == 1)
        {
            return;
        }

        try
        {
            var bytes = await _thumbnails.GetThumbnailAsync(File, ThumbnailEdgePixels, cancellationToken).ConfigureAwait(false);
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
            Interlocked.Exchange(ref _thumbnailRequested, 0);
        }
        catch (Exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() => ThumbnailUnavailable = true);
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}
