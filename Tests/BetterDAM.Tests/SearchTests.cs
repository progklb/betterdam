using BetterDAM.Core.Interfaces;
using BetterDAM.Core.Models;
using BetterDAM.Core.Services;
using BetterDAM.Database;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BetterDAM.Tests;

public class SearchQueryParserTests
{
    [Fact]
    public void An_empty_query_is_empty()
    {
        Assert.True(SearchQueryParser.Parse(null).IsEmpty);
        Assert.True(SearchQueryParser.Parse("   ").IsEmpty);
    }

    [Fact]
    public void Bare_words_become_free_text()
    {
        var query = SearchQueryParser.Parse("lioness dawn");

        Assert.Equal(["lioness", "dawn"], query.FreeText.ToArray());
    }

    [Fact]
    public void Keyword_filters_are_recognised()
    {
        var query = SearchQueryParser.Parse("keyword:motorcycle kw:travel");

        Assert.Equal(["motorcycle", "travel"], query.Keywords.ToArray());
        Assert.Empty(query.FreeText);
    }

    [Theory]
    [InlineData("rating:>=4", ComparisonOperator.GreaterThanOrEqual, 4)]
    [InlineData("rating:<=2", ComparisonOperator.LessThanOrEqual, 2)]
    [InlineData("rating:>3", ComparisonOperator.GreaterThan, 3)]
    [InlineData("rating:<1", ComparisonOperator.LessThan, 1)]
    [InlineData("rating:5", ComparisonOperator.Equal, 5)]
    public void Rating_comparisons_are_parsed(string text, ComparisonOperator op, int value)
    {
        var rating = SearchQueryParser.Parse(text).Rating;

        Assert.NotNull(rating);
        Assert.Equal(op, rating.Operator);
        Assert.Equal(value, rating.Value);
    }

    [Theory]
    [InlineData("rating:9")]
    [InlineData("rating:abc")]
    [InlineData("rating:")]
    public void A_nonsense_rating_is_reported_rather_than_ignored(string text)
    {
        var query = SearchQueryParser.Parse(text);

        // Silently dropping a filter would return results the user did not ask for.
        Assert.Null(query.Rating);
        Assert.NotEmpty(query.UnrecognisedTerms);
    }

    [Theory]
    [InlineData("type:video", MediaType.Video)]
    [InlineData("type:videos", MediaType.Video)]
    [InlineData("type:image", MediaType.Image)]
    [InlineData("type:photos", MediaType.Image)]
    public void Media_type_filters_are_parsed(string text, MediaType expected)
        => Assert.Equal(expected, SearchQueryParser.Parse(text).MediaType);

    [Fact]
    public void An_unknown_media_type_is_reported()
    {
        var query = SearchQueryParser.Parse("type:audio");

        Assert.Null(query.MediaType);
        Assert.Contains("type:audio", query.UnrecognisedTerms);
    }

    [Fact]
    public void Quoted_values_keep_their_spaces()
    {
        var query = SearchQueryParser.Parse("lens:\"RF 100-500\"");

        Assert.Equal(["RF 100-500"], query.Lenses.ToArray());
    }

    [Fact]
    public void Filters_combine()
    {
        var query = SearchQueryParser.Parse("rating:>=4 AND keyword:motorcycle AND type:video");

        Assert.Equal(4, query.Rating!.Value);
        Assert.Equal(["motorcycle"], query.Keywords.ToArray());
        Assert.Equal(MediaType.Video, query.MediaType);

        // A literal AND is accepted but is not itself a search term.
        Assert.Empty(query.FreeText);
    }

    [Fact]
    public void Camera_filters_are_parsed()
        => Assert.Equal(["Sony"], SearchQueryParser.Parse("camera:Sony").Cameras.ToArray());

    [Theory]
    [InlineData("date:>=2024-01-01", ComparisonOperator.GreaterThanOrEqual)]
    [InlineData("date:<2020-06-15", ComparisonOperator.LessThan)]
    public void Date_comparisons_are_parsed(string text, ComparisonOperator op)
    {
        var date = SearchQueryParser.Parse(text).CaptureDate;

        Assert.NotNull(date);
        Assert.Equal(op, date.Operator);
    }

