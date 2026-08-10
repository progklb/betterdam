using System.Collections.Immutable;

namespace BetterDAM.Core.Models;

/// <summary>
/// The user-editable metadata the application presents identically for every media format.
/// A null property means "not present", which is deliberately distinct from an empty string.
/// </summary>
public sealed record EditableMetadata
{
    public static readonly EditableMetadata Empty = new();

    public string? Title { get; init; }

    public string? Description { get; init; }

    public ImmutableArray<string> Keywords { get; init; } = [];

    /// <summary>XMP rating, 0–5. Null when the file carries no rating at all.</summary>
    public int? Rating { get; init; }

    public string? Label { get; init; }

    public string? Creator { get; init; }

    public string? Copyright { get; init; }

    public string? Headline { get; init; }

    public bool IsEmpty =>
        Title is null && Description is null && Keywords.IsDefaultOrEmpty && Rating is null &&
        Label is null && Creator is null && Copyright is null && Headline is null;

    /// <summary>
    /// Value comparison. The compiler-generated record equality cannot be used here because
    /// <see cref="ImmutableArray{T}"/> compares by underlying-array reference, so two identical
    /// keyword lists built separately would wrongly look different.
    /// </summary>
    public bool ValueEquals(EditableMetadata? other)
    {
        if (other is null)
        {
            return false;
        }

        return Title == other.Title
            && Description == other.Description
            && Rating == other.Rating
            && Label == other.Label
            && Creator == other.Creator
            && Copyright == other.Copyright
            && Headline == other.Headline
            && Keywords.AsSpan().SequenceEqual(other.Keywords.AsSpan());
    }

    /// <summary>
    /// Overlays <paramref name="overlay"/> on top of this instance. Only properties the overlay
    /// actually supplies win, so a sidecar that carries just a rating does not blank out a title
    /// that came from the embedded metadata.
    /// </summary>
    public EditableMetadata MergeWith(EditableMetadata? overlay)
    {
        if (overlay is null)
        {
            return this;
        }

        return new EditableMetadata
        {
            Title = overlay.Title ?? Title,
            Description = overlay.Description ?? Description,
            Keywords = overlay.Keywords.IsDefaultOrEmpty ? Keywords : overlay.Keywords,
            Rating = overlay.Rating ?? Rating,
            Label = overlay.Label ?? Label,
            Creator = overlay.Creator ?? Creator,
            Copyright = overlay.Copyright ?? Copyright,
            Headline = overlay.Headline ?? Headline
        };
    }
}
