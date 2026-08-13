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

    public async Task<int> IndexAsync(
        IReadOnlyList<MediaFile> files,
        IProgress<JobProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (files.Count == 0 || !_metadata.IsAvailable)
        {
            return 0;
        }

        var indexed = 0;

        foreach (var chunk in files.Chunk(ChunkSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var metadata = await _metadata.ReadManyAsync(chunk, null, cancellationToken).ConfigureAwait(false);

            var entries = new List<CatalogEntry>(chunk.Length);
            foreach (var file in chunk)
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
            progress?.Report(new JobProgress(indexed, files.Count, chunk[^1].FileName));
        }

        _logger.LogInformation("Indexed {Count} of {Total} file(s) into the catalog", indexed, files.Count);
        return indexed;
    }

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