    [Fact]
    public void A_bare_year_means_from_the_start_of_that_year()
    {
        var date = SearchQueryParser.Parse("date:2024").CaptureDate;

        Assert.NotNull(date);
        Assert.Equal(2024, date.Value.Year);
        Assert.Equal(ComparisonOperator.GreaterThanOrEqual, date.Operator);
    }

    [Fact]
    public void An_unknown_field_is_treated_as_text_rather_than_discarded()
    {
        // Someone searching for a URL should still get results.
        var query = SearchQueryParser.Parse("http://example.com");

        Assert.Contains("http://example.com", query.FreeText);
    }

    [Fact]
    public void Tokenizing_respects_quotes()
        => Assert.Equal(
            ["lens:\"RF 100-500\"", "rating:>=4"],
            SearchQueryParser.Tokenize("lens:\"RF 100-500\" rating:>=4").ToArray());
}

public class CatalogQueryBuilderTests
{
    [Fact]
    public void Free_text_becomes_a_prefix_match()
    {
        // "namib" should find "Namibia", so every term gets a trailing wildcard.
        Assert.Equal("\"namib\"*", SqliteCatalog.BuildMatchExpression(["namib"]));
    }

    [Fact]
    public void Multiple_terms_are_anded()
        => Assert.Equal("\"a\"* AND \"b\"*", SqliteCatalog.BuildMatchExpression(["a", "b"]));

    [Fact]
    public void Quotes_in_a_term_cannot_break_out_of_the_expression()
    {
        // A stray quote would otherwise turn into FTS syntax and throw.
        Assert.Equal("\"ab\"*", SqliteCatalog.BuildMatchExpression(["a\"b"]));
    }

    [Fact]
    public void Every_value_is_parameterised()
    {
        var query = SearchQueryParser.Parse("keyword:motorcycle camera:Sony rating:>=4 type:video lioness");
        var (sql, parameters) = SqliteCatalog.BuildSearch(query, 100);

        // A search box is user input; nothing from it may be concatenated into SQL.
        Assert.DoesNotContain("motorcycle", sql);
        Assert.DoesNotContain("Sony", sql);
        Assert.DoesNotContain("lioness", sql);
        Assert.Contains("@keyword0", sql);
        Assert.Contains("@camera0", sql);
        Assert.Contains("@rating", sql);
        Assert.Contains("keyword0", parameters.ParameterNames);
    }
}

public class CaptureDateParsingTests
{
    [Fact]
    public void Exif_style_dates_are_understood()
    {
        // EXIF uses colons between date parts, which no standard parser accepts as-is.
        var parsed = CatalogIndexer.ParseCaptureDate("2024:06:01 09:15:22");

        Assert.NotNull(parsed);
        Assert.Equal(new DateTime(2024, 6, 1, 9, 15, 22), parsed.Value.DateTime);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a date")]
    public void Unparseable_values_yield_null(string? value)
        => Assert.Null(CatalogIndexer.ParseCaptureDate(value));
}

public class SqliteCatalogTests
{
    private static SqliteCatalog Create(TempFolder temp)
        => new(new TestPaths(temp.Path), NullLogger<SqliteCatalog>.Instance);

    private static CatalogEntry Entry(
        string path,
        MediaType type = MediaType.Image,
        string? title = null,
        string? description = null,
        int? rating = null,
        string? camera = null,
        string? lens = null,
        DateTimeOffset? captureDate = null,
        params string[] keywords)
        => new(
            new MediaFile
            {
                FullPath = path,
                FileName = Path.GetFileName(path),
                MediaType = type,
                SizeBytes = 100,
                ModifiedUtc = DateTimeOffset.UnixEpoch,
                CreatedUtc = DateTimeOffset.UnixEpoch
            },
            new EditableMetadata
            {
                Title = title,
                Description = description,
                Rating = rating,
                Keywords = [.. keywords]
            },
            new CameraInfo { Camera = camera, Lens = lens },
            HasSidecar: false,
            captureDate);

    private static async Task<IReadOnlyList<string>> SearchPathsAsync(SqliteCatalog catalog, string query)
        => (await catalog.SearchAsync(SearchQueryParser.Parse(query))).Select(h => h.FullPath).ToList();

    [Fact]
    public async Task An_empty_catalog_reports_nothing()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);

