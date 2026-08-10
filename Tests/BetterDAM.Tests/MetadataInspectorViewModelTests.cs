using BetterDAM.Core.Interfaces;
using BetterDAM.Core.Models;
using BetterDAM.Core.Services;
using BetterDAM.UI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BetterDAM.Tests;

public class MetadataInspectorViewModelTests
{
    private sealed class StubMetadataProvider(MediaMetadata? result) : IMetadataProvider
    {
        public bool IsAvailable => true;

        public Task<MediaMetadata?> ReadAsync(MediaFile file, CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }

    private sealed class StubThumbnailService : IThumbnailService
    {
        public Task<byte[]?> GetThumbnailAsync(
            MediaFile file,
            int maxEdgePixels,
            ThumbnailPriority priority = ThumbnailPriority.Background,
            CancellationToken cancellationToken = default)
            => Task.FromResult<byte[]?>(null);
    }

    private const string FilePath = "/library/IMG001.jpg";

    private static MediaItemViewModel Item() => new(
        new MediaFile
        {
            FullPath = FilePath,
            FileName = "IMG001.jpg",
            MediaType = MediaType.Image,
            SizeBytes = 1,
            ModifiedUtc = DateTimeOffset.UnixEpoch,
            CreatedUtc = DateTimeOffset.UnixEpoch
        },
        new StubThumbnailService());

    /// <summary>Records what it was asked to write without touching the filesystem.</summary>
    private sealed class StubMetadataWriter : IMetadataWriter
    {
        public bool IsAvailable { get; set; } = true;

        public bool ShouldSucceed { get; set; } = true;

        public List<(MediaFile File, EditableMetadata Metadata)> Writes { get; } = [];

        public Task<SidecarWriteResult> WriteSidecarAsync(
            MediaFile file,
            EditableMetadata metadata,
            SidecarWriteOptions options,
            CancellationToken cancellationToken = default)
        {
            Writes.Add((file, metadata));

            return Task.FromResult(ShouldSucceed
                ? new SidecarWriteResult(file.FullPath, true, file.FullPath + ".xmp")
                : SidecarWriteResult.Failed(file.FullPath, "stub failure"));
        }
    }

    private static (MetadataInspectorViewModel Inspector, PendingChangeStore Store, StubMetadataWriter Writer)
        Create(MediaMetadata? metadata)
    {
        var store = new PendingChangeStore();
        var writer = new StubMetadataWriter();
        var inspector = new MetadataInspectorViewModel(
            new StubMetadataProvider(metadata),
            writer,
            store,
            NullLogger<MetadataInspectorViewModel>.Instance);

        return (inspector, store, writer);
    }

    [Fact]
    public async Task Loads_existing_metadata_into_the_fields()
    {
        var metadata = new MediaMetadata
        {
            Embedded = new EditableMetadata
            {
                Title = "Lioness at dawn",
                Rating = 4,
                Keywords = ["wildlife", "Namibia"]
            }
        };

        var (inspector, store, _) = Create(metadata);
        await inspector.LoadAsync(Item());

        Assert.Equal("Lioness at dawn", inspector.Title);
        Assert.Equal(4, inspector.Rating);
        Assert.Equal(["wildlife", "Namibia"], inspector.Keywords.ToArray());

        // Populating the form is not an edit.
        Assert.Equal(0, store.Count);
        Assert.False(inspector.HasPendingChanges);
    }

    [Fact]
    public async Task Setting_a_rating_from_a_string_parameter_works()
    {
        var (inspector, _, _) = Create(MediaMetadata.Empty);
        await inspector.LoadAsync(Item());

        // XAML passes CommandParameter="4" as a string; an int-typed command would silently no-op.
        inspector.SetRatingCommand.Execute("4");

        Assert.Equal(4, inspector.Rating);
    }

    [Fact]
    public async Task Clicking_the_current_rating_clears_it()
    {
        var (inspector, _, _) = Create(MediaMetadata.Empty);
        await inspector.LoadAsync(Item());

        inspector.SetRatingCommand.Execute("3");
        inspector.SetRatingCommand.Execute("3");

        Assert.Equal(0, inspector.Rating);
    }

    [Fact]
    public async Task A_non_numeric_rating_parameter_is_ignored()
    {
        var (inspector, _, _) = Create(MediaMetadata.Empty);
        await inspector.LoadAsync(Item());

        inspector.SetRatingCommand.Execute("not a number");

        Assert.Equal(0, inspector.Rating);
    }

