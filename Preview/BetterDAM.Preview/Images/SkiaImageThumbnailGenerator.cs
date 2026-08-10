using BetterDAM.Core.Interfaces;
using BetterDAM.Core.Models;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace BetterDAM.Preview.Images;

/// <summary>
/// Decodes still images with Skia. RAW formats are not handled here — Skia cannot decode them, and
/// pulling the embedded preview out of a RAW is an ExifTool job that arrives in a later phase.
/// </summary>
public sealed class SkiaImageThumbnailGenerator : IThumbnailGenerator
{
    private const int JpegQuality = 85;

    private static readonly SKSamplingOptions Sampling = new(SKCubicResampler.Mitchell);

    private static readonly HashSet<string> DecodableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp"
    };

    private readonly ILogger<SkiaImageThumbnailGenerator> _logger;

    public SkiaImageThumbnailGenerator(ILogger<SkiaImageThumbnailGenerator> logger)
    {
        _logger = logger;
    }

    public bool CanHandle(MediaFile file)
        => file.MediaType == MediaType.Image && DecodableExtensions.Contains(Path.GetExtension(file.FullPath));

    public Task<byte[]?> GenerateAsync(MediaFile file, int maxEdgePixels, CancellationToken cancellationToken = default)
        => Task.Run(() => Generate(file, maxEdgePixels, cancellationToken), cancellationToken);

    private byte[]? Generate(MediaFile file, int maxEdgePixels, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = File.OpenRead(file.FullPath);
            using var codec = SKCodec.Create(stream);
            if (codec is null)
            {
                _logger.LogDebug("No Skia codec for {File}", file.FullPath);
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();

            using var decoded = DecodeDownsampled(codec, maxEdgePixels);
            if (decoded is null)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();

            using var oriented = ApplyOrientation(decoded, codec.EncodedOrigin);
            using var resized = ResizeToFit(oriented, maxEdgePixels);
            using var image = SKImage.FromBitmap(resized);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
            return data.ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate thumbnail for {File}", file.FullPath);
            return null;
        }
    }

    /// <summary>
    /// Asks the codec for the nearest scale it can decode natively. For JPEG this avoids decoding a
    /// 50MP frame at full resolution just to shrink it, which is the difference between a browsable
    /// grid and a stalled one.
    /// </summary>
    private static SKBitmap? DecodeDownsampled(SKCodec codec, int maxEdgePixels)
    {
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0)
        {
            return null;
        }

        var longestEdge = Math.Max(info.Width, info.Height);
        var desiredScale = Math.Min(1f, (float)maxEdgePixels / longestEdge);
        var supported = codec.GetScaledDimensions(desiredScale);

        var decodeInfo = new SKImageInfo(supported.Width, supported.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(decodeInfo);

        var result = codec.GetPixels(decodeInfo, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            bitmap.Dispose();
            return null;
        }

        return bitmap;
    }

    private static SKBitmap ResizeToFit(SKBitmap source, int maxEdgePixels)
    {
        var longestEdge = Math.Max(source.Width, source.Height);
        if (longestEdge <= maxEdgePixels)
        {
            return source.Copy();
        }

        var scale = (float)maxEdgePixels / longestEdge;
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        return source.Resize(new SKImageInfo(width, height, source.ColorType, source.AlphaType), Sampling)
            ?? source.Copy();
    }

    private static SKBitmap ApplyOrientation(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.Default or SKEncodedOrigin.TopLeft)
        {
            return source.Copy();
        }

        float w = source.Width;
        float h = source.Height;

        // Each matrix maps a source pixel to its place in the upright image. Origins 5-8 transpose
        // the axes, so the target bitmap swaps width and height.
        var (matrix, swapsAxes) = origin switch
        {
            SKEncodedOrigin.TopRight => (Affine(-1, 0, w, 0, 1, 0), false),
            SKEncodedOrigin.BottomRight => (Affine(-1, 0, w, 0, -1, h), false),
            SKEncodedOrigin.BottomLeft => (Affine(1, 0, 0, 0, -1, h), false),
            SKEncodedOrigin.LeftTop => (Affine(0, 1, 0, 1, 0, 0), true),
            SKEncodedOrigin.RightTop => (Affine(0, -1, h, 1, 0, 0), true),
            SKEncodedOrigin.RightBottom => (Affine(0, -1, h, -1, 0, w), true),
            SKEncodedOrigin.LeftBottom => (Affine(0, 1, 0, -1, 0, w), true),
            _ => (SKMatrix.Identity, false)
        };

        var targetWidth = swapsAxes ? source.Height : source.Width;
        var targetHeight = swapsAxes ? source.Width : source.Height;

        var target = new SKBitmap(new SKImageInfo(targetWidth, targetHeight, source.ColorType, source.AlphaType));
        using var canvas = new SKCanvas(target);
        using var image = SKImage.FromBitmap(source);
        canvas.SetMatrix(matrix);
        canvas.DrawImage(image, 0, 0, Sampling);
        canvas.Flush();
        return target;
    }

    private static SKMatrix Affine(float scaleX, float skewX, float transX, float skewY, float scaleY, float transY)
        => new(scaleX, skewX, transX, skewY, scaleY, transY, 0, 0, 1);
}
