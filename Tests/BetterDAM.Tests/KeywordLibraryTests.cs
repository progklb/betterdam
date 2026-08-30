using BetterDAM.Core.Interfaces;
using BetterDAM.Core.Models;
using BetterDAM.UI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BetterDAM.Tests;

public class KeywordLibraryTests
{
    // ---- Building from flat keywords -------------------------------------------------------------

    [Fact]
    public void Flat_keywords_become_roots()
    {
        var library = KeywordLibrary.FromFlat(["animal", "plant"]);

        Assert.Equal(2, library.Roots.Length);
        Assert.All(library.Roots, root => Assert.False(root.HasChildren));
    }

    /// <summary>
    /// Lightroom writes hierarchical keywords with a pipe. Filing them under the right parent is what
    /// makes importing an existing library produce a structure rather than a flat wall of names.
    /// </summary>
    [Theory]
    [InlineData("Subject|animal")]
    [InlineData("Subject/animal")]
    public void A_path_is_filed_under_its_parent(string keyword)
    {
        var library = KeywordLibrary.FromFlat([keyword]);

        var root = Assert.Single(library.Roots);
        Assert.Equal("Subject", root.Name);

        var child = Assert.Single(root.Children);
        Assert.Equal("animal", child.Name);
    }

    [Fact]
    public void Siblings_share_one_parent()
    {
        var library = KeywordLibrary.FromFlat(["Subject|animal", "Subject|plant", "Mood|warm"]);

        Assert.Equal(2, library.Roots.Length);
        Assert.Equal(2, library.Roots.Single(r => r.Name == "Subject").Children.Length);
        Assert.Single(library.Roots.Single(r => r.Name == "Mood").Children);
    }

    [Fact]
    public void Nesting_goes_as_deep_as_the_path()
    {
        var library = KeywordLibrary.FromFlat(["Subject|animal|mammal|elephant"]);

        Assert.Equal(4, library.Flatten().Count());
        Assert.Equal("elephant", library.Flatten().Last().Name);
    }

    /// <summary>Case differences are the same keyword; a library with both would be a mess to tick.</summary>
    [Fact]
    public void The_same_keyword_in_another_case_is_not_duplicated()
    {
        var library = KeywordLibrary.FromFlat(["Subject|Animal", "subject|animal"]);

        var root = Assert.Single(library.Roots);
        Assert.Single(root.Children);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("||")]
    public void Empty_input_produces_nothing(string keyword)
        => Assert.True(KeywordLibrary.FromFlat([keyword]).IsEmpty);

    [Fact]
    public void Surrounding_space_is_trimmed()
    {
        var library = KeywordLibrary.FromFlat([" Subject | animal "]);

        Assert.Equal("Subject", library.Roots.Single().Name);
        Assert.Equal("animal", library.Roots.Single().Children.Single().Name);
    }

    // ---- Round tripping and merging ---------------------------------------------------------------

    /// <summary>
    /// Parents are emitted as paths in their own right. A group is a keyword too, and dropping it
    /// here would quietly delete it the next time anything merged.
    /// </summary>
    [Fact]
    public void Paths_include_parents_as_well_as_leaves()
    {
        var library = KeywordLibrary.FromFlat(["Subject|animal"]);

        Assert.Equal(["Subject", "Subject|animal"], library.ToPaths().Order().ToArray());
    }

    [Fact]
    public void A_library_survives_a_round_trip_through_paths()
    {
        var original = KeywordLibrary.FromFlat(["Subject|animal", "Subject|plant", "Mood|warm", "loose"]);

        var rebuilt = KeywordLibrary.FromFlat(original.ToPaths());

        Assert.Equal(original.ToPaths().Order(), rebuilt.ToPaths().Order());
    }

