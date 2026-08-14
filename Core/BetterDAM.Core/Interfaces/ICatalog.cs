using BetterDAM.Core.Models;

namespace BetterDAM.Core.Interfaces;

/// <summary>One file's searchable facts, ready to be written to the catalog.</summary>
public sealed record CatalogEntry(
    MediaFile File,
    EditableMetadata Metadata,
    CameraInfo Camera,
    bool HasSidecar,
    DateTimeOffset? CaptureDate);

/// <param name="SizeBytes">
/// Size on disk including SQLite's write-ahead log and shared-memory files, which can dwarf the
/// main database file and would otherwise make the reported size look wrong.
/// </param>
public sealed record CatalogStatistics(int FileCount, int KeywordCount, long SizeBytes)
{
    public static readonly CatalogStatistics Empty = new(0, 0, 0);
}

/// <summary>
/// The local search catalog.
///
/// It is a cache of what is already in the media and its sidecars — the files stay authoritative,
/// and deleting the catalog only costs re-indexing.
/// </summary>
public interface ICatalog
{
    Task<CatalogStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);

    /// <summary>Inserts or updates entries, keyed by path.</summary>
    Task UpsertAsync(IReadOnlyList<CatalogEntry> entries, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, int limit = 5000, CancellationToken cancellationToken = default);

    /// <summary>Forgets files that no longer exist on disk, so results cannot point at nothing.</summary>
    Task<int> RemoveMissingAsync(CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}

/// <summary>Populates the catalog from a set of files.</summary>
public interface ICatalogIndexer
{
    Task<int> IndexAsync(
        IReadOnlyList<MediaFile> files,
        IProgress<JobProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
