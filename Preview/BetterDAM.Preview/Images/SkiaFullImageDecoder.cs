using BetterDAM.Core.Interfaces;
using BetterDAM.Core.Models;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace BetterDAM.Preview.Images;

/// <summary>
/// Decodes an image at native resolution for full-quality viewing.
///
/// Nothing here downscales or re-encodes: the preview pipeline already produces a 1600px JPEG, which
/// is what makes the grid fast, and looking at that fullscreen is looking at a rendition rather than
/// the photograph. RAW files go through their embedded preview, which is the largest thing available
/// without a demosaicing library — usually the camera's own full-size JPEG.
/// </summary>
public sealed class SkiaFullImageDecoder : IFullImageDecoder
{
    /// <summary>
    /// Ceiling on decoded pixels, about 80MP. Beyond this the image is decoded downscaled: 80MP is
    /// already 320 MB of BGRA, and a panorama that large would otherwise be able to exhaust memory
    /// from a double-click.
    /// </summary>
    private const long MaxPixels = 80_000_000;

    private readonly IEmbeddedPreviewExtractor _previews;
    private readonly IRawDecoder _raw;
    private readonly ISettingsService _settings;
    private readonly ILogger<SkiaFullImageDecoder> _logger;

    public SkiaFullImageDecoder(
        IEmbeddedPreviewExtractor previews,
        IRawDecoder raw,
        ISettingsService settings,
        ILogger<SkiaFullImageDecoder> logger)
    {
        _previews = previews;
        _raw = raw;
        _settings = settings;
        _logger = logger;
    }

    public Task<DecodedImage?> DecodeAsync(MediaFile file, CancellationToken cancellationToken = default)
        => Task.Run(() => DecodeCore(file, cancellationToken), cancellationToken);

    private async Task<DecodedImage?> DecodeCore(MediaFile file, CancellationToken cancellationToken)
    {
        if (file.MediaType != MediaType.Image)
        {
            return null;
        }

        try
        {
            if (SkiaThumbnailRenderer.CanDecode(file.FullPath))
            {
                await using var stream = File.OpenRead(file.FullPath);
                return Decode(stream, cancellationToken);
            }

            // RAW. Developing is the real thing but costs seconds; the embedded preview is instant.
            // Which one is wanted is a setting, and a failed develop falls back rather than showing
            // nothing.
            if (_settings.Current.DevelopRawFiles && _raw.IsAvailable)
            {
                if (await _raw.DevelopAsync(file, cancellationToken).ConfigureAwait(false) is { } developed)
                {
                    return developed;
                }

                _logger.LogDebug("Developing {File} produced nothing; falling back to its preview", file.FullPath);
            }

            var preview = await _previews.ExtractAsync(file, cancellationToken).ConfigureAwait(false);
            if (preview is null)
            {
                return null;
            }

            using var memory = new MemoryStream(preview);
            return Decode(memory, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            _logger.LogDebug("Cannot open {File} at full size; it no longer exists", file.FullPath);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decode {File} at full size", file.FullPath);
            return null;
        }
    }

    private static DecodedImage? Decode(Stream source, CancellationToken cancellationToken)
    {
        using var codec = SKCodec.Create(source);
        if (codec is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        using var decoded = DecodeWithinBudget(codec);
        if (decoded is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        using var oriented = SkiaThumbnailRenderer.ApplyOrientationTo(decoded, codec.EncodedOrigin);

        // BGRA to match what the UI blits, so handing it over is a copy rather than a conversion.
        var info = new SKImageInfo(oriented.Width, oriented.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var target = new SKBitmap(info);

        if (!oriented.CopyTo(target, SKColorType.Bgra8888))
        {
            return null;
        }

        return new DecodedImage(target.Bytes, target.Width, target.Height);
    }

    /// <summary>
    /// Decodes at full size, or at the nearest scale the codec offers when the image is enormous.
    /// Asking the codec to downscale is far cheaper than decoding everything and shrinking it.
    /// </summary>
    private static SKBitmap? DecodeWithinBudget(SKCodec codec)
    {
        var info = codec.Info;
        var pixels = (long)info.Width * info.Height;

        if (pixels <= MaxPixels)
        {
            return SKBitmap.Decode(codec);
        }

        var scale = Math.Sqrt(MaxPixels / (double)pixels);
        var supported = codec.GetScaledDimensions((float)scale);

        return SKBitmap.Decode(codec, new SKImageInfo(supported.Width, supported.Height));
    }
}