    /// <summary>
    /// The point of merging: someone who has arranged their groups by hand and then imports should
    /// gain what they were missing, not have their arrangement flattened.
    /// </summary>
    [Fact]
    public void Merging_keeps_the_existing_arrangement()
    {
        var existing = KeywordLibrary.FromFlat(["Subject|animal", "Mood|warm"]);

        var merged = existing.MergedWith(["Subject|plant", "golden hour"]);

        Assert.Equal(2, merged.Roots.Single(r => r.Name == "Subject").Children.Length);
        Assert.Single(merged.Roots.Single(r => r.Name == "Mood").Children);
        Assert.Contains(merged.Roots, r => r.Name == "golden hour");
    }

    [Fact]
    public void Merging_the_same_keywords_changes_nothing()
    {
        var existing = KeywordLibrary.FromFlat(["Subject|animal", "Mood|warm"]);

        var merged = existing.MergedWith(["Subject|animal"]);

        Assert.Equal(existing.ToPaths().Order(), merged.ToPaths().Order());
    }

    /// <summary>A flat keyword matching an existing group must not become a second copy of it.</summary>
    [Fact]
    public void Merging_a_flat_name_that_already_exists_as_a_group_does_not_duplicate_it()
    {
        var existing = KeywordLibrary.FromFlat(["Subject|animal"]);

        var merged = existing.MergedWith(["Subject"]);

        Assert.Single(merged.Roots);
        Assert.Single(merged.Roots.Single().Children);
    }

    // ---- Lookups ----------------------------------------------------------------------------------

    [Fact]
    public void All_names_covers_every_level()
    {
        var names = KeywordLibrary.FromFlat(["Subject|animal", "Mood|warm"]).AllNames();

        Assert.Equal(4, names.Count);
        Assert.Contains("Subject", names);
        Assert.Contains("animal", names);
    }

    [Fact]
    public void All_names_ignores_case_when_asked()
        => Assert.Contains("ANIMAL", KeywordLibrary.FromFlat(["animal"]).AllNames());

    [Fact]
    public void An_empty_library_reports_itself_as_empty()
    {
        Assert.True(KeywordLibrary.Empty.IsEmpty);
        Assert.Equal(0, KeywordLibrary.Empty.Count);
        Assert.Empty(KeywordLibrary.Empty.ToPaths());
    }

    [Fact]
    public void Count_includes_every_level()
        => Assert.Equal(4, KeywordLibrary.FromFlat(["Subject|animal", "Mood|warm"]).Count);
}

/// <summary>
/// The contract that the grouping never reaches the files: a keyword is written as its own bare name,
/// so what is tagged stays readable by Bridge, Lightroom or exiftool.
/// </summary>
public class KeywordIdentityTests
{
    [Fact]
    public void A_keyword_is_its_own_name_regardless_of_where_it_is_filed()
    {
        var library = KeywordLibrary.FromFlat(["Shot type|wide", "Subject|animal"]);

        var names = library.AllNames();

        Assert.Contains("wide", names);
        Assert.Contains("animal", names);

        // Never the path.
        Assert.DoesNotContain("Shot type|wide", names);
    }

    /// <summary>
    /// The same word in two groups is one keyword. Files carry names, so there is nothing to tell the
    /// two apart — the picker must tick both, and this pins the fact it follows from.
    /// </summary>
    [Fact]
    public void The_same_name_in_two_groups_is_one_keyword()
    {
        var library = KeywordLibrary.FromFlat(["Shot type|wide", "Landscape|wide"]);

        Assert.Equal(2, library.Roots.Length);
        Assert.Equal(2, library.Flatten().Count(node => node.Name == "wide"));

        // One keyword, though, because names are the identity.
        Assert.Single(library.AllNames().Where(name => name == "wide"));
    }

    [Fact]
    public void Groups_are_offered_as_keywords_too()
    {
        var names = KeywordLibrary.FromFlat(["Mood|warm"]).AllNames();

        Assert.Contains("Mood", names);
        Assert.Contains("warm", names);
    }
}

/// <summary>Refiling a keyword under another one, which is how the tree is arranged.</summary>
public class KeywordMoveTests
{
    private static KeywordLibraryEditorViewModel Editor(params string[] keywords)
    {
        var service = new StubLibraryService(KeywordLibrary.FromFlat(keywords));
        return new KeywordLibraryEditorViewModel(service, new StubCatalog(), NullLogger<KeywordLibraryEditorViewModel>.Instance);
    }