        var stats = await catalog.GetStatisticsAsync();

        Assert.Equal(0, stats.FileCount);
    }

    [Fact]
    public async Task Entries_can_be_stored_and_counted()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);

        await catalog.UpsertAsync([
            Entry("/a.jpg", keywords: ["wildlife", "namibia"]),
            Entry("/b.jpg", keywords: ["wildlife"])
        ]);

        var stats = await catalog.GetStatisticsAsync();

        Assert.Equal(2, stats.FileCount);
        Assert.Equal(2, stats.KeywordCount);
    }

    [Fact]
    public async Task Re_indexing_a_file_updates_rather_than_duplicating_it()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);

        await catalog.UpsertAsync([Entry("/a.jpg", title: "First")]);
        await catalog.UpsertAsync([Entry("/a.jpg", title: "Second")]);

        var stats = await catalog.GetStatisticsAsync();
        Assert.Equal(1, stats.FileCount);

        var hits = await catalog.SearchAsync(SearchQueryParser.Parse("Second"));
        Assert.Single(hits);
        Assert.Equal("Second", hits[0].Title);

        // The old text must no longer match, or the index has drifted from the data.
        Assert.Empty(await catalog.SearchAsync(SearchQueryParser.Parse("First")));
    }

    [Fact]
    public async Task Free_text_searches_titles_and_descriptions()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);

        await catalog.UpsertAsync([
            Entry("/a.jpg", title: "Lioness at dawn"),
            Entry("/b.jpg", description: "Motorcycle on a gravel road"),
            Entry("/c.jpg", title: "Something else")
        ]);

        Assert.Equal(["/a.jpg"], await SearchPathsAsync(catalog, "lioness"));
        Assert.Equal(["/b.jpg"], await SearchPathsAsync(catalog, "gravel"));
    }

    [Fact]
    public async Task Free_text_matches_on_a_prefix()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);
        await catalog.UpsertAsync([Entry("/a.jpg", title: "Namibia trip")]);

        Assert.Equal(["/a.jpg"], await SearchPathsAsync(catalog, "namib"));
    }

    [Fact]
    public async Task Free_text_finds_keywords_too()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);
        await catalog.UpsertAsync([Entry("/a.jpg", keywords: ["motorcycle"])]);

        Assert.Equal(["/a.jpg"], await SearchPathsAsync(catalog, "motorcycle"));
    }

    [Fact]
    public async Task Keyword_filters_match_exactly_and_case_insensitively()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);

        await catalog.UpsertAsync([
            Entry("/a.jpg", keywords: ["Motorcycle", "Travel"]),
            Entry("/b.jpg", keywords: ["Travel"])
        ]);

        Assert.Equal(["/a.jpg"], await SearchPathsAsync(catalog, "keyword:motorcycle"));
        Assert.Equal(["/a.jpg", "/b.jpg"], (await SearchPathsAsync(catalog, "keyword:TRAVEL")).Order().ToArray());
    }

    [Fact]
    public async Task Multiple_keywords_must_all_be_present()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);

        await catalog.UpsertAsync([
            Entry("/a.jpg", keywords: ["motorcycle", "travel"]),
            Entry("/b.jpg", keywords: ["motorcycle"])
        ]);

        Assert.Equal(["/a.jpg"], await SearchPathsAsync(catalog, "keyword:motorcycle keyword:travel"));
    }

    [Fact]
    public async Task Rating_filters_work()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);

        await catalog.UpsertAsync([
            Entry("/one.jpg", rating: 1),
            Entry("/four.jpg", rating: 4),
            Entry("/five.jpg", rating: 5),
            Entry("/none.jpg")
        ]);

        var atLeastFour = await SearchPathsAsync(catalog, "rating:>=4");

        Assert.Equal(["/five.jpg", "/four.jpg"], atLeastFour.Order().ToArray());

        // An unrated file must not satisfy a rating filter.
        Assert.DoesNotContain("/none.jpg", atLeastFour);
    }

    [Fact]
    public async Task Media_type_filters_work()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);

        await catalog.UpsertAsync([
            Entry("/a.jpg"),
            Entry("/b.mp4", MediaType.Video)
        ]);

        Assert.Equal(["/b.mp4"], await SearchPathsAsync(catalog, "type:video"));
        Assert.Equal(["/a.jpg"], await SearchPathsAsync(catalog, "type:image"));
    }

    [Fact]
    public async Task Camera_and_lens_filters_match_partially()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);

        await catalog.UpsertAsync([
            Entry("/a.jpg", camera: "Sony A7 IV", lens: "FE 24-70mm F2.8"),
            Entry("/b.jpg", camera: "Canon EOS R5", lens: "RF 100-500mm")
        ]);

        Assert.Equal(["/a.jpg"], await SearchPathsAsync(catalog, "camera:Sony"));
        Assert.Equal(["/b.jpg"], await SearchPathsAsync(catalog, "lens:\"RF 100-500\""));
    }

    [Fact]
    public async Task Date_filters_work()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);

        await catalog.UpsertAsync([
            Entry("/old.jpg", captureDate: new DateTimeOffset(2019, 5, 1, 0, 0, 0, TimeSpan.Zero)),
            Entry("/new.jpg", captureDate: new DateTimeOffset(2024, 5, 1, 0, 0, 0, TimeSpan.Zero))
        ]);

        Assert.Equal(["/new.jpg"], await SearchPathsAsync(catalog, "date:>=2024-01-01"));
        Assert.Equal(["/old.jpg"], await SearchPathsAsync(catalog, "date:<2020-01-01"));
    }

    [Fact]
    public async Task Filters_combine_with_and_semantics()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);

        await catalog.UpsertAsync([
            Entry("/hit.mp4", MediaType.Video, rating: 5, keywords: ["motorcycle"]),
            Entry("/wrongtype.jpg", MediaType.Image, rating: 5, keywords: ["motorcycle"]),
            Entry("/wrongrating.mp4", MediaType.Video, rating: 2, keywords: ["motorcycle"]),
            Entry("/wrongkeyword.mp4", MediaType.Video, rating: 5, keywords: ["travel"])
        ]);

        Assert.Equal(
            ["/hit.mp4"],
            await SearchPathsAsync(catalog, "rating:>=4 AND keyword:motorcycle AND type:video"));
    }

    [Fact]
    public async Task Removed_keywords_stop_matching_after_a_re_index()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);

        await catalog.UpsertAsync([Entry("/a.jpg", keywords: ["wildlife", "namibia"])]);
        await catalog.UpsertAsync([Entry("/a.jpg", keywords: ["wildlife"])]);

        Assert.Empty(await SearchPathsAsync(catalog, "keyword:namibia"));
        Assert.Single(await SearchPathsAsync(catalog, "keyword:wildlife"));
    }

    [Fact]
    public async Task Entries_for_deleted_files_can_be_pruned()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);

        var present = Path.Combine(temp.Path, "present.jpg");
        await File.WriteAllTextAsync(present, "x");

        await catalog.UpsertAsync([Entry(present), Entry(Path.Combine(temp.Path, "gone.jpg"))]);

        var removed = await catalog.RemoveMissingAsync();

        Assert.Equal(1, removed);
        Assert.Equal(1, (await catalog.GetStatisticsAsync()).FileCount);
    }

    [Fact]
    public async Task Clearing_empties_the_catalog()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);
        await catalog.UpsertAsync([Entry("/a.jpg", title: "Something", keywords: ["kw"])]);

        await catalog.ClearAsync();

        Assert.Equal(0, (await catalog.GetStatisticsAsync()).FileCount);
        Assert.Empty(await SearchPathsAsync(catalog, "Something"));
    }

    [Fact]
    public async Task The_catalog_survives_being_reopened()
    {
        using var temp = new TempFolder();

        await Create(temp).UpsertAsync([Entry("/a.jpg", title: "Persisted")]);

        // A new instance stands in for the next launch.
        Assert.Single(await Create(temp).SearchAsync(SearchQueryParser.Parse("Persisted")));
    }

    [Fact]
    public async Task A_query_that_matches_nothing_returns_nothing_rather_than_everything()
    {
        using var temp = new TempFolder();
        var catalog = Create(temp);
        await catalog.UpsertAsync([Entry("/a.jpg", title: "Lioness")]);

        Assert.Empty(await SearchPathsAsync(catalog, "keyword:doesnotexist"));
    }
}