    [Fact]
    public async Task Editing_a_field_records_a_pending_change()
    {
        var (inspector, store, _) = Create(MediaMetadata.Empty);
        var item = Item();
        await inspector.LoadAsync(item);

        inspector.Title = "New title";

        Assert.True(store.HasChanges(FilePath));
        Assert.Equal("New title", store.GetEdited(FilePath)!.Title);
        Assert.True(inspector.HasPendingChanges);
        Assert.True(item.HasPendingChanges);
    }

    [Fact]
    public async Task Editing_back_to_the_original_drops_the_pending_change()
    {
        var metadata = new MediaMetadata { Embedded = new EditableMetadata { Title = "Original" } };
        var (inspector, store, _) = Create(metadata);
        await inspector.LoadAsync(Item());

        inspector.Title = "Changed";
        inspector.Title = "Original";

        Assert.False(store.HasChanges(FilePath));
        Assert.False(inspector.HasPendingChanges);
    }

    [Fact]
    public async Task Adding_a_keyword_splits_on_commas()
    {
        var (inspector, _, _) = Create(MediaMetadata.Empty);
        await inspector.LoadAsync(Item());

        inspector.NewKeyword = "wildlife, Namibia, lioness";
        inspector.AddKeywordCommand.Execute(null);

        Assert.Equal(["wildlife", "Namibia", "lioness"], inspector.Keywords.ToArray());
        Assert.Equal(string.Empty, inspector.NewKeyword);
    }

    [Fact]
    public async Task Duplicate_keywords_are_ignored_case_insensitively()
    {
        var (inspector, _, _) = Create(MediaMetadata.Empty);
        await inspector.LoadAsync(Item());

        inspector.NewKeyword = "wildlife";
        inspector.AddKeywordCommand.Execute(null);
        inspector.NewKeyword = "WILDLIFE";
        inspector.AddKeywordCommand.Execute(null);

        Assert.Equal(["wildlife"], inspector.Keywords.ToArray());
    }

    [Fact]
    public async Task Removing_a_keyword_records_a_pending_change()
    {
        var metadata = new MediaMetadata { Embedded = new EditableMetadata { Keywords = ["a", "b"] } };
        var (inspector, store, _) = Create(metadata);
        await inspector.LoadAsync(Item());

        inspector.RemoveKeywordCommand.Execute("a");

        Assert.Equal(["b"], inspector.Keywords.ToArray());
        Assert.True(store.HasChanges(FilePath));
    }

    [Fact]
    public async Task Revert_restores_the_values_from_disk()
    {
        var metadata = new MediaMetadata { Embedded = new EditableMetadata { Title = "Original", Rating = 2 } };
        var (inspector, store, _) = Create(metadata);
        var item = Item();
        await inspector.LoadAsync(item);

        inspector.Title = "Changed";
        inspector.SetRatingCommand.Execute("5");
        Assert.True(store.HasChanges(FilePath));

        inspector.RevertChangesCommand.Execute(null);

        Assert.Equal("Original", inspector.Title);
        Assert.Equal(2, inspector.Rating);
        Assert.False(store.HasChanges(FilePath));
        Assert.False(item.HasPendingChanges);
    }

    [Fact]
    public async Task Reselecting_a_file_shows_its_pending_edit_rather_than_the_value_on_disk()
    {
        var metadata = new MediaMetadata { Embedded = new EditableMetadata { Title = "On disk" } };
        var (inspector, _, _) = Create(metadata);

        await inspector.LoadAsync(Item());
        inspector.Title = "Edited but unsaved";

        // Select something else, then come back.
        await inspector.LoadAsync(null);
        await inspector.LoadAsync(Item());

        Assert.Equal("Edited but unsaved", inspector.Title);
        Assert.True(inspector.HasPendingChanges);
    }

    [Fact]
    public async Task Writing_the_sidecar_clears_the_pending_change()
    {
        var (inspector, store, writer) = Create(MediaMetadata.Empty);
        var item = Item();
        await inspector.LoadAsync(item);

        inspector.Title = "New title";
        Assert.True(store.HasChanges(FilePath));

        await inspector.WriteSidecarCommand.ExecuteAsync(null);

        Assert.Equal("New title", Assert.Single(writer.Writes).Metadata.Title);
        Assert.False(store.HasChanges(FilePath));
        Assert.False(item.HasPendingChanges);
        Assert.False(inspector.WriteFailed);
    }

