using SkiaSharp;

namespace BetterDAM.Preview.Images;

/// <summary>
/// Turns encoded image bytes into a downscaled, correctly oriented JPEG thumbnail.
///
/// Shared by the still-image generator and the RAW generator: a RAW's embedded preview is just a
/// JPEG, and it carries the same EXIF orientation as the RAW itself, so both paths need identical
/// decode/orient/resize handling.
/// </summary>
internal static class SkiaThumbnailRenderer
{
    private const int JpegQuality = 85;

    private static readonly SKSamplingOptions Sampling = new(SKCubicResampler.Mitchell);

    /// <summary>Formats Skia can decode directly. Anything else needs its preview extracting first.</summary>
    private static readonly HashSet<string> DecodableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp"
    };

    public static bool CanDecode(string filePath)
        => DecodableExtensions.Contains(Path.GetExtension(filePath));

    /// <summary>Returns encoded JPEG bytes, or null when the source cannot be decoded.</summary>
    public static byte[]? Render(Stream source, int maxEdgePixels, CancellationToken cancellationToken)
    {
        using var codec = SKCodec.Create(source);
        if (codec is null)
        {
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

    /// <summary>
    /// Asks the codec for the nearest scale it can decode natively. For JPEG this avoids decoding a
    /// 50MP frame at full resolution just to shrink it, which is the difference between a browsable
    /// grid and a stalled one — and it matters doubly for RAW previews, which are often full-size.
    /// </summary>
    private static SKBitmap? DecodeDownsampled(SKCodec codec, int maxEdgePixels)
    {
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0)
        {
            return null;
        }

        var longestEdge = Math.Max(info.Width, info.Height);
        var scale = Math.Min(1f, (float)maxEdgePixels / longestEdge);
        var supported = codec.GetScaledDimensions(scale);

        // The codec only offers discrete scales (JPEG: eighths), and rounds down — asking for 320px
        // of a 2400px image yields 300px. Since the renderer never upscales, that would quietly
        // produce thumbnails smaller and softer than requested. Step back up until the decode is at
        // least the target size, then resize down to it precisely.
        while (Math.Max(supported.Width, supported.Height) < maxEdgePixels && scale < 1f)
        {
            scale = Math.Min(1f, scale * 2f);
            supported = codec.GetScaledDimensions(scale);
        }

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

    /// <summary>Shared with the full-size decoder: orientation handling is identical.</summary>
    internal static SKBitmap ApplyOrientationTo(SKBitmap source, SKEncodedOrigin origin)
        => ApplyOrientation(source, origin);

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
