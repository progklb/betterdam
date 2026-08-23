using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using BetterDAM.Core.Interfaces;
using BetterDAM.Core.Models;
using BetterDAM.UI.Services;
using BetterDAM.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace BetterDAM.UI.ViewModels;

/// <summary>
/// A keyword on the current file, and whether the library knows it.
/// </summary>
/// <param name="IsInLibrary">
/// False for keywords that arrived with the file — from a camera, or another tool. The chip offers to
/// adopt those, so a vocabulary can be reconciled where the gap is noticed.
/// </param>
/// <param name="IsCopyAction">
/// True for the single trailing pill that copies the set.
///
/// Carried in the same collection rather than placed beside it so that it wraps with the keywords
/// instead of being pushed onto a line of its own: two elements in one wrap panel would each be
/// measured as a block, and a long list would either overflow or strand the button.
/// </param>
public sealed record AppliedKeyword(string Name, bool IsInLibrary, bool IsCopyAction = false)
{
    public static readonly AppliedKeyword Copy = new("Copy", true, IsCopyAction: true);
}

/// <summary>
/// The metadata inspector. Edits made here never touch the media file — they are written to the
/// <see cref="IPendingChangeStore"/> and stay there until an explicit Sync in a later phase.
/// </summary>
public sealed partial class MetadataInspectorViewModel : ObservableObject
{
    private readonly IMetadataProvider _metadata;
    private readonly IMetadataWriter _writer;
    private readonly IPendingChangeStore _pending;
    private readonly IKeywordLibraryService _library;
    private readonly ISettingsService _settings;
    private readonly IKeywordClipboard _clipboard;
    private readonly ILogger<MetadataInspectorViewModel> _logger;

    private CancellationTokenSource? _loadCts;
    private MediaItemViewModel? _item;
    private EditableMetadata _baseline = EditableMetadata.Empty;
    private MediaMetadata _lastRead = MediaMetadata.Empty;

    /// <summary>
    /// Guards the field setters while <see cref="LoadAsync"/> populates them, so filling the form
    /// from disk is not mistaken for the user typing and recorded as a pending change.
    /// </summary>
    private bool _suppressEdits;

    public MetadataInspectorViewModel(
        IMetadataProvider metadata,
        IMetadataWriter writer,
        IPendingChangeStore pending,
        IKeywordLibraryService library,
        ISettingsService settings,
        IKeywordClipboard clipboard,
        ILogger<MetadataInspectorViewModel> logger)
    {
        _metadata = metadata;
        _writer = writer;
        _pending = pending;
        _library = library;
        _settings = settings;
        _clipboard = clipboard;
        _logger = logger;

        // Rebuilt rather than patched: editing the library in Settings can change anything about it,
        // and the tick list is cheap to make.
        _library.Changed += (_, library) => BuildPicker(library);
        BuildPicker(_library.Current);
        _settings.Changed += (_, _) => OnPropertyChanged(nameof(IsRestrictedToLibrary));
        _clipboard.Changed += (_, _) => OnPropertyChanged(nameof(CopiedKeywordsSummary));
    }

    // ---- Keyword library ------------------------------------------------------------------------

    public ObservableCollection<KeywordPickerNodeViewModel> KeywordPicker { get; } = [];

    /// <summary>
    /// Whether the tick list is showing.
    ///
    /// Deliberately not reset when the selection changes. Working through a folder of untagged media
    /// means tagging one, moving to the next and tagging that — a panel that folded itself away each
    /// time would have to be reopened for every photograph. It starts closed on launch, because most
    /// sessions are not tagging sessions and the panel is calmer without it.
    /// </summary>
    [ObservableProperty]
    private bool _isKeywordLibraryOpen;

    public bool HasKeywordLibrary => KeywordPicker.Count > 0;

    /// <summary>
    /// Whether typing can only choose from the library. Off automatically when there is no library —
    /// there would be nothing to choose from, and refusing every word would be absurd.
    /// </summary>
    public bool IsRestrictedToLibrary => HasKeywordLibrary && _settings.Current.RestrictKeywordsToLibrary;

    /// <summary>
    /// Keywords matching what has been typed, flattened out of the tree.
    ///
    /// Flat on purpose: while searching, the answer is a short list of candidates, and the shape of
    /// the tree only gets in the way of picking one. The tree comes back when the box is empty.
    /// </summary>
    public ObservableCollection<KeywordPickerNodeViewModel> KeywordMatches { get; } = [];

    public bool IsSearchingKeywords => !string.IsNullOrWhiteSpace(NewKeyword);