    private static KeywordNodeViewModel Find(KeywordLibraryEditorViewModel editor, string name)
        => Flatten(editor.Roots).First(node => node.Name == name);

    private static IEnumerable<KeywordNodeViewModel> Flatten(IEnumerable<KeywordNodeViewModel> nodes)
        => nodes.SelectMany(node => new[] { node }.Concat(Flatten(node.Children)));

    [Fact]
    public void A_root_can_be_filed_under_another_root()
    {
        var editor = Editor("wide", "Shot type");
        editor.Selected = Find(editor, "wide");

        var target = editor.MoveTargets.Single(t => t.Label.Trim() == "Shot type");
        editor.MoveSelectedCommand.Execute(target);

        Assert.Single(editor.Roots);
        Assert.Equal("wide", editor.Roots.Single().Children.Single().Name);
    }

    [Fact]
    public void A_child_can_be_promoted_to_the_top_level()
    {
        var editor = Editor("Shot type|wide");
        editor.Selected = Find(editor, "wide");

        editor.MoveSelectedCommand.Execute(editor.MoveTargets.Single(t => t.Label == "Top level"));

        Assert.Equal(2, editor.Roots.Count);
        Assert.Empty(Find(editor, "Shot type").Children);
    }

    /// <summary>
    /// The move that would lose the subtree: filing a group under one of its own children detaches it
    /// from the roots entirely. It must not even be offered.
    /// </summary>
    [Fact]
    public void A_keyword_is_never_offered_its_own_subtree()
    {
        var editor = Editor("Subject|animal|mammal");
        editor.Selected = Find(editor, "Subject");

        var labels = editor.MoveTargets.Select(t => t.Label.Trim()).ToList();

        Assert.DoesNotContain("Subject", labels);
        Assert.DoesNotContain("animal", labels);
        Assert.DoesNotContain("mammal", labels);
    }

    /// <summary>Offering the parent it is already under would be a no-op cluttering the list.</summary>
    [Fact]
    public void The_current_parent_is_not_offered()
    {
        var editor = Editor("Shot type|wide", "Mood|warm");
        editor.Selected = Find(editor, "wide");

        Assert.DoesNotContain("Shot type", editor.MoveTargets.Select(t => t.Label.Trim()));
        Assert.Contains("Mood", editor.MoveTargets.Select(t => t.Label.Trim()));
    }

    [Fact]
    public void A_lone_root_has_nowhere_to_go()
    {
        var editor = Editor("wide");
        editor.Selected = Find(editor, "wide");

        Assert.Empty(editor.MoveTargets);
        Assert.False(editor.CanMoveSelected);
    }

    [Fact]
    public void Moving_takes_the_children_with_it()
    {
        var editor = Editor("Subject|animal|mammal", "Mood");
        editor.Selected = Find(editor, "animal");

        editor.MoveSelectedCommand.Execute(editor.MoveTargets.Single(t => t.Label.Trim() == "Mood"));

        var moved = Find(editor, "Mood").Children.Single();
        Assert.Equal("animal", moved.Name);
        Assert.Equal("mammal", moved.Children.Single().Name);
    }

    private sealed class StubLibraryService(KeywordLibrary library) : IKeywordLibraryService
    {
        public KeywordLibrary Current { get; private set; } = library;

        public event EventHandler<KeywordLibrary>? Changed { add { } remove { } }

        public Task SaveAsync(KeywordLibrary library, CancellationToken cancellationToken = default)
        {
            Current = library;
            return Task.CompletedTask;
        }
    }

    private sealed class StubCatalog : ICatalog
    {
        public Task<CatalogStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CatalogStatistics.Empty);

