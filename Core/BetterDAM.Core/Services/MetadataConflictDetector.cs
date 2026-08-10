using System.Collections.Immutable;
using BetterDAM.Core.Models;

namespace BetterDAM.Core.Services;

/// <summary>
/// Compares the embedded and sidecar layers field by field.
///
/// A conflict requires <b>both</b> sides to carry a value and for those values to differ. A field the
/// sidecar simply does not mention is not a conflict — that is the normal case for a sidecar that
/// only carries a rating.
/// </summary>
public static class MetadataConflictDetector
{
    public const string TitleField = "Title";
    public const string DescriptionField = "Description";
    public const string KeywordsField = "Keywords";
    public const string RatingField = "Rating";
    public const string LabelField = "Label";
    public const string CreatorField = "Creator";
    public const string CopyrightField = "Copyright";
    public const string HeadlineField = "Headline";

    public static ImmutableArray<MetadataConflict> Detect(MediaMetadata metadata)
    {
        if (metadata.Sidecar is not { } sidecar)
        {
            return [];
        }

        var embedded = metadata.Embedded;
        var conflicts = ImmutableArray.CreateBuilder<MetadataConflict>();

        AddIfConflicting(TitleField, embedded.Title, sidecar.Title);
        AddIfConflicting(DescriptionField, embedded.Description, sidecar.Description);
        AddIfConflicting(HeadlineField, embedded.Headline, sidecar.Headline);
        AddIfConflicting(LabelField, embedded.Label, sidecar.Label);
        AddIfConflicting(CreatorField, embedded.Creator, sidecar.Creator);
        AddIfConflicting(CopyrightField, embedded.Copyright, sidecar.Copyright);
        AddIfConflicting(
            RatingField,
            embedded.Rating?.ToString(),
            sidecar.Rating?.ToString());

        // Keyword order is not meaningful, so only membership counts.
        if (!embedded.Keywords.IsDefaultOrEmpty && !sidecar.Keywords.IsDefaultOrEmpty &&
            !embedded.Keywords.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(sidecar.Keywords.OrderBy(k => k, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
        {
            conflicts.Add(new MetadataConflict(
                KeywordsField,
                string.Join(", ", embedded.Keywords),
                string.Join(", ", sidecar.Keywords)));
        }

        return conflicts.ToImmutable();

        void AddIfConflicting(string field, string? embeddedValue, string? sidecarValue)
        {
            if (embeddedValue is null || sidecarValue is null || embeddedValue == sidecarValue)
            {
                return;
            }

            conflicts.Add(new MetadataConflict(field, embeddedValue, sidecarValue));
        }
    }

    /// <summary>
    /// Produces the metadata that settles the conflicts, ready to be recorded as a pending edit.
    /// </summary>
    public static EditableMetadata Resolve(MediaMetadata metadata, ConflictResolution resolution)
    {
        var embedded = metadata.Embedded;
        var sidecar = metadata.Sidecar;

        return resolution switch
        {
            // Only the conflicting side is replaced; fields the sidecar uniquely supplies survive.
            ConflictResolution.KeepEmbedded => (sidecar ?? EditableMetadata.Empty).MergeWith(embedded),
            ConflictResolution.KeepSidecar => metadata.Effective,
            ConflictResolution.Merge => metadata.Effective with
            {
                Keywords = embedded.Keywords
                    .Concat(sidecar?.Keywords ?? [])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToImmutableArray()
            },
            _ => metadata.Effective
        };
    }
}
