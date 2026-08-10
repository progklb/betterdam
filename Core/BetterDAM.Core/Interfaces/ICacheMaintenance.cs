namespace BetterDAM.Core.Interfaces;

public sealed record CacheStatistics(long TotalBytes, int FileCount)
{
    public static readonly CacheStatistics Empty = new(0, 0);
}

/// <summary>
/// Housekeeping for the disposable thumbnail cache.
/// </summary>
public interface ICacheMaintenance
{
    Task<CacheStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);

    /// <summary>Deletes every cached thumbnail. Returns the bytes freed.</summary>
    Task<long> ClearAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Evicts least-recently-used entries until the cache fits the configured limit. Returns the
    /// bytes freed; zero when no limit is set or the cache is already within it.
    /// </summary>
    Task<long> TrimAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Tells the maintainer how much was just written, so trimming can happen on its own once
    /// enough has accumulated rather than being scheduled by every caller.
    /// </summary>
    void NotifyBytesWritten(long bytes);
}
