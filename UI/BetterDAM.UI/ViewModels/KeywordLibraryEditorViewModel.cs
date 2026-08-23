using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using BetterDAM.Core.Interfaces;
using BetterDAM.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace BetterDAM.UI.ViewModels;

/// <summary>
/// Somewhere a keyword can be moved to. A null <paramref name="Node"/> means the top level.
/// </summary>
/// <param name="Label">Indented by depth, so the shape of the tree is readable in a flat list.</param>
public sealed record KeywordMoveTarget(string Label, KeywordNodeViewModel? Node);

/// <summary>
/// The Keywords tab: build and arrange the vocabulary that the metadata panel will offer.
///
/// Saved as it is edited rather than behind a Save button. Everything here is one small change at a
/// time — rename a category, add a keyword, delete a typo — and a dialog that could be closed with
/// unsaved work would be the only way to lose any of it.
/// </summary>
public sealed partial class KeywordLibraryEditorViewModel : ObservableObject
{
    /// <summary>
    /// Long enough that typing a name is one save rather than one per keystroke, short enough that
    /// closing the window straight after typing still catches it.
    /// </summary>
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(400);

    private readonly IKeywordLibraryService _library;
    private readonly ICatalog _catalog;
    private readonly ILogger<KeywordLibraryEditorViewModel> _logger;

    private CancellationTokenSource? _saveCts;
    private bool _loading;

    public KeywordLibraryEditorViewModel(
        IKeywordLibraryService library,
        ICatalog catalog,
        ILogger<KeywordLibraryEditorViewModel> logger)
    {
        _library = library;
        _catalog = catalog;
        _logger = logger;

        Load(library.Current);
    }

    /// <summary>The open workspace, so Import can be scoped to it. Set by the view.</summary>
    public string? WorkspacePath { get; set; }

