using BetterDAM.Core.Interfaces;
using BetterDAM.Core.Models;
using Microsoft.Extensions.Logging;

namespace BetterDAM.Preview.Images;

/// <summary>
/// Decodes still images Skia understands directly. RAW formats are handled by
/// <see cref="RawThumbnailGenerator"/>, which extracts their embedded preview first.
/// </summary>
public sealed class SkiaImageThumbnailGenerator : IThumbnailGenerator
{
    private readonly ILogger<SkiaImageThumbnailGenerator> _logger;

    public SkiaImageThumbnailGenerator(ILogger<SkiaImageThumbnailGenerator> logger)
    {
        _logger = logger;
    }

    public bool CanHandle(MediaFile file)
        => file.MediaType == MediaType.Image && SkiaThumbnailRenderer.CanDecode(file.FullPath);

    public Task<byte[]?> GenerateAsync(MediaFile file, int maxEdgePixels, CancellationToken cancellationToken = default)
        => Task.Run(() => Generate(file, maxEdgePixels, cancellationToken), cancellationToken);

    private byte[]? Generate(MediaFile file, int maxEdgePixels, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = File.OpenRead(file.FullPath);
            return SkiaThumbnailRenderer.Render(stream, maxEdgePixels, cancellationToken);
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
}
