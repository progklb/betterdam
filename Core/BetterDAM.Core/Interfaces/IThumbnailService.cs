using BetterDAM.Core.Models;

namespace BetterDAM.Core.Interfaces;

/// <summary>
/// Produces cached thumbnail images. Returns encoded image bytes rather than a UI bitmap type so
/// that the preview layer stays independent of the UI toolkit.
/// </summary>
public interface IThumbnailService
{
    Task<byte[]?> GetThumbnailAsync(MediaFile file, int maxEdgePixels, CancellationToken cancellationToken = default);
}

/// <summary>
/// Generates a thumbnail for a single class of media. Registered implementations are tried in order.
/// </summary>
public interface IThumbnailGenerator
{
    bool CanHandle(MediaFile file);

    Task<byte[]?> GenerateAsync(MediaFile file, int maxEdgePixels, CancellationToken cancellationToken = default);
}
