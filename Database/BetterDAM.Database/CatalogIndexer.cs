using System.Globalization;
using BetterDAM.Core.Interfaces;
using BetterDAM.Core.Models;
using Microsoft.Extensions.Logging;

namespace BetterDAM.Database;

/// <summary>
/// Reads metadata for a set of files and writes it into the catalog.
///
/// Work is chunked so a large library reports progress and can be cancelled part-way without losing
/// what it has already indexed — the alternative, one enormous transaction, would make a cancelled
/// index of 50,000 files worth nothing.
/// </summary>
public sealed class CatalogIndexer : ICatalogIndexer
{
    private const int ChunkSize = 100;

    private readonly IMetadataProvider _metadata;
    private readonly ICatalog _catalog;
    private readonly ILogger<CatalogIndexer> _logger;

    public CatalogIndexer(IMetadataProvider metadata, ICatalog catalog, ILogger<CatalogIndexer> logger)
    {
        _metadata = metadata;
        _catalog = catalog;
        _logger = logger;
    }

    public async Task<IndexResult> IndexAsync(
        IReadOnlyList<MediaFile> files,
        IProgress<JobProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (files.Count == 0 || !_metadata.IsAvailable)
        {
            return default;
        }

        var indexed = 0;
        var skipped = 0;
        var seen = 0;

        foreach (var chunk in files.Chunk(ChunkSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Ask what is already known before reading anything. Metadata reads shell out to
            // ExifTool and dominate the cost; this query does not.
            var known = await _catalog
                .GetIndexedStampsAsync(chunk.Select(f => f.FullPath).ToList(), cancellationToken)
                .ConfigureAwait(false);

            var stale = chunk.Where(file => NeedsIndexing(file, known)).ToArray();

            seen += chunk.Length;
            skipped += chunk.Length - stale.Length;

            if (stale.Length == 0)
            {
                progress?.Report(new JobProgress(seen, files.Count, chunk[^1].FileName));
                continue;
            }

            var metadata = await _metadata.ReadManyAsync(stale, null, cancellationToken).ConfigureAwait(false);

            var entries = new List<CatalogEntry>(stale.Length);
            foreach (var file in stale)
            {
                if (!metadata.TryGetValue(file.FullPath, out var read))
                {
                    continue;
                }

                entries.Add(new CatalogEntry(
                    file,
                    read.Effective,
                    read.Camera,
                    read.HasSidecar,
                    ParseCaptureDate(read.Camera.CaptureDate)));
            }

            await _catalog.UpsertAsync(entries, cancellationToken).ConfigureAwait(false);

            indexed += entries.Count;
            progress?.Report(new JobProgress(seen, files.Count, chunk[^1].FileName));
        }

        _logger.LogInformation(
            "Indexed {Count} file(s), skipped {Skipped} already current, of {Total}",
            indexed,
            skipped,
            files.Count);

        return new IndexResult(indexed, skipped);
    }

    /// <summary>
    /// True when the catalog has no record of the file, or its size or modified time has moved.
    ///
    /// Comparing size *and* modified time rather than either alone: an edit that preserves the
    /// timestamp usually changes the size, and one that preserves the size usually changes the
    /// timestamp. Content hashing would be exact, but reading every byte of every file would cost
    /// far more than the metadata read it is trying to avoid.
    /// </summary>
    internal static bool NeedsIndexing(MediaFile file, IReadOnlyDictionary<string, IndexedStamp> known)
        => !known.TryGetValue(file.FullPath, out var stamp)
           || stamp.SizeBytes != file.SizeBytes
           || stamp.ModifiedUtc != file.ModifiedUtc.ToUnixTimeSeconds();

    /// <summary>
    /// EXIF dates use colons between date parts — "2024:06:01 09:15:22" — which no standard parser
    /// accepts, so the date portion is normalised before parsing.
    /// </summary>
    internal static DateTimeOffset? ParseCaptureDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        var separator = text.IndexOf(' ');

        if (separator > 0)
        {
            text = string.Concat(text[..separator].Replace(':', '-'), text[separator..]);
        }
        else
        {
            text = text.Replace(':', '-');
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }
}