        public Task<IReadOnlyDictionary<string, MediaMarks>> GetMarksAsync(string? rootPath = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, MediaMarks>>(new Dictionary<string, MediaMarks>());

        public Task<IReadOnlyList<LabelUsage>> GetLabelsAsync(string? rootPath = null, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<LabelUsage>>([]);

    public Task<IReadOnlyList<KeywordUsage>> GetKeywordsAsync(string? rootPath = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<KeywordUsage>>([]);

        public Task UpsertAsync(IReadOnlyList<CatalogEntry> entries, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyDictionary<string, IndexedStamp>> GetIndexedStampsAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, IndexedStamp>>(new Dictionary<string, IndexedStamp>());

        public Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, string? rootPath = null, int limit = 5000, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SearchHit>>([]);

        public Task<int> RemoveMissingAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

/// <summary>Alphabetical order, at every level and after every kind of change.</summary>
public class KeywordSortingTests
{
    private static KeywordLibraryEditorViewModel Editor(params string[] keywords)
        => new(
            new StubLibrary(KeywordLibrary.FromFlat(keywords)),
            new StubCatalogForSorting(),
            NullLogger<KeywordLibraryEditorViewModel>.Instance);

    private static KeywordNodeViewModel Find(KeywordLibraryEditorViewModel editor, string name)
        => Flatten(editor.Roots).First(node => node.Name == name);

    private static IEnumerable<KeywordNodeViewModel> Flatten(IEnumerable<KeywordNodeViewModel> nodes)
        => nodes.SelectMany(node => new[] { node }.Concat(Flatten(node.Children)));

    /// <summary>
    /// Import order is catalog usage counts, which reads as no order at all. Roots were the one level
    /// that was never sorted.
    /// </summary>
    [Fact]
    public void Roots_are_alphabetical_however_they_arrived()
    {
        var library = KeywordLibrary.FromFlat(["Wide", "Bush", "Calm"]);

        Assert.Equal(["Bush", "Calm", "Wide"], library.Roots.Select(r => r.Name).ToArray());
    }

    [Fact]
    public void Children_are_alphabetical_too()
    {
        var library = KeywordLibrary.FromFlat(["Shot type|wide", "Shot type|close", "Shot type|medium"]);

        Assert.Equal(
            ["close", "medium", "wide"],
            library.Roots.Single().Children.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void Order_ignores_case()
    {
        var library = KeywordLibrary.FromFlat(["banana", "Apple", "cherry"]);

        Assert.Equal(["Apple", "banana", "cherry"], library.Roots.Select(r => r.Name).ToArray());
    }

    [Fact]
    public void A_loaded_library_is_sorted_even_if_it_was_stored_out_of_order()
    {
        var editor = Editor("Wide", "Bush");

        Assert.Equal(["Bush", "Wide"], editor.Roots.Select(r => r.Name).ToArray());
    }

    [Fact]
    public void A_moved_keyword_lands_in_order()
    {
        var editor = Editor("Shot type|close", "Shot type|wide", "medium");
        editor.Selected = Find(editor, "medium");

        editor.MoveSelectedCommand.Execute(
            editor.MoveTargets.Single(t => t.Label.Trim() == "Shot type"));

        Assert.Equal(
            ["close", "medium", "wide"],
            Find(editor, "Shot type").Children.Select(c => c.Name).ToArray());
    }

    /// <summary>
    /// Sorting is entirely Move operations. If those were treated like ordinary adds and removals the
    /// node would be attached and then immediately detached, and every later edit to it would go
    /// unnoticed — including, silently, never being saved.
    /// </summary>
    [Fact]
    public void Sorting_leaves_the_tree_still_listening()
    {
        var editor = Editor("Wide", "Bush");

        var node = Find(editor, "Wide");

        Assert.NotNull(node.Owner);
        Assert.Same(editor, node.Owner);
    }

    [Fact]
    public void Renaming_then_committing_reorders_the_level()
    {
        var editor = Editor("Bush", "Calm", "Wide");

        var node = Find(editor, "Wide");
        node.Name = "Aardvark";
        editor.SortSiblingsOf(node);

        Assert.Equal(["Aardvark", "Bush", "Calm"], editor.Roots.Select(r => r.Name).ToArray());
    }

    [Fact]
    public void Move_targets_are_offered_in_order()
    {
        var editor = Editor("Wide", "Bush", "Calm");
        editor.Selected = Find(editor, "Wide");

        Assert.Equal(
            ["Bush", "Calm"],
            editor.MoveTargets.Select(t => t.Label.Trim()).ToArray());
    }

    private sealed class StubLibrary(KeywordLibrary library) : IKeywordLibraryService
    {
        public KeywordLibrary Current { get; private set; } = library;

        public event EventHandler<KeywordLibrary>? Changed { add { } remove { } }

        public Task SaveAsync(KeywordLibrary library, CancellationToken cancellationToken = default)
        {
            Current = library;
            return Task.CompletedTask;
        }
    }

    private sealed class StubCatalogForSorting : ICatalog
    {
        public Task<CatalogStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CatalogStatistics.Empty);

        public Task<IReadOnlyDictionary<string, MediaMarks>> GetMarksAsync(string? rootPath = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, MediaMarks>>(new Dictionary<string, MediaMarks>());

        public Task<IReadOnlyList<LabelUsage>> GetLabelsAsync(string? rootPath = null, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<LabelUsage>>([]);

    public Task<IReadOnlyList<KeywordUsage>> GetKeywordsAsync(string? rootPath = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<KeywordUsage>>([]);

        public Task UpsertAsync(IReadOnlyList<CatalogEntry> entries, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyDictionary<string, IndexedStamp>> GetIndexedStampsAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, IndexedStamp>>(new Dictionary<string, IndexedStamp>());

        public Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, string? rootPath = null, int limit = 5000, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SearchHit>>([]);

        public Task<int> RemoveMissingAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

/// <summary>
/// Which metadata fields the panel shows. Everyone uses a different subset, and someone who never
/// writes a headline is paying for three large text boxes with the space their keywords would use.
/// </summary>
public class MetadataFieldVisibilityTests
{
    [Fact]
    public void Everything_is_visible_by_default()
    {
        var settings = AppSettings.Default;

        Assert.Empty(settings.HiddenMetadataFields);
        Assert.All(Enum.GetValues<MetadataField>(), field => Assert.True(settings.IsFieldVisible(field)));
    }

    [Fact]
    public void A_hidden_field_reports_itself_hidden()
    {
        var settings = AppSettings.Default with { HiddenMetadataFields = [MetadataField.Headline] };

        Assert.False(settings.IsFieldVisible(MetadataField.Headline));
        Assert.True(settings.IsFieldVisible(MetadataField.Title));
    }

    /// <summary>
    /// The reason this stores what to hide rather than what to show. A field added in a later version
    /// is absent from everyone's hidden list and so appears for everyone; an allow-list would hide
    /// every new field from every existing user, silently.
    /// </summary>
    [Fact]
    public void A_field_nobody_has_heard_of_is_visible()
    {
        var settings = AppSettings.Default with
        {
            HiddenMetadataFields = [MetadataField.Title, MetadataField.Headline]
        };

        // Stands in for a field introduced after these settings were written.
        Assert.True(settings.IsFieldVisible(MetadataField.Copyright));
    }

    [Fact]
    public void Hiding_several_fields_leaves_the_rest_alone()
    {
        var settings = AppSettings.Default with
        {
            HiddenMetadataFields = [MetadataField.Title, MetadataField.Headline, MetadataField.Description]
        };

        Assert.False(settings.IsFieldVisible(MetadataField.Description));
        Assert.True(settings.IsFieldVisible(MetadataField.Keywords));
        Assert.True(settings.IsFieldVisible(MetadataField.Rating));
    }

    /// <summary>Hiding a field is a display choice; nothing about it reaches the files.</summary>
    [Fact]
    public void Hiding_a_field_changes_nothing_else()
    {
        var before = AppSettings.Default;
        var after = before with { HiddenMetadataFields = [MetadataField.Title] };

        Assert.Equal(before.RestrictKeywordsToLibrary, after.RestrictKeywordsToLibrary);
        Assert.Equal(before.DevelopRawFiles, after.DevelopRawFiles);
        Assert.Equal(before.RenderCacheEnabled, after.RenderCacheEnabled);
    }
}