    [Fact]
    public async Task A_failed_write_keeps_the_pending_change()
    {
        var (inspector, store, writer) = Create(MediaMetadata.Empty);
        writer.ShouldSucceed = false;
        await inspector.LoadAsync(Item());

        inspector.Title = "New title";
        await inspector.WriteSidecarCommand.ExecuteAsync(null);

        // Losing the user's edit because a write failed would be the worst possible outcome.
        Assert.True(store.HasChanges(FilePath));
        Assert.True(inspector.WriteFailed);
        Assert.Equal("stub failure", inspector.WriteStatus);
    }

    [Fact]
    public async Task Conflicts_are_surfaced_when_the_layers_disagree()
    {
        var metadata = new MediaMetadata
        {
            Embedded = new EditableMetadata { Title = "Embedded", Rating = 1 },
            Sidecar = new EditableMetadata { Title = "Sidecar", Rating = 5 },
            SidecarPath = "/library/IMG001.xmp"
        };

        var (inspector, _, _) = Create(metadata);
        var item = Item();
        await inspector.LoadAsync(item);

        Assert.True(inspector.HasConflicts);
        Assert.Equal(2, inspector.Conflicts.Count);
        Assert.True(item.HasConflicts);
    }

    [Fact]
    public async Task Resolving_a_conflict_records_a_pending_change_without_writing()
    {
        var metadata = new MediaMetadata
        {
            Embedded = new EditableMetadata { Title = "Embedded", Rating = 1 },
            Sidecar = new EditableMetadata { Title = "Sidecar", Rating = 5 },
            SidecarPath = "/library/IMG001.xmp"
        };

        var (inspector, store, writer) = Create(metadata);
        await inspector.LoadAsync(Item());

        inspector.ResolveConflictsCommand.Execute("KeepEmbedded");

        Assert.Equal("Embedded", inspector.Title);
        Assert.Equal(1, inspector.Rating);
        Assert.False(inspector.HasConflicts);

        // Resolving is a decision, not a commit.
        Assert.True(store.HasChanges(FilePath));
        Assert.Empty(writer.Writes);
    }

    [Fact]
    public async Task An_unrecognised_resolution_is_ignored()
    {
        var metadata = new MediaMetadata
        {
            Embedded = new EditableMetadata { Title = "Embedded" },
            Sidecar = new EditableMetadata { Title = "Sidecar" },
            SidecarPath = "/library/IMG001.xmp"
        };

        var (inspector, _, _) = Create(metadata);
        await inspector.LoadAsync(Item());

        inspector.ResolveConflictsCommand.Execute("nonsense");

        Assert.True(inspector.HasConflicts);
    }

    private static MediaItemViewModel VideoItem() => new(
        new MediaFile
        {
            FullPath = "/library/CLIP.mp4",
            FileName = "CLIP.mp4",
            MediaType = MediaType.Video,
            SizeBytes = 1,
            ModifiedUtc = DateTimeOffset.UnixEpoch,
            CreatedUtc = DateTimeOffset.UnixEpoch
        },
        new StubThumbnailService());

    [Fact]
    public async Task Selecting_an_image_moves_off_the_hidden_video_tab()
    {
        var (inspector, _, _) = Create(MediaMetadata.Empty);

        await inspector.LoadAsync(VideoItem());
        inspector.SelectedTabIndex = 2; // Video

        await inspector.LoadAsync(Item());

        // The Video tab is hidden for stills; leaving it selected shows empty fields under no tab.
        Assert.False(inspector.IsVideo);
        Assert.Equal(0, inspector.SelectedTabIndex);
    }

    [Fact]
    public async Task Selecting_an_image_keeps_a_still_relevant_tab()
    {
        var (inspector, _, _) = Create(MediaMetadata.Empty);

        await inspector.LoadAsync(VideoItem());
        inspector.SelectedTabIndex = 1; // Camera

        await inspector.LoadAsync(Item());

        Assert.Equal(1, inspector.SelectedTabIndex);
    }

    [Fact]
    public async Task Deselecting_clears_the_panel()
    {
        var metadata = new MediaMetadata { Embedded = new EditableMetadata { Title = "Something" } };
        var (inspector, _, _) = Create(metadata);

        await inspector.LoadAsync(Item());
        await inspector.LoadAsync(null);

        Assert.False(inspector.HasItem);
        Assert.Null(inspector.Title);
        Assert.Empty(inspector.Keywords);
    }
}
