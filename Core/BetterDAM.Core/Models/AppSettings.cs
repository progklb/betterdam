namespace BetterDAM.Core.Models;

/// <summary>
/// User preferences. Persisted outside the cache directory — settings must survive "Clear cache".
/// </summary>
public sealed record AppSettings
{
    public const long UnlimitedCache = 0;

    public static readonly AppSettings Default = new();

    /// <summary>
    /// Where derived data is written. Null means the platform default. Useful when the media lives
    /// on an external drive and the boot disk is small.
    /// </summary>
    public string? CacheDirectoryOverride { get; init; }

    /// <summary>
    /// Size ceiling for the thumbnail cache in bytes. <see cref="UnlimitedCache"/> disables trimming.
    /// When exceeded, the least recently used entries are evicted — the cache is disposable, so
    /// discarding old entries only costs regenerating them if they are needed again.
    /// </summary>
    public long CacheSizeLimitBytes { get; init; } = UnlimitedCache;

    public bool IsCacheLimited => CacheSizeLimitBytes > UnlimitedCache;
}