    /// <summary>
    /// Nothing in the library matches what was typed. Not an error — it is how a vocabulary grows —
    /// so it is offered as an action rather than reported as a failure.
    /// </summary>
    public bool HasNoKeywordMatch => IsSearchingKeywords && KeywordMatches.Count == 0;

    public bool CanAddTypedKeywordToLibrary => HasNoKeywordMatch && HasKeywordLibrary;

    public string AddToLibraryPrompt => $"Add \"{NewKeyword?.Trim()}\" to your library";

    public string KeywordInputWatermark => IsRestrictedToLibrary
        ? "Find a keyword…"
        : "Add keyword…";

    partial void OnNewKeywordChanged(string? value) => RefreshMatches();

    private void RefreshMatches()
    {
        KeywordMatches.Clear();

        var term = NewKeyword?.Trim();

        if (!string.IsNullOrEmpty(term))
        {
            // Contains rather than starts-with: "hour" should find "Golden Hour", which is exactly
            // the case where remembering the beginning of the name is hardest.
            foreach (var node in KeywordPicker
                         .SelectMany(root => root.SelfAndDescendants())
                         .Where(node => node.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase))
            {
                KeywordMatches.Add(node);
            }
        }

        OnPropertyChanged(nameof(IsSearchingKeywords));
        OnPropertyChanged(nameof(HasNoKeywordMatch));
        OnPropertyChanged(nameof(CanAddTypedKeywordToLibrary));
        OnPropertyChanged(nameof(AddToLibraryPrompt));
    }

    /// <summary>
    /// The keywords on this file, each knowing whether the library has heard of it.
    ///
    /// Kept alongside <see cref="Keywords"/> rather than replacing it: that collection is what gets
    /// written, and putting a view concern into it would mean touching the save path for a chip.
    /// </summary>
    public ObservableCollection<AppliedKeyword> AppliedKeywords { get; } = [];

    /// <summary>
    /// Files arrive with keywords from cameras and other tools, and no library will ever have all of
    /// them. Offering to adopt one where it is noticed beats a separate reconciliation pass that
    /// nobody will run.
    /// </summary>
    [RelayCommand]
    private async Task AdoptKeywordAsync(string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return;
        }

