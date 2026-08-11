using BetterDAM.Core.Interfaces;
using BetterDAM.Core.Models;
using Microsoft.Extensions.Logging;

namespace BetterDAM.Preview.Cache;

/// <summary>
/// Statistics, clearing, and rolling size-cap eviction for the derived-data cache — both thumbnails
/// and video proxies, since both are disposable and both compete for the same disk budget.
///
/// Eviction is safe to do bluntly: entries are content-addressed and independent, so deleting any
/// of them only costs regenerating it if it is wanted again.
/// </summary>
public sealed class ThumbnailCacheMaintenance : ICacheMaintenance
{
    /// <summary>
    /// Bytes written before a trim is considered. Trimming enumerates the whole cache directory, so
    /// doing it after every thumbnail would cost more than it saves.
    /// </summary>
    private const long TrimCheckInterval = 32L * 1024 * 1024;

    /// <summary>
    /// Trim down to slightly under the limit so the next few writes do not immediately trigger
    /// another pass.
    /// </summary>
    private const double TrimTargetRatio = 0.9;

    private readonly IAppPaths _paths;
    private readonly ISettingsService _settings;
    private readonly ILogger<ThumbnailCacheMaintenance> _logger;

    private long _bytesSinceTrim;
    private int _trimInFlight;

    public ThumbnailCacheMaintenance(IAppPaths paths, ISettingsService settings, ILogger<ThumbnailCacheMaintenance> logger)
    {
        _paths = paths;
        _settings = settings;
        _logger = logger;
    }

    public Task<CacheStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            var files = EnumerateEntries(cancellationToken);
            return new CacheStatistics(files.Sum(f => f.Length), files.Count);
        }, cancellationToken);

    public Task<long> ClearAsync(CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            var freed = 0L;

            foreach (var entry in EnumerateEntries(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                freed += TryDelete(entry);
            }

            RemoveEmptyShards(cancellationToken);
            Interlocked.Exchange(ref _bytesSinceTrim, 0);

            _logger.LogInformation("Cleared the cache, freeing {Bytes}", ByteSize.Format(freed));
            return freed;
        }, cancellationToken);

    public Task<long> TrimAsync(CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            var limit = _settings.Current.CacheSizeLimitBytes;
            if (limit <= AppSettings.UnlimitedCache)
            {
                return 0L;
            }

            var entries = EnumerateEntries(cancellationToken);
            var total = entries.Sum(e => e.Length);
            if (total <= limit)
            {
                return 0L;
            }

            var target = (long)(limit * TrimTargetRatio);
            var freed = 0L;

            // Least recently used first. Access times are unreliable on some volumes (noatime), so
            // last-write is used as the ordering key — for an immutable, content-addressed cache the
            // two only differ for entries that were read but never rewritten.
            foreach (var entry in entries.OrderBy(e => e.LastWriteTimeUtc))
            {
                if (total - freed <= target)
                {
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                freed += TryDelete(entry);
            }

            RemoveEmptyShards(cancellationToken);
            Interlocked.Exchange(ref _bytesSinceTrim, 0);

            if (freed > 0)
            {
                _logger.LogInformation(
                    "Trimmed the cache to its {Limit} limit, freeing {Freed}",
                    ByteSize.Format(limit), ByteSize.Format(freed));
            }

            return freed;
        }, cancellationToken);

    public void NotifyBytesWritten(long bytes)
    {
        if (!_settings.Current.IsCacheLimited)
        {
            return;
        }

        if (Interlocked.Add(ref _bytesSinceTrim, bytes) < TrimCheckInterval)
        {
            return;
        }

        // Only ever one trim at a time, and never on the caller's thread.
        if (Interlocked.CompareExchange(ref _trimInFlight, 1, 0) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _bytesSinceTrim, 0);

        _ = Task.Run(async () =>
        {
            try
            {
                await TrimAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Background cache trim failed");
            }
            finally
            {
                Interlocked.Exchange(ref _trimInFlight, 0);
            }
        });
    }

    /// <summary>Cache directories whose contents are disposable, newest concern first.</summary>
    private IEnumerable<string> CacheDirectories =>
        [_paths.ThumbnailCacheRoot, _paths.VideoProxyCacheRoot];

    private List<FileInfo> EnumerateEntries(CancellationToken cancellationToken)
    {
        var entries = new List<FileInfo>();

        foreach (var root in CacheDirectories)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var info = new FileInfo(path);
                    if (info.Exists)
                    {
                        entries.Add(info);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not enumerate the cache at {Root}", root);
            }
        }

        return entries;
    }

    private long TryDelete(FileInfo entry)
    {
        try
        {
            var length = entry.Length;
            entry.Delete();
            return length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not delete the cache entry {Path}", entry.FullName);
            return 0;
        }
    }

    private void RemoveEmptyShards(CancellationToken cancellationToken)
    {
        foreach (var root in CacheDirectories)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (var shard in Directory.EnumerateDirectories(root))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!Directory.EnumerateFileSystemEntries(shard).Any())
                    {
                        Directory.Delete(shard);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Could not tidy empty cache shards under {Root}", root);
            }
        }
    }
}
