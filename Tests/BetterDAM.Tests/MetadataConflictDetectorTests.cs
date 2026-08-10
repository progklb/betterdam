using BetterDAM.Core.Models;
using BetterDAM.Core.Services;
using Xunit;

namespace BetterDAM.Tests;

public class MetadataConflictDetectorTests
{
    private static MediaMetadata WithLayers(EditableMetadata embedded, EditableMetadata? sidecar)
        => new()
        {
            Embedded = embedded,
            Sidecar = sidecar,
            SidecarPath = sidecar is null ? null : "/library/IMG001.xmp"
        };

    [Fact]
    public void No_sidecar_means_no_conflicts()
    {
        var metadata = WithLayers(new EditableMetadata { Title = "Embedded" }, null);

        Assert.Empty(MetadataConflictDetector.Detect(metadata));
    }

    [Fact]
    public void A_field_the_sidecar_does_not_mention_is_not_a_conflict()
    {
        // The common case: a sidecar carrying only a rating must not be reported as conflicting
        // with every field the media file happens to have.
        var metadata = WithLayers(
            new EditableMetadata { Title = "Embedded", Creator = "Kevin" },
            new EditableMetadata { Rating = 5 });

        Assert.Empty(MetadataConflictDetector.Detect(metadata));
    }

    [Fact]
    public void Identical_values_are_not_a_conflict()
    {
        var metadata = WithLayers(
            new EditableMetadata { Title = "Same", Rating = 3 },
            new EditableMetadata { Title = "Same", Rating = 3 });

        Assert.Empty(MetadataConflictDetector.Detect(metadata));
    }

    [Fact]
    public void Differing_values_on_both_sides_conflict()
    {
        var metadata = WithLayers(
            new EditableMetadata { Title = "Embedded title", Rating = 1 },
            new EditableMetadata { Title = "Sidecar title", Rating = 5 });

        var conflicts = MetadataConflictDetector.Detect(metadata);

        Assert.Equal(2, conflicts.Length);

        var title = conflicts.Single(c => c.Field == MetadataConflictDetector.TitleField);
        Assert.Equal("Embedded title", title.EmbeddedValue);
        Assert.Equal("Sidecar title", title.SidecarValue);

        var rating = conflicts.Single(c => c.Field == MetadataConflictDetector.RatingField);
        Assert.Equal("1", rating.EmbeddedValue);
        Assert.Equal("5", rating.SidecarValue);
    }

    [Fact]
    public void Keyword_order_alone_is_not_a_conflict()
    {
        var metadata = WithLayers(
            new EditableMetadata { Keywords = ["a", "b"] },
            new EditableMetadata { Keywords = ["b", "a"] });

        Assert.Empty(MetadataConflictDetector.Detect(metadata));
    }

    [Fact]
    public void Different_keyword_membership_conflicts()
    {
        var metadata = WithLayers(
            new EditableMetadata { Keywords = ["a", "b"] },
            new EditableMetadata { Keywords = ["a", "c"] });

        var conflict = Assert.Single(MetadataConflictDetector.Detect(metadata));
        Assert.Equal(MetadataConflictDetector.KeywordsField, conflict.Field);
    }

    [Fact]
    public void Keep_embedded_takes_the_embedded_value_but_keeps_sidecar_only_fields()
    {
        var metadata = WithLayers(
            new EditableMetadata { Title = "Embedded title", Rating = 1 },
            new EditableMetadata { Title = "Sidecar title", Rating = 5, Label = "Red" });

        var resolved = MetadataConflictDetector.Resolve(metadata, ConflictResolution.KeepEmbedded);

        Assert.Equal("Embedded title", resolved.Title);
        Assert.Equal(1, resolved.Rating);

        // The sidecar was the only source of a label; resolving a conflict must not discard it.
        Assert.Equal("Red", resolved.Label);
    }

    [Fact]
    public void Keep_sidecar_takes_the_sidecar_value()
    {
        var metadata = WithLayers(
            new EditableMetadata { Title = "Embedded title", Rating = 1 },
            new EditableMetadata { Title = "Sidecar title", Rating = 5 });

        var resolved = MetadataConflictDetector.Resolve(metadata, ConflictResolution.KeepSidecar);

        Assert.Equal("Sidecar title", resolved.Title);
        Assert.Equal(5, resolved.Rating);
    }

    [Fact]
    public void Merge_unions_the_keywords()
    {
        var metadata = WithLayers(
            new EditableMetadata { Keywords = ["a", "b"], Title = "Embedded" },
            new EditableMetadata { Keywords = ["b", "c"], Title = "Sidecar" });

        var resolved = MetadataConflictDetector.Resolve(metadata, ConflictResolution.Merge);

        Assert.Equal(["a", "b", "c"], resolved.Keywords.OrderBy(k => k).ToArray());

        // Single-valued fields cannot be merged, so the sidecar wins.
        Assert.Equal("Sidecar", resolved.Title);
    }
}
