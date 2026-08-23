using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using BetterDAM.Core.Interfaces;
using BetterDAM.Core.Models;
using BetterDAM.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace BetterDAM.UI.ViewModels;

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
        ILogger<MetadataInspectorViewModel> logger)
    {
        _metadata = metadata;
        _writer = writer;
        _pending = pending;
        _library = library;
        _logger = logger;

        // Rebuilt rather than patched: editing the library in Settings can change anything about it,
        // and the tick list is cheap to make.
        _library.Changed += (_, library) => BuildPicker(library);
        BuildPicker(_library.Current);
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

    private void BuildPicker(KeywordLibrary library)
    {
        KeywordPicker.Clear();

        foreach (var root in library.Roots)
        {
            KeywordPicker.Add(KeywordPickerNodeViewModel.FromModel(root, OnKeywordToggled));
        }

        SyncPicker();
        OnPropertyChanged(nameof(HasKeywordLibrary));
    }

    /// <summary>
    /// Brings every tick in line with the keywords on the file.
    ///
    /// Matched by name, which is what a keyword is — so the same word filed in two groups ticks in
    /// both places, and a keyword typed by hand ticks wherever it appears in the library.
    /// </summary>
    private void SyncPicker()
    {
        var applied = Keywords.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var node in KeywordPicker.SelectMany(root => root.SelfAndDescendants()))
        {
            node.SyncChecked(applied.Contains(node.Name));
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
        SyncPicker();
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

    [RelayCommand]
    private void AddKeyword()
    {
        var keyword = NewKeyword?.Trim();
        if (string.IsNullOrEmpty(keyword))
        {
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
        SyncPicker();
        RecordEdit();
    }

    [RelayCommand]
    private void RemoveKeyword(string? keyword)
    {
        if (keyword is not null && Keywords.Remove(keyword))
        {
            SyncPicker();
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

            SyncPicker();
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