    public ObservableCollection<KeywordNodeViewModel> Roots { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MoveTargets))]
    [NotifyPropertyChangedFor(nameof(CanMoveSelected))]
    private KeywordNodeViewModel? _selected;

    /// <summary>
    /// Where the selected keyword could be filed instead — every other group, plus the top level.
    ///
    /// A list rather than drag and drop. Dragging is the obvious gesture and the wrong one here: the
    /// tree scrolls, the targets are often off screen, and dropping between two rows means something
    /// different from dropping onto one. Picking the destination by name is unambiguous and works the
    /// same whether the target is the next row or four hundred rows away.
    /// </summary>
    public IReadOnlyList<KeywordMoveTarget> MoveTargets => GetMoveTargetsFor(Selected);

    /// <summary>Where <paramref name="node"/> could be filed instead.</summary>
    public IReadOnlyList<KeywordMoveTarget> GetMoveTargetsFor(KeywordNodeViewModel? node)
    {
        if (node is null)
        {
            return [];
        }

        var targets = new List<KeywordMoveTarget>();

        if (node.Parent is not null)
        {
            targets.Add(new KeywordMoveTarget("Top level", null));
        }

        foreach (var root in Roots)
        {
            Collect(root, depth: 0, targets, node);
        }

        return targets;

        static void Collect(
            KeywordNodeViewModel candidate,
            int depth,
            List<KeywordMoveTarget> into,
            KeywordNodeViewModel moving)
        {
            // Not into itself or anything beneath it — that would detach the subtree from the roots
            // and lose it — and not into the parent it is already under.
            if (moving.Contains(candidate))
            {
                return;
            }

            if (!ReferenceEquals(candidate, moving.Parent))
            {
                into.Add(new KeywordMoveTarget(new string(' ', depth * 4) + candidate.Name, candidate));
            }

            foreach (var child in candidate.Children)
            {
                Collect(child, depth + 1, into, moving);
            }
        }
    }

    public bool CanMoveSelected => MoveTargets.Count > 0;

    /// <summary>
    /// Refiles the selected keyword. The subtree travels with it, which is what the tree already
    /// shows and so is what anyone would expect.
    /// </summary>
    [RelayCommand]
    private void MoveSelected(KeywordMoveTarget? target) => Move(Selected, target);

    /// <summary>
    /// Refiles a keyword. The subtree travels with it, which is what the tree already shows and so is
    /// what anyone would expect.
    /// </summary>
    public void Move(KeywordNodeViewModel? node, KeywordMoveTarget? target)
    {
        if (node is null || target is null || (target.Node is { } into && node.Contains(into)))
        {
            return;
        }

        (node.Parent?.Children ?? Roots).Remove(node);

        node.Parent = target.Node;

        var destination = target.Node?.Children ?? Roots;
        destination.Add(node);
        Sort(destination);

        if (target.Node is { } parent)
        {
            parent.IsExpanded = true;
        }

        Selected = node;
        Refresh();
    }

    [ObservableProperty]
    private bool _isImporting;

    [ObservableProperty]
    private string? _statusMessage;

    public bool IsEmpty => Roots.Count == 0;

    public string CountSummary => IsEmpty
        ? "No keywords yet."
        : $"{Roots.Sum(CountIn):N0} keyword(s) in {Roots.Count:N0} group(s).";

    private static int CountIn(KeywordNodeViewModel node) => 1 + node.Children.Sum(CountIn);

    private void Load(KeywordLibrary library)
    {
        _loading = true;

        try
        {
            foreach (var root in Roots)
            {
                Detach(root);
            }

            Roots.Clear();

            foreach (var root in library.Roots)
            {
                Add(Roots, KeywordNodeViewModel.FromModel(root));
            }

            // Cheap insurance: a library saved by an earlier build, or hand-edited, need not be in
            // order, and the list is meant to be alphabetical whatever it was loaded from.
            SortDeep(Roots);
        }
        finally
        {
            _loading = false;
        }

        Refresh();
    }

    /// <summary>
    /// Watches a node and everything under it, so any edit anywhere in the tree schedules a save
    /// without every command having to remember to ask for one.
    /// </summary>
    private void Add(ObservableCollection<KeywordNodeViewModel> into, KeywordNodeViewModel node)
    {
        into.Add(node);
        Attach(node);
    }

    private void Attach(KeywordNodeViewModel node)
    {
        node.Owner = this;
        node.PropertyChanged += OnNodeChanged;
        node.Children.CollectionChanged += OnChildrenChanged;

        foreach (var child in node.Children)
        {
            Attach(child);
        }
    }

    private void Detach(KeywordNodeViewModel node)
    {
        node.Owner = null;
        node.PropertyChanged -= OnNodeChanged;
        node.Children.CollectionChanged -= OnChildrenChanged;

        foreach (var child in node.Children)
        {
            Detach(child);
        }
    }

    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Expanding a branch is a view state, not a change to the vocabulary.
        if (e.PropertyName != nameof(KeywordNodeViewModel.IsExpanded))
        {
            ScheduleSave();
        }
    }

    private void OnChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // A Move reports the same node as both added and removed. Treating it like the others would
        // attach it and then immediately detach it, leaving a live node nothing is listening to —
        // and sorting is entirely Moves, so every sort would quietly deafen the tree.
        if (e.Action != NotifyCollectionChangedAction.Move)
        {
            foreach (var added in e.NewItems?.OfType<KeywordNodeViewModel>() ?? [])
            {
                Attach(added);
            }

            foreach (var removed in e.OldItems?.OfType<KeywordNodeViewModel>() ?? [])
            {
                Detach(removed);
            }
        }

        ScheduleSave();
    }

    /// <summary>
    /// Puts one level in alphabetical order, in place.
    ///
    /// Reordered with <see cref="ObservableCollection{T}.Move"/> rather than rebuilt, so the nodes
    /// keep their identity — a rebuild would drop the selection and the expanded state, and detach
    /// every handler on the way past.
    /// </summary>
    private static void Sort(ObservableCollection<KeywordNodeViewModel> nodes)
    {
        for (var i = 1; i < nodes.Count; i++)
        {
            var node = nodes[i];

            var target = i - 1;
            while (target >= 0 && Compare(nodes[target], node) > 0)
            {
                target--;
            }

            if (target + 1 != i)
            {
                nodes.Move(i, target + 1);
            }
        }

        static int Compare(KeywordNodeViewModel a, KeywordNodeViewModel b)
            => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static void SortDeep(ObservableCollection<KeywordNodeViewModel> nodes)
    {
        Sort(nodes);

        foreach (var node in nodes)
        {
            SortDeep(node.Children);
        }
    }

    /// <summary>
    /// Re-sorts the level a keyword sits on.
    ///
    /// Called when a name has finished being edited rather than on every keystroke: sorting as the
    /// user types would send the row they are editing jumping around under the cursor.
    /// </summary>
    public void SortSiblingsOf(KeywordNodeViewModel? node)
    {
        if (node is not null)
        {
            Sort(node.Parent?.Children ?? Roots);
        }
    }

    [RelayCommand]
    private void AddGroup()
    {
        var node = new KeywordNodeViewModel("New group");
        Add(Roots, node);
        Sort(Roots);
        Selected = node;
        Refresh();
    }

    [RelayCommand]
    private void AddChild(KeywordNodeViewModel? parent)
    {
        if (parent is null)
        {
            return;
        }

        var node = new KeywordNodeViewModel("New keyword", parent);
        parent.Children.Add(node);
        Sort(parent.Children);
        parent.IsExpanded = true;
        Selected = node;
        Refresh();
    }

    [RelayCommand]
    private void Remove(KeywordNodeViewModel? node)
    {
        if (node is null)
        {
            return;
        }

        // Removing a group takes its children with it, which is what the tree already shows and so
        // is what anyone would expect. Nothing is written to any photograph either way.
        var siblings = node.Parent?.Children ?? Roots;
        siblings.Remove(node);

        Selected = null;
        Refresh();
    }

    /// <summary>
    /// Builds the vocabulary from what the photographs already say.
    ///
    /// Merged rather than replacing: someone who has arranged their groups by hand and then imports
    /// should gain the keywords they were missing, not have their arrangement flattened. Hierarchical
    /// keywords written by other tools — "Subject|animal" — are filed under the right parent.
    /// </summary>
    [RelayCommand]
    private async Task ImportAsync()
    {
        IsImporting = true;
        StatusMessage = null;

        try
        {
            var found = await _catalog.GetKeywordsAsync(WorkspacePath).ConfigureAwait(true);
            if (found.Count == 0)
            {
                StatusMessage = WorkspacePath is null
                    ? "No keywords found in the catalog."
                    : "No keywords found in this workspace. Index it first, or open a workspace that has some.";
                return;
            }

            var before = _library.Current.Count;
            var merged = _library.Current.MergedWith(found.Select(keyword => keyword.Value));

            await _library.SaveAsync(merged).ConfigureAwait(true);
            Load(merged);

            var added = merged.Count - before;
            StatusMessage = added == 0
                ? $"Found {found.Count:N0} keyword(s); all of them were already in the library."
                : $"Imported {added:N0} new keyword(s) from {found.Count:N0} found.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not import keywords");
            StatusMessage = $"Import failed: {ex.Message}";
        }
        finally
        {
            IsImporting = false;
        }
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CountSummary));
        OnPropertyChanged(nameof(MoveTargets));
        OnPropertyChanged(nameof(CanMoveSelected));
    }

    /// <summary>
    /// Saves shortly after the last edit. Debounced because renaming a keyword raises a change per
    /// keystroke, and writing the whole library each time would be pointless work.
    /// </summary>
    private void ScheduleSave()
    {
        if (_loading)
        {
            return;
        }

        Refresh();

        _saveCts?.Cancel();
        _saveCts?.Dispose();
        _saveCts = new CancellationTokenSource();

        var token = _saveCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SaveDelay, token).ConfigureAwait(false);
                await SaveAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);
    }

    /// <summary>Writes the tree out. Public so the window can flush on close rather than lose an edit.</summary>
    public Task SaveAsync()
    {
        var library = new KeywordLibrary
        {
            Roots = [.. Roots.Select(root => root.ToModel()).OfType<KeywordNode>()]
        };

        return _library.SaveAsync(library);
    }
}
