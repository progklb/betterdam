using System.Data;
using System.Text;
using BetterDAM.Core.Interfaces;
using BetterDAM.Core.Models;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace BetterDAM.Database;

/// <summary>
/// SQLite-backed search catalog, with FTS5 for word searching and normalised keywords for exact
/// keyword filters.
/// </summary>
public sealed class SqliteCatalog : ICatalog
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteCatalog> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _initialised;

    public SqliteCatalog(IAppPaths paths, ILogger<SqliteCatalog> logger)
    {
        _logger = logger;

        var path = paths.CatalogPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        if (!_initialised)
        {
            CatalogSchema.Apply(connection);
            _initialised = true;
        }

        return connection;
    }

    public async Task<CatalogStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var files = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Media;").ConfigureAwait(false);
        var keywords = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Keyword;").ConfigureAwait(false);

        return new CatalogStatistics(files, keywords);
    }

    public async Task UpsertAsync(IReadOnlyList<CatalogEntry> entries, CancellationToken cancellationToken = default)
    {
        if (entries.Count == 0)
        {
            return;
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await UpsertOneAsync(connection, (SqliteTransaction)transaction, entry).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task UpsertOneAsync(SqliteConnection connection, SqliteTransaction transaction, CatalogEntry entry)
    {
        var file = entry.File;
        var metadata = entry.Metadata;

        var id = await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO Media (Path, FileName, MediaType, SizeBytes, ModifiedUtc, CreatedUtc,
                               Title, Description, Headline, Label, Creator, Copyright, Rating,
                               Camera, Lens, CaptureDate, HasSidecar, IndexedUtc)
            VALUES (@Path, @FileName, @MediaType, @SizeBytes, @ModifiedUtc, @CreatedUtc,
                    @Title, @Description, @Headline, @Label, @Creator, @Copyright, @Rating,
                    @Camera, @Lens, @CaptureDate, @HasSidecar, @IndexedUtc)
            ON CONFLICT(Path) DO UPDATE SET
                FileName = excluded.FileName, MediaType = excluded.MediaType,
                SizeBytes = excluded.SizeBytes, ModifiedUtc = excluded.ModifiedUtc,
                CreatedUtc = excluded.CreatedUtc, Title = excluded.Title,
                Description = excluded.Description, Headline = excluded.Headline,
                Label = excluded.Label, Creator = excluded.Creator,
                Copyright = excluded.Copyright, Rating = excluded.Rating,
                Camera = excluded.Camera, Lens = excluded.Lens,
                CaptureDate = excluded.CaptureDate, HasSidecar = excluded.HasSidecar,
                IndexedUtc = excluded.IndexedUtc
            RETURNING Id;
            """,
            new
            {
                Path = file.FullPath,
                file.FileName,
                MediaType = (int)file.MediaType,
                file.SizeBytes,
                ModifiedUtc = file.ModifiedUtc.ToUnixTimeSeconds(),
                CreatedUtc = file.CreatedUtc.ToUnixTimeSeconds(),
                metadata.Title,
                metadata.Description,
                metadata.Headline,
                metadata.Label,
                metadata.Creator,
                metadata.Copyright,
                metadata.Rating,
                entry.Camera.Camera,
                entry.Camera.Lens,
                CaptureDate = entry.CaptureDate?.ToUnixTimeSeconds(),
                HasSidecar = entry.HasSidecar ? 1 : 0,
                IndexedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            },
            transaction).ConfigureAwait(false);

        // Keywords are rewritten wholesale: working out the difference would cost more than the
        // handful of rows involved.
        await connection.ExecuteAsync("DELETE FROM MediaKeyword WHERE MediaId = @id;", new { id }, transaction)
            .ConfigureAwait(false);

        foreach (var keyword in metadata.Keywords.Where(k => !string.IsNullOrWhiteSpace(k)))
        {
            await connection.ExecuteAsync(
                "INSERT OR IGNORE INTO Keyword (Value) VALUES (@keyword);",
                new { keyword }, transaction).ConfigureAwait(false);

            await connection.ExecuteAsync(
                """
                INSERT OR IGNORE INTO MediaKeyword (MediaId, KeywordId)
                SELECT @id, Id FROM Keyword WHERE Value = @keyword;
                """,
                new { id, keyword }, transaction).ConfigureAwait(false);
        }

        // FTS rows share Media.Id as their rowid, so refreshing one is a delete by rowid.
        await connection.ExecuteAsync("DELETE FROM MediaSearch WHERE rowid = @id;", new { id }, transaction)
            .ConfigureAwait(false);

        await connection.ExecuteAsync(
            """
            INSERT INTO MediaSearch (rowid, Title, Description, Headline, Keywords, Creator)
            VALUES (@id, @Title, @Description, @Headline, @Keywords, @Creator);
            """,
            new
            {
                id,
                metadata.Title,
                metadata.Description,
                metadata.Headline,
                Keywords = string.Join(' ', metadata.Keywords),
                metadata.Creator
            },
            transaction).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        SearchQuery query,
        int limit = 5000,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var (sql, parameters) = BuildSearch(query, limit);

        var rows = await connection.QueryAsync<SearchRow>(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.Select(r => new SearchHit(
            r.Path,
            r.FileName,
            (MediaType)r.MediaType,
            r.SizeBytes,
            DateTimeOffset.FromUnixTimeSeconds(r.ModifiedUtc),
            DateTimeOffset.FromUnixTimeSeconds(r.CreatedUtc),
            r.Rating is { } rating ? (int)rating : null,
            r.Title)).ToList();
    }

    /// <summary>
    /// Builds the WHERE clause from the parsed query. Every value is parameterised — a search box is
    /// user input, and it is not going anywhere near string-concatenated SQL.
    /// </summary>
    internal static (string Sql, DynamicParameters Parameters) BuildSearch(SearchQuery query, int limit)
    {
        var sql = new StringBuilder("""
            SELECT m.Path, m.FileName, m.MediaType, m.SizeBytes, m.ModifiedUtc, m.CreatedUtc, m.Rating, m.Title
            FROM Media m
            WHERE 1 = 1
            """);

        var parameters = new DynamicParameters();

        if (!query.FreeText.IsDefaultOrEmpty)
        {
            sql.Append("\n  AND m.Id IN (SELECT rowid FROM MediaSearch WHERE MediaSearch MATCH @match)");
            parameters.Add("match", BuildMatchExpression(query.FreeText));
        }

        if (query.MediaType is { } mediaType)
        {
            sql.Append("\n  AND m.MediaType = @mediaType");
            parameters.Add("mediaType", (int)mediaType);
        }

        if (query.Rating is { } rating)
        {
            sql.Append($"\n  AND m.Rating IS NOT NULL AND m.Rating {ToSql(rating.Operator)} @rating");
            parameters.Add("rating", rating.Value);
        }

        if (query.CaptureDate is { } date)
        {
            sql.Append($"\n  AND m.CaptureDate IS NOT NULL AND m.CaptureDate {ToSql(date.Operator)} @captureDate");
            parameters.Add("captureDate", date.Value.ToUnixTimeSeconds());
        }

        for (var i = 0; i < query.Keywords.Length; i++)
        {
            // EXISTS per keyword gives AND semantics: all of them must be present.
            sql.Append($"""

                  AND EXISTS (SELECT 1 FROM MediaKeyword mk
                              JOIN Keyword k ON k.Id = mk.KeywordId
                              WHERE mk.MediaId = m.Id AND k.Value = @keyword{i})
                """);
            parameters.Add($"keyword{i}", query.Keywords[i]);
        }

        for (var i = 0; i < query.Cameras.Length; i++)
        {
            sql.Append($"\n  AND m.Camera LIKE @camera{i}");
            parameters.Add($"camera{i}", $"%{query.Cameras[i]}%");
        }

        for (var i = 0; i < query.Lenses.Length; i++)
        {
            sql.Append($"\n  AND m.Lens LIKE @lens{i}");
            parameters.Add($"lens{i}", $"%{query.Lenses[i]}%");
        }

        sql.Append("\nORDER BY m.FileName COLLATE NOCASE\nLIMIT @limit;");
        parameters.Add("limit", limit);

        return (sql.ToString(), parameters);
    }

    /// <summary>
    /// Turns bare words into an FTS5 expression. Each term is quoted (so punctuation cannot be read
    /// as FTS syntax) and given a prefix wildcard, because "namib" should find "Namibia".
    /// </summary>
    internal static string BuildMatchExpression(IEnumerable<string> terms)
        => string.Join(" AND ", terms
            .Select(t => t.Replace("\"", string.Empty).Trim())
            .Where(t => t.Length > 0)
            .Select(t => $"\"{t}\"*"));

    private static string ToSql(ComparisonOperator op) => op switch
    {
        ComparisonOperator.GreaterThanOrEqual => ">=",
        ComparisonOperator.LessThanOrEqual => "<=",
        ComparisonOperator.GreaterThan => ">",
        ComparisonOperator.LessThan => "<",
        _ => "="
    };

    public async Task<int> RemoveMissingAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var paths = (await connection.QueryAsync<MediaRow>("SELECT Id, Path FROM Media;")
            .ConfigureAwait(false)).ToList();

        var missing = paths.Where(p => !File.Exists(p.Path)).Select(p => p.Id).ToList();
        if (missing.Count == 0)
        {
            return 0;
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            await connection.ExecuteAsync("DELETE FROM Media WHERE Id IN @missing;", new { missing }, (SqliteTransaction)transaction)
                .ConfigureAwait(false);
            await connection.ExecuteAsync("DELETE FROM MediaSearch WHERE rowid IN @missing;", new { missing }, (SqliteTransaction)transaction)
                .ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        _logger.LogInformation("Removed {Count} catalog entries for files that no longer exist", missing.Count);
        return missing.Count;
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

            foreach (var table in (string[])["MediaKeyword", "Keyword", "MediaSearch", "Media"])
            {
                await connection.ExecuteAsync($"DELETE FROM {table};").ConfigureAwait(false);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// SQLite hands back every integer as a 64-bit value, so the columns are read as such and
    /// narrowed on the way out — declaring them as int makes Dapper fail to find a matching
    /// constructor at all.
    /// </summary>
    private sealed record SearchRow(
        string Path,
        string FileName,
        long MediaType,
        long SizeBytes,
        long ModifiedUtc,
        long CreatedUtc,
        long? Rating,
        string? Title);

    private sealed record MediaRow(long Id, string Path);
}