        await _library.SaveAsync(_library.Current.MergedWith([keyword.Trim()])).ConfigureAwait(true);
    }

    /// <summary>Adds what was typed to the library and applies it, in one action.</summary>
    [RelayCommand]
    private async Task AddTypedKeywordToLibraryAsync()
    {
        var keyword = NewKeyword?.Trim();
        if (string.IsNullOrEmpty(keyword))
        {
            return;
        }

        await _library.SaveAsync(_library.Current.MergedWith([keyword])).ConfigureAwait(true);

        if (AddKeywordNamed(keyword))
        {
            SyncKeywordState();
            RecordEdit();
        }

        NewKeyword = string.Empty;
    }

    private void BuildPicker(KeywordLibrary library)
    {
        KeywordPicker.Clear();

        foreach (var root in library.Roots)
        {
            KeywordPicker.Add(KeywordPickerNodeViewModel.FromModel(root, OnKeywordToggled));
        }

        SyncKeywordState();
        RefreshMatches();

        OnPropertyChanged(nameof(HasKeywordLibrary));
        OnPropertyChanged(nameof(IsRestrictedToLibrary));
        OnPropertyChanged(nameof(KeywordInputWatermark));
        OnPropertyChanged(nameof(CanAddTypedKeywordToLibrary));
    }

    /// <summary>
    /// Brings every tick in line with the keywords on the file.
    ///
    /// Matched by name, which is what a keyword is — so the same word filed in two groups ticks in
    /// both places, and a keyword typed by hand ticks wherever it appears in the library.
    /// </summary>
    private void SyncKeywordState()
    {
        var applied = Keywords.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var node in KeywordPicker.SelectMany(root => root.SelfAndDescendants()))
        {
            node.SyncChecked(applied.Contains(node.Name));
        }

        var known = _library.Current.AllNames();

        OnPropertyChanged(nameof(HasKeywords));

        AppliedKeywords.Clear();
        foreach (var keyword in Keywords)
        {
            AppliedKeywords.Add(new AppliedKeyword(keyword, known.Contains(keyword)));
        }

        // Trails the keywords it would copy. Nothing to copy, nothing to show.
        if (AppliedKeywords.Count > 0)
        {
            AppliedKeywords.Add(AppliedKeyword.Copy);
        }
    }

    /// <summary>
    /// Applies or removes a keyword. Ticking is exactly equivalent to typing the name into the box:
    /// the same bare word goes to the file, and the grouping stays behind in the library.
    /// </summary>
    private void OnKeywordToggled(KeywordPickerNodeViewModel node, bool isChecked)
    {
        if (_suppressEdits)
        {
            return;
        }

        var changed = isChecked
            ? AddKeywordNamed(node.Name)
            : RemoveKeywordNamed(node.Name);

        if (!changed)
        {
            return;
        }

        // The same name can appear in more than one group; they all describe one keyword, so they
        // all have to move together.
        SyncKeywordState();
        RecordEdit();
    }

    private bool AddKeywordNamed(string keyword)
    {
        if (Keywords.Contains(keyword, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        Keywords.Add(keyword);
        return true;
    }

    private bool RemoveKeywordNamed(string keyword)
    {
        var existing = Keywords.FirstOrDefault(k => string.Equals(k, keyword, StringComparison.OrdinalIgnoreCase));

        return existing is not null && Keywords.Remove(existing);
    }

    public ObservableCollection<string> Keywords { get; } = [];

    public ObservableCollection<RawMetadataTag> RawTags { get; } = [];

    public ObservableCollection<MetadataConflict> Conflicts { get; } = [];

    public bool IsMetadataEngineAvailable => _metadata.IsAvailable;

    public static string ExifToolInstallHint => OperatingSystem.IsMacOS()
        ? "Install it with:  brew install exiftool"
        : OperatingSystem.IsWindows()
            ? "Install it with:  winget install OliverBetz.ExifTool"
            : "Install it with your package manager, e.g.  sudo apt install libimage-exiftool-perl";

    [ObservableProperty]
    private bool _hasItem;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private CameraInfo _camera = CameraInfo.Empty;

    [ObservableProperty]
    private VideoInfo _video = VideoInfo.Empty;

    [ObservableProperty]
    private bool _isVideo;

    /// <summary>
    /// Which inspector tab is showing. Bound rather than left to the TabControl because the Video
    /// tab is hidden for stills: without this, selecting an image after a video leaves the hidden
    /// tab selected and the panel shows empty video fields with no tab highlighted.
    /// </summary>
    [ObservableProperty]
    private int _selectedTabIndex;

    private const int VideoTabIndex = 2;

    [ObservableProperty]
    private bool _hasSidecar;

    [ObservableProperty]
    private string? _sidecarPath;

    [ObservableProperty]
    private bool _hasPendingChanges;

    [ObservableProperty]
    private bool _hasConflicts;

    [ObservableProperty]
    private bool _isWriting;

    [ObservableProperty]
    private string? _writeStatus;

    [ObservableProperty]
    private bool _writeFailed;

    [ObservableProperty]
    private string? _newKeyword;

    // Editable fields. Each records a pending change when the user — not the loader — changes it.
    [ObservableProperty]
    private string? _title;

    [ObservableProperty]
    private string? _description;

    [ObservableProperty]
    private int _rating;

    [ObservableProperty]
    private string? _label;

    [ObservableProperty]
    private string? _creator;

    [ObservableProperty]
    private string? _copyright;

    [ObservableProperty]
    private string? _headline;

    partial void OnTitleChanged(string? value) => RecordEdit();

    partial void OnDescriptionChanged(string? value) => RecordEdit();

    partial void OnRatingChanged(int value) => RecordEdit();

    partial void OnLabelChanged(string? value) => RecordEdit();

    partial void OnCreatorChanged(string? value) => RecordEdit();

    partial void OnCopyrightChanged(string? value) => RecordEdit();

    partial void OnHeadlineChanged(string? value) => RecordEdit();

    public async Task LoadAsync(MediaItemViewModel? item)
    {
        if (_loadCts is { } previous)
        {
            await previous.CancelAsync();
            previous.Dispose();
        }

        _item = item;
        HasItem = item is not null;

        if (item is null)
        {
            Reset();
            _loadCts = null;
            return;
        }

        IsVideo = item.IsVideo;
        if (!IsVideo && SelectedTabIndex == VideoTabIndex)
        {
            SelectedTabIndex = 0;
        }

        var cts = new CancellationTokenSource();
        _loadCts = cts;
        IsLoading = true;

        try
        {
            var result = await _metadata.ReadAsync(item.File, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            var metadata = result ?? MediaMetadata.Empty;
            _lastRead = metadata;
            _baseline = metadata.Effective;

            // A pending edit for this file wins over what is on disk: it is what the user last chose.
            var effective = _pending.GetEdited(item.File.FullPath) ?? _baseline;

            Apply(effective);

            Camera = metadata.Camera;
            Video = metadata.Video;
            HasSidecar = metadata.HasSidecar;
            SidecarPath = metadata.SidecarPath;
            HasPendingChanges = _pending.HasChanges(item.File.FullPath);

            RawTags.Clear();
            foreach (var tag in metadata.RawTags)
            {
                RawTags.Add(tag);
            }

            Conflicts.Clear();
            foreach (var conflict in MetadataConflictDetector.Detect(metadata))
            {
                Conflicts.Add(conflict);
            }

            HasConflicts = Conflicts.Count > 0;
            item.HasConflicts = HasConflicts;
            item.HasSidecar = metadata.HasSidecar;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read metadata for {File}", item.File.FullPath);
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                IsLoading = false;
                _loadCts = null;
                cts.Dispose();
            }
        }
    }

    /// <summary>
    /// What Enter and the Add button do.
    ///
    /// With a library and the restriction on, this picks the best match rather than accepting the
    /// text — so typing three letters is the quick path to a consistent keyword. Without a library,
    /// or with the restriction off, it takes the text as written, which is what it has always done.
    /// </summary>
    [RelayCommand]
    private void AddKeyword()
    {
        var keyword = NewKeyword?.Trim();
        if (string.IsNullOrEmpty(keyword))
        {
            return;
        }

        if (IsRestrictedToLibrary)
        {
            // An exact name beats a nearer-the-top partial one: typing "sand" in full should not
            // apply "Sand Dune" because it happened to sort first.
            var match = KeywordMatches.FirstOrDefault(
                            node => string.Equals(node.Name, keyword, StringComparison.OrdinalIgnoreCase))
                        ?? KeywordMatches.FirstOrDefault();

            if (match is not null)
            {
                ApplyFromLibrary(match.Name);
            }

            return;
        }

        // Accept a pasted "a, b, c" as three keywords rather than one odd-looking entry.
        foreach (var part in keyword.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Keywords.Contains(part, StringComparer.OrdinalIgnoreCase))
            {
                Keywords.Add(part);
            }
        }

        NewKeyword = string.Empty;
        SyncKeywordState();
        RecordEdit();
    }

    /// <summary>
    /// Copies this photograph's keywords, ready to be applied to a selection.
    ///
    /// The copy is taken here rather than from a tile because this is the one place the keywords are
    /// visible — copying from something you cannot see would be a guess.
    /// </summary>
    [RelayCommand]
    private void CopyKeywords() => _clipboard.Copy(Keywords);

    /// <summary>
    /// Copies the keywords of a file that may not be the one on screen — what the grid's right-click
    /// menu needs.
    ///
    /// Reads through the pending store first, so copying picks up edits that have not been written
    /// yet. Copying what is on disk while the panel shows something else would be a quiet way to
    /// spread stale tags.
    /// </summary>
    public async Task CopyKeywordsFromAsync(MediaFile file)
    {
        if (_item?.File.FullPath == file.FullPath)
        {
            _clipboard.Copy(Keywords);
            return;
        }

        if (_pending.GetEdited(file.FullPath) is { } edited)
        {
            _clipboard.Copy(edited.Keywords);
            return;
        }

        try
        {
            var metadata = await _metadata.ReadAsync(file).ConfigureAwait(true);
            _clipboard.Copy(metadata.Effective.Keywords);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read keywords from {File}", file.FullPath);
        }
    }

    public bool HasKeywords => Keywords.Count > 0;

    /// <summary>What is on the keyword clipboard, for the paste control to describe itself.</summary>
    public string CopiedKeywordsSummary => _clipboard.HasKeywords
        ? string.Join(", ", _clipboard.Keywords)
        : "Nothing copied";

    /// <summary>Applies a keyword chosen from the library and clears the search.</summary>
    [RelayCommand]
    private void ApplyFromLibrary(string? keyword)
    {
        if (keyword is not null && AddKeywordNamed(keyword))
        {
            SyncKeywordState();
            RecordEdit();
        }

        NewKeyword = string.Empty;
    }

    [RelayCommand]
    private void RemoveKeyword(string? keyword)
    {
        if (keyword is not null && Keywords.Remove(keyword))
        {
            SyncKeywordState();
            RecordEdit();
        }
    }

    /// <summary>
    /// Takes the star position as a string because that is what a XAML <c>CommandParameter="4"</c>
    /// literal produces. Typing this as <c>int</c> makes RelayCommand&lt;int&gt;.CanExecute reject the
    /// string parameter, and the button silently does nothing.
    /// </summary>
    [RelayCommand]
    private void SetRating(string? position)
    {
        if (!int.TryParse(position, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rating))
        {
            return;
        }

        // Clicking the current rating clears it, which is how Bridge and Photo Mechanic behave.
        Rating = Rating == rating ? 0 : rating;
    }

    [RelayCommand]
    private void RevertChanges()
    {
        if (_item is null)
        {
            return;
        }

        _pending.Discard(_item.File.FullPath);
        Apply(_baseline);
        HasPendingChanges = false;
        _item.HasPendingChanges = false;
        WriteStatus = null;
    }

    /// <summary>
    /// Settles the detected conflicts by recording the chosen values as a pending edit. Nothing is
    /// written to disk here — resolving a conflict is a decision, and committing it is a separate,
    /// explicit act.
    /// </summary>
    [RelayCommand]
    private void ResolveConflicts(string? resolution)
    {
        if (_item is null || !Enum.TryParse<ConflictResolution>(resolution, out var choice))
        {
            return;
        }

        var resolved = MetadataConflictDetector.Resolve(_lastRead, choice);
        Apply(resolved);
        RecordEdit();

        Conflicts.Clear();
        HasConflicts = false;
        _item.HasConflicts = false;
    }

    [RelayCommand]
    private async Task WriteSidecarAsync()
    {
        if (_item is null || !_writer.IsAvailable)
        {
            return;
        }

        IsWriting = true;
        WriteStatus = null;
        WriteFailed = false;

        try
        {
            var toWrite = _pending.GetEdited(_item.File.FullPath) ?? _baseline;
            var result = await _writer.WriteSidecarAsync(_item.File, toWrite, new SidecarWriteOptions()).ConfigureAwait(true);

            if (result.Success)
            {
                // The sidecar now holds these values, so they become the new baseline and the file
                // is no longer pending.
                _pending.Discard(_item.File.FullPath);
                _item.HasPendingChanges = false;
                HasPendingChanges = false;
                WriteStatus = $"Saved to {Path.GetFileName(result.SidecarPath)}";

                await LoadAsync(_item).ConfigureAwait(true);
            }
            else
            {
                WriteFailed = true;
                WriteStatus = result.Error ?? "The sidecar could not be written.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed writing the sidecar for {File}", _item.File.FullPath);
            WriteFailed = true;
            WriteStatus = ex.Message;
        }
        finally
        {
            IsWriting = false;
        }
    }

    private void Apply(EditableMetadata metadata)
    {
        _suppressEdits = true;
        try
        {
            Title = metadata.Title;
            Description = metadata.Description;
            Rating = metadata.Rating ?? 0;
            Label = metadata.Label;
            Creator = metadata.Creator;
            Copyright = metadata.Copyright;
            Headline = metadata.Headline;

            Keywords.Clear();
            foreach (var keyword in metadata.Keywords)
            {
                Keywords.Add(keyword);
            }

            SyncKeywordState();
        }
        finally
        {
            _suppressEdits = false;
        }
    }

    private void Reset()
    {
        Apply(EditableMetadata.Empty);
        _baseline = EditableMetadata.Empty;
        _lastRead = MediaMetadata.Empty;
        Camera = CameraInfo.Empty;
        Video = VideoInfo.Empty;
        HasSidecar = false;
        SidecarPath = null;
        HasPendingChanges = false;
        HasConflicts = false;
        WriteStatus = null;
        WriteFailed = false;
        RawTags.Clear();
        Conflicts.Clear();
    }

    public bool CanWriteSidecar => _writer.IsAvailable;

    private void RecordEdit()
    {
        if (_suppressEdits || _item is null)
        {
            return;
        }

        var edited = new EditableMetadata
        {
            Title = NullIfBlank(Title),
            Description = NullIfBlank(Description),
            Keywords = Keywords.ToImmutableArray(),
            Rating = Rating == 0 ? null : Rating,
            Label = NullIfBlank(Label),
            Creator = NullIfBlank(Creator),
            Copyright = NullIfBlank(Copyright),
            Headline = NullIfBlank(Headline)
        };

        _pending.Set(_item.File.FullPath, _baseline, edited);

        HasPendingChanges = _pending.HasChanges(_item.File.FullPath);
        _item.HasPendingChanges = HasPendingChanges;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
