using System.Collections.Immutable;

namespace BetterDAM.Core.Models;

public enum ComparisonOperator
{
    Equal,
    GreaterThanOrEqual,
    LessThanOrEqual,
    GreaterThan,
    LessThan
}

public sealed record RatingFilter(ComparisonOperator Operator, int Value);

public sealed record DateFilter(ComparisonOperator Operator, DateTimeOffset Value);

/// <summary>
/// A parsed search. Everything here is structured — the parser turns the user's typed string into
/// this, and the catalog turns this into SQL. Nothing downstream re-parses text.
/// </summary>
public sealed record SearchQuery
{
    public static readonly SearchQuery Empty = new();

    /// <summary>Bare words, matched across title, description, headline, keywords and creator.</summary>
    public ImmutableArray<string> FreeText { get; init; } = [];

    /// <summary>Exact keyword matches — <c>keyword:motorcycle</c>.</summary>
    public ImmutableArray<string> Keywords { get; init; } = [];

    public ImmutableArray<string> Cameras { get; init; } = [];

    public ImmutableArray<string> Lenses { get; init; } = [];

    public MediaType? MediaType { get; init; }

    public RatingFilter? Rating { get; init; }

    public DateFilter? CaptureDate { get; init; }

    /// <summary>Anything the parser could not make sense of, so the UI can say so rather than silently ignoring it.</summary>
    public ImmutableArray<string> UnrecognisedTerms { get; init; } = [];

    public bool IsEmpty =>
        FreeText.IsDefaultOrEmpty && Keywords.IsDefaultOrEmpty && Cameras.IsDefaultOrEmpty &&
        Lenses.IsDefaultOrEmpty && MediaType is null && Rating is null && CaptureDate is null;
}

/// <summary>One row of a search result — enough to render a tile without touching the disk.</summary>
public sealed record SearchHit(
    string FullPath,
    string FileName,
    MediaType MediaType,
    long SizeBytes,
    DateTimeOffset ModifiedUtc,
    DateTimeOffset CreatedUtc,
    int? Rating,
    string? Title)
{
    public MediaFile ToMediaFile() => new()
    {
        FullPath = FullPath,
        FileName = FileName,
        MediaType = MediaType,
        SizeBytes = SizeBytes,
        ModifiedUtc = ModifiedUtc,
        CreatedUtc = CreatedUtc
    };
}
