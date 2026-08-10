using BetterDAM.Core.Interfaces;
using BetterDAM.Core.Models;
using Microsoft.Extensions.Logging;

namespace BetterDAM.Preview.Images;

/// <summary>
/// Produces thumbnails for images Skia cannot decode — CR3, NEF, ARW, RAF and friends — by
/// extracting the JPEG preview the camera embedded, then rendering that.
///
/// This is the same trick Bridge and Photo Mechanic use. It is also why RAW browsing can be fast:
/// nothing here develops the RAW.
/// </summary>
public sealed class RawThumbnailGenerator : IThumbnailGenerator
{
    private readonly IEmbeddedPreviewExtractor _extractor;
    private readonly ILogger<RawThumbnailGenerator> _logger;

    public RawThumbnailGenerator(IEmbeddedPreviewExtractor extractor, ILogger<RawThumbnailGenerator> logger)
    {
        _extractor = extractor;
        _logger = logger;
    }

    /// <summary>
    /// Anything that is an image but not directly decodable. Defining it as the complement rather
    /// than a RAW extension list means new formats are attempted rather than silently unsupported —
    /// and a file with no embedded preview simply yields null, as it does today.
    /// </summary>
    public bool CanHandle(MediaFile file)
        => file.MediaType == MediaType.Image
           && !SkiaThumbnailRenderer.CanDecode(file.FullPath)
           && _extractor.IsAvailable;

    public async Task<byte[]?> GenerateAsync(MediaFile file, int maxEdgePixels, CancellationToken cancellationToken = default)
    {
        var preview = await _extractor.ExtractAsync(file, cancellationToken).ConfigureAwait(false);
        if (preview is null)
        {
            return null;
        }

        try
        {
            // The embedded preview carries the RAW's EXIF orientation, so the shared renderer
            // rotates it correctly without needing to read the orientation separately.
            using var stream = new MemoryStream(preview);
            return SkiaThumbnailRenderer.Render(stream, maxEdgePixels, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to render the embedded preview of {File}", file.FullPath);
            return null;
        }
    }
}
