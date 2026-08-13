using Microsoft.Data.Sqlite;

namespace BetterDAM.Database;

/// <summary>
/// Creates and migrates the catalog schema.
///
/// Versioned from the start so later phases can add columns without asking anyone to delete their
/// catalog — the whole point of a migration step is that upgrading is never a data-loss event.
/// </summary>
internal static class CatalogSchema
{
    public const int CurrentVersion = 1;

    public static void Apply(SqliteConnection connection)
    {
        // WAL lets indexing write while the UI reads, which is the normal state of this application.
        Execute(connection, "PRAGMA journal_mode=WAL;");
        Execute(connection, "PRAGMA foreign_keys=ON;");

        Execute(connection, "CREATE TABLE IF NOT EXISTS SchemaVersion (Version INTEGER NOT NULL);");

        var version = GetVersion(connection);
        if (version >= CurrentVersion)
        {
            return;
        }

        if (version < 1)
        {
            ApplyVersion1(connection);
        }

        SetVersion(connection, CurrentVersion);
    }

    private static void ApplyVersion1(SqliteConnection connection)
    {
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS Media (
                Id           INTEGER PRIMARY KEY AUTOINCREMENT,
                Path         TEXT    NOT NULL UNIQUE,
                FileName     TEXT    NOT NULL,
                MediaType    INTEGER NOT NULL,
                SizeBytes    INTEGER NOT NULL,
                ModifiedUtc  INTEGER NOT NULL,
                CreatedUtc   INTEGER NOT NULL,
                Title        TEXT,
                Description  TEXT,
                Headline     TEXT,
                Label        TEXT,
                Creator      TEXT,
                Copyright    TEXT,
                Rating       INTEGER,
                Camera       TEXT,
                Lens         TEXT,
                CaptureDate  INTEGER,
                HasSidecar   INTEGER NOT NULL DEFAULT 0,
                IndexedUtc   INTEGER NOT NULL
            );
            """);

        // Filters people actually combine: type, rating and date.
        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_Media_MediaType ON Media(MediaType);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_Media_Rating ON Media(Rating);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_Media_CaptureDate ON Media(CaptureDate);");

        // Keywords are normalised so keyword:x is an exact indexed lookup rather than a text scan.
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS Keyword (
                Id    INTEGER PRIMARY KEY AUTOINCREMENT,
                Value TEXT NOT NULL COLLATE NOCASE UNIQUE
            );
            """);

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS MediaKeyword (
                MediaId   INTEGER NOT NULL REFERENCES Media(Id) ON DELETE CASCADE,
                KeywordId INTEGER NOT NULL REFERENCES Keyword(Id) ON DELETE CASCADE,
                PRIMARY KEY (MediaId, KeywordId)
            );
            """);

        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_MediaKeyword_KeywordId ON MediaKeyword(KeywordId);");

        // Full text over the fields worth searching by words. rowid is kept equal to Media.Id so a
        // re-index is a delete-by-rowid rather than a scan.
        Execute(connection, """
            CREATE VIRTUAL TABLE IF NOT EXISTS MediaSearch USING fts5(
                Title, Description, Headline, Keywords, Creator
            );
            """);
    }

    private static int GetVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(Version), 0) FROM SchemaVersion;";
        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
    }

    private static void SetVersion(SqliteConnection connection, int version)
    {
        Execute(connection, "DELETE FROM SchemaVersion;");

        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO SchemaVersion (Version) VALUES ($version);";
        command.Parameters.AddWithValue("$version", version);
        command.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
