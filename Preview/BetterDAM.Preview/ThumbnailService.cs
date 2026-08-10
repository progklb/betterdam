using BetterDAM.Core.Interfaces;
using BetterDAM.Core.Models;
using BetterDAM.Preview.Cache;
using Microsoft.Extensions.Logging;

namespace BetterDAM.Preview;

public sealed class ThumbnailService : IThumbnailService, IDisposable
{
    private readonly ThumbnailCache _cache;
    private readonly IReadOnlyList<IThumbnailGenerator> _generators;
    private readonly ILogger<ThumbnailService> _logger;

    // Thumbnail work is CPU- and process-heavy. Bounding it keeps a large folder scan from
    // saturating every core and starving the UI thread of scheduling time.
    private readonly SemaphoreSlim _concurrency;

    public ThumbnailService(
        ThumbnailCache cache,
        IEnumerable<IThumbnailGenerator> generators,
        ILogger<ThumbnailService> logger)
    {
        _cache = cache;
        _generators = generators.ToList();
        _logger = logger;
        _concurrency = new SemaphoreSlim(Math.Max(2, Environment.ProcessorCount - 1));
    }

    public async Task<byte[]?> GetThumbnailAsync(MediaFile file, int maxEdgePixels, CancellationToken cancellationToken = default)
    {
        var key = _cache.GetCacheKey(file, maxEdgePixels);

        var cached = await _cache.TryReadAsync(key, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached;
        }

        var generator = _generators.FirstOrDefault(g => g.CanHandle(file));
        if (generator is null)
        {
            _logger.LogDebug("No thumbnail generator available for {File}", file.FullPath);
            return null;
        }

        await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another request may have produced this thumbnail while we queued.
            cached = await _cache.TryReadAsync(key, cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                return cached;
            }

            var generated = await generator.GenerateAsync(file, maxEdgePixels, cancellationToken).ConfigureAwait(false);
            if (generated is null)
            {
                return null;
            }

            await _cache.WriteAsync(key, generated, cancellationToken).ConfigureAwait(false);
            return generated;
        }
        finally
        {
            _concurrency.Release();
        }
    }

    public void Dispose() => _concurrency.Dispose();
}
