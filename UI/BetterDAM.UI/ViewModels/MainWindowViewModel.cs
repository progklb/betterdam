using Avalonia;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using BetterDAM.Core.Interfaces;
using BetterDAM.Core.Models;
using BetterDAM.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace BetterDAM.UI.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private const int PreviewEdgePixels = 1600;

    // Adding items to the UI collection one at a time makes a large scan crawl. Flushing in
    // batches keeps the grid filling visibly without a layout pass per file.
    private const int BatchSize = 64;
    private static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(80);

    private readonly IMediaScanner _scanner;
    private readonly IFolderBrowser _folderBrowser;
    private readonly IThumbnailService _thumbnails;
    private readonly IFullImageDecoder _fullImages;
    private readonly IFfmpegLocator _ffmpeg;
    private readonly IPendingChangeStore _pending;
    private readonly IMetadataWriter _writer;
    private readonly ICatalog _catalog;
    private readonly ICatalogIndexer _indexer;
    private readonly ISettingsService _settings;
    private readonly ILogger<MainWindowViewModel> _logger;

    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _previewCts;
    private CancellationTokenSource? _indexCts;
    private CancellationTokenSource? _workspaceIndexCts;
    private CancellationTokenSource? _searchCts;

    public MainWindowViewModel(
        IMediaScanner scanner,
        IFolderBrowser folderBrowser,
        IThumbnailService thumbnails,
        IFullImageDecoder fullImages,
        IFfmpegLocator ffmpeg,
        IPendingChangeStore pending,
        IMetadataWriter writer,
        ICatalog catalog,
        ICatalogIndexer indexer,
        ISettingsService settings,
        MetadataInspectorViewModel inspector,
        VideoPlayerViewModel player,
        BatchEditViewModel batch,
        ILogger<MainWindowViewModel> logger)
    {
        _scanner = scanner;
        _folderBrowser = folderBrowser;
        _thumbnails = thumbnails;
        _fullImages = fullImages;
        _ffmpeg = ffmpeg;
        _pending = pending;
        _writer = writer;
        _catalog = catalog;
        _indexer = indexer;
        _settings = settings;
        _logger = logger;
        Inspector = inspector;
        Player = player;
        Batch = batch;

        // The full pixel size arrives with the metadata, and the loupe needs it to open a RAW at the
        // right magnification before the develop has finished.
        Inspector.RawTags.CollectionChanged += (_, _) => OnPropertyChanged(nameof(LoupeTargetWidth));

        // A batch run marks many files at once; refresh whatever is on screen afterwards.
        // A batch run edits the pending store behind the inspector's back, so the panel has to be
        // told to re-read. Without this, pasting keywords onto the selected file left the old ones on
        // screen until something else happened to reload it.
        Batch.Applied += () =>
        {
            PendingChangeCount = _pending.Count;
            _ = Inspector.LoadAsync(SelectedItem);
        };

        _pending.Changed += (_, _) => PendingChangeCount = _pending.Count;

        // Driven off the collection itself rather than the four places that mutate it, so the
        // prompt cannot drift out of step with what is actually on screen.
        MediaItems.CollectionChanged += (_, _) => UpdateEmptyState();
        UpdateEmptyState();

        RecentWorkspaces = new ObservableCollection<string>(_settings.Current.RecentWorkspaces);
        _viewerOpensFullscreen = _settings.Current.ViewerOpensFullscreen;
        _developRawFiles = _settings.Current.DevelopRawFiles;

        var develop = _settings.Current.RawDevelop;
        _highlights = develop.Highlights;
        _whiteBalance = develop.WhiteBalance;
        _exposureStops = develop.ExposureStops;
        _noiseReduction = develop.NoiseReduction;
        _rawQuality = develop.Quality;

        // Otherwise the kind toggles start unticked on an empty query, which reads as "showing
        // nothing" when it means "showing everything".
        ReadFiltersFromQuery();

        StatusText = _ffmpeg.IsAvailable
            ? "Ready. Choose a folder to begin."
            : "Ready. FFmpeg was not found — video thumbnails are unavailable.";
    }

    public MetadataInspectorViewModel Inspector { get; }

    public VideoPlayerViewModel Player { get; }

    public BatchEditViewModel Batch { get; }

    public ObservableCollection<FolderNodeViewModel> FolderRoots { get; } = [];

    public ObservableCollection<MediaItemViewModel> MediaItems { get; } = [];

    /// <summary>
    /// Storage provider from the active window, supplied by the view. The ViewModel deliberately
    /// does not reach for the window itself.
    /// </summary>
    public IStorageProvider? StorageProvider { get; set; }

    [ObservableProperty]
    private FolderNodeViewModel? _selectedFolder;

    [ObservableProperty]
    private MediaItemViewModel? _selectedItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoupeSource))]
    [NotifyPropertyChangedFor(nameof(LoupeTargetWidth))]
    private Bitmap? _preview;

    [ObservableProperty]
    private bool _isPreviewLoading;

    /// <summary>
    /// What the loupe magnifies: the full-resolution decode when there is one, otherwise the cached
    /// preview.
    ///
    /// Falling back rather than waiting is the point. The loupe always shows real pixels at 1:1 —
    /// before the decode lands that is the 1600px preview, which is a genuine if modest magnification
    /// and perfectly sharp, and when the decode arrives the same spot simply becomes more detailed.
    /// The alternative, an empty box for the several seconds a RAW takes, would make the feature
    /// useless exactly when it is wanted.
    /// </summary>
    public Bitmap? LoupeSource => FullPreview ?? Preview;

    /// <summary>Whether the loupe is showing the photograph itself rather than a rendition of it.</summary>
    public bool IsLoupeFullResolution => FullPreview is not null;

    /// <summary>
    /// The pixel width that 100% in the loupe refers to.
    ///
    /// Known exactly once the file has been decoded. Before that it comes from the metadata already
    /// read for the inspector, which matters because it is the only way the loupe can open at the
    /// right magnification on a RAW whose develop has not finished — the embedded preview is a
    /// quarter of the size, and scaling to that would make the picture jump when the develop landed.
    /// Zero when nothing knows, which leaves the loupe at the source's own 1:1.
    /// </summary>
    public double LoupeTargetWidth
        => FullPreview?.PixelSize.Width ?? MetadataPixelWidth() ?? Preview?.PixelSize.Width ?? 0;

    /// <summary>
    /// The full pixel width according to ExifTool's Composite:ImageSize.
    ///
    /// Composite deliberately: on a RAF, File:ImageWidth is 4416 — the embedded preview — while the
    /// sensor image is 6240 wide. Reading the obvious tag would give exactly the wrong number.
    /// </summary>
    private double? MetadataPixelWidth()
    {
        var size = Inspector.RawTags
            .FirstOrDefault(tag => tag.QualifiedName is "Composite:ImageSize")?.Value;

        if (size is null)
        {
            return null;
        }

        var separator = size.IndexOf('x');
        return separator > 0
               && double.TryParse(size[..separator], NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
               && width > 0
            ? width
            : null;
    }

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _recursive = true;

    [ObservableProperty]
    private double _thumbnailSize = 160;

    [ObservableProperty]
    private string? _currentFolderPath;

    /// <summary>The open workspace, or null when none is. Everything else keys off this.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWorkspace))]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    [NotifyPropertyChangedFor(nameof(SearchWatermark))]
    private string? _workspacePath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    [NotifyPropertyChangedFor(nameof(SearchWatermark))]
    private string? _workspaceName;

    public bool HasWorkspace => WorkspacePath is not null;

    public string WindowTitle => WorkspaceName is null ? "BetterDAM" : $"{WorkspaceName} — BetterDAM";

    /// <summary>
    /// Names what will actually be searched, since the scope is the workspace rather than
    /// everything. Follows the Everywhere toggle so the field never claims the wrong scope.
    /// </summary>
    public string SearchWatermark => !HasWorkspace || SearchEverywhere
        ? "Search everything indexed"
        : $"Search {WorkspaceName}";

    /// <summary>
    /// The search vocabulary, for the help in the filter popup and the list offered at a colon.
    /// Read straight from the parser's own catalogue rather than restated here, so the help cannot
    /// describe a field that does not work or miss one that does.
    /// </summary>
    public static IReadOnlyList<SearchField> SearchHelp => SearchFields.All;

    /// <summary>
    /// The fields worth offering for what is currently half-typed. Empty when the box is not asking,
    /// which is also what hides the popup.
    /// </summary>
    public ObservableCollection<SearchField> FieldSuggestions { get; } = [];

    [ObservableProperty]
    private int _selectedSuggestionIndex = -1;

    public bool HasFieldSuggestions => FieldSuggestions.Count > 0;

    /// <summary>
    /// Recomputes what to offer for a caret position. Returns true when the popup should be open.
    /// </summary>
    public bool UpdateFieldSuggestions(string? text, int caret)
    {
        var prefix = SearchSuggestion.PrefixAt(text, caret);

        FieldSuggestions.Clear();

        if (prefix is not null)
        {
            foreach (var field in SearchFields.Matching(prefix))
            {
                FieldSuggestions.Add(field);
            }
        }

        SelectedSuggestionIndex = FieldSuggestions.Count > 0 ? 0 : -1;
        OnPropertyChanged(nameof(HasFieldSuggestions));

        return FieldSuggestions.Count > 0;
    }

    public void DismissFieldSuggestions()
    {
        if (FieldSuggestions.Count == 0)
        {
            return;
        }

        FieldSuggestions.Clear();
        SelectedSuggestionIndex = -1;
        OnPropertyChanged(nameof(HasFieldSuggestions));
    }

    // ---- Filter controls -------------------------------------------------------------------------

    /// <summary>
    /// Set while a control is rewriting the query, so the write-back does not immediately re-read
    /// and fight what was just set.
    /// </summary>
    private bool _syncingFilters;

    /// <summary>
    /// The rating floor the query currently asks for, 0 when it asks for none. Clicking the star
    /// that is already the floor clears it, which is the only way back to "any rating" without
    /// editing the text.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RatingFilterSummary))]
    private int _filterRating;

    [ObservableProperty]
    private bool _filterRaw;

    [ObservableProperty]
    private bool _filterJpeg;

    [ObservableProperty]
    private bool _filterVideo;

    partial void OnFilterRatingChanged(int value)
        => WriteFilter(() => SearchText = SearchQueryText.WithField(
            SearchText, "rating", value > 0 ? $">={value}" : null));

    partial void OnFilterRawChanged(bool value) => WriteKinds();

    partial void OnFilterJpegChanged(bool value) => WriteKinds();

    partial void OnFilterVideoChanged(bool value) => WriteKinds();

    private void WriteKinds() => WriteFilter(() =>
    {
        var kinds = new List<string>();

        if (FilterRaw) kinds.Add("raw");
        if (FilterJpeg) kinds.Add("jpg");
        if (FilterVideo) kinds.Add("video");

        // All three ticked is the same as no filter at all, and says so more plainly.
        var value = kinds.Count is 0 or 3 ? null : string.Join(',', kinds);

        SearchText = SearchQueryText.WithField(SearchText, "type", value);
    });

    private void WriteFilter(Action write)
    {
        if (_syncingFilters)
        {
            return;
        }

        _syncingFilters = true;

        try
        {
            write();
        }
        finally
        {
            _syncingFilters = false;
        }

        _ = SearchCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// Brings the controls in line with the query, so opening the popup shows what is actually being
    /// filtered — including filters that were typed rather than clicked.
    ///
    /// Read by parsing rather than by matching text, so <c>rating:&gt;=3</c> and <c>r:&gt;=3</c> and
    /// a query where the term sits in the middle all light the same three stars.
    /// </summary>
    private void ReadFiltersFromQuery()
    {
        if (_syncingFilters)
        {
            return;
        }

        var query = SearchQueryParser.Parse(SearchText);

        _syncingFilters = true;

        try
        {
            // Only a floor is representable as stars; anything else leaves them dark rather than
            // claiming a filter the query does not have.
            FilterRating = query.Rating is { Operator: ComparisonOperator.GreaterThanOrEqual } rating
                ? rating.Value
                : 0;

            var kinds = query.Kinds;
            var all = kinds.IsDefaultOrEmpty;

            FilterRaw = all || kinds.Contains(MediaKind.Raw);
            FilterJpeg = all || kinds.Contains(MediaKind.Jpeg);
            FilterVideo = all || kinds.Contains(MediaKind.Video);
        }
        finally
        {
            _syncingFilters = false;
        }
    }

    public string RatingFilterSummary => FilterRating > 0 ? "and up" : string.Empty;

    /// <summary>
    /// Clicking a star sets the floor, or clears it when it is already the floor — the only way back
    /// to "any rating" without editing the text.
    /// </summary>
    /// <param name="stars">
    /// Taken as a string because that is what a XAML CommandParameter is; parsing here keeps five
    /// buttons' markup free of x:Int32 wrappers.
    /// </param>
    [RelayCommand]
    private void SetRatingFilter(string? stars)
    {
        if (int.TryParse(stars, out var value))
        {
            FilterRating = FilterRating == value ? 0 : value;
        }
    }

    /// <summary>Moves through the offered fields, wrapping at both ends.</summary>
    public void MoveSuggestion(int delta)
    {
        if (FieldSuggestions.Count == 0)
        {
            return;
        }

        var next = SelectedSuggestionIndex + delta;
        SelectedSuggestionIndex = (next % FieldSuggestions.Count + FieldSuggestions.Count) % FieldSuggestions.Count;
    }

    /// <summary>
    /// The selection decoded at native resolution, for the viewer. Null until asked for: it costs
    /// tens of megabytes and a decode, neither of which is worth spending on the inline preview.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoupeSource))]
    [NotifyPropertyChangedFor(nameof(IsLoupeFullResolution))]
    [NotifyPropertyChangedFor(nameof(LoupeTargetWidth))]
    [NotifyPropertyChangedFor(nameof(PreviewSourceLabel))]
    [NotifyPropertyChangedFor(nameof(IsShowingDevelopedRaw))]
    private Bitmap? _fullPreview;

    private CancellationTokenSource? _fullPreviewCts;

    /// <summary>
    /// What has been asked for, delivered, and is held. See <see cref="FullPreviewTracker"/> — this
    /// used to be a single "last requested" field, which latched whenever a run ended without
    /// producing anything and left the picture stuck on its embedded JPEG.
    /// </summary>
    private readonly FullPreviewTracker _fullPreviewState = new();

    /// <summary>
    /// Which decoder produced what is on screen. Only a LibRaw develop answers to the develop
    /// settings, so the panel has to be able to say when it does not.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DevelopSettingsApply))]
    [NotifyPropertyChangedFor(nameof(RendererNote))]
    [NotifyPropertyChangedFor(nameof(PreviewSourceLabel))]
    [NotifyPropertyChangedFor(nameof(IsShowingDevelopedRaw))]
    private string? _fullPreviewRenderer;

    public bool DevelopSettingsApply => FullPreviewRenderer is null or DecodedImage.LibRaw;

    /// <summary>
    /// What is actually on screen in the viewer: the demosaiced sensor data, the JPEG the camera
    /// embedded in the RAW, or an ordinary image file.
    ///
    /// Worth stating rather than leaving to be inferred. The two renderings of a RAW can look similar
    /// at a glance and are not remotely the same thing to judge a photograph by, and which one you get
    /// depends on a setting, a keystroke, and whether the develop succeeded.
    /// </summary>
    public string PreviewSourceLabel
    {
        get
        {
            if (FullPreview is null)
            {
                return "Preview";
            }

            if (!IsRawSelected)
            {
                return "Full resolution";
            }

            // A develop is the only thing that sets a renderer. Falling back to the embedded preview
            // goes through the ordinary image path, which does not.
            return FullPreviewRenderer is null ? "JPEG" : "RAW";
        }
    }

    /// <summary>True while the viewer is showing developed sensor data, for the badge to colour by.</summary>
    public bool IsShowingDevelopedRaw => FullPreview is not null && IsRawSelected && FullPreviewRenderer is not null;

    /// <summary>Says why the controls are doing nothing, rather than leaving them looking broken.</summary>
    public string? RendererNote => DevelopSettingsApply
        ? null
        : "This file was rendered by macOS — LibRaw cannot unpack it, so these settings do not apply.";

    /// <summary>
    /// Set while the full-size decode is running. Developing a RAW takes seconds, and without
    /// saying so the viewer looks like it has simply decided the preview is as good as it gets.
    /// </summary>
    [ObservableProperty]
    private bool _isPreparingFullPreview;

    [ObservableProperty]
    private string? _fullPreviewStatus;

    /// <summary>
    /// Decodes the selection at full size if that has not already happened. Safe to call repeatedly;
    /// the viewer calls it whenever the selection changes.
    /// </summary>
    public async Task EnsureFullPreviewAsync()
    {
        // Selecting an item raises several properties, each of which asks the viewer to refresh.
        // Without this the same decode is started and cancelled two or three times, and a RAW
        // develop is far too expensive to do that to.
        var wanted = SelectedItem?.File.FullPath;
        if (!_fullPreviewState.ShouldStart(wanted))
        {
            return;
        }

        // Re-rendering the same photograph is a comparison: the point is to see one version replace
        // the other. Dropping back to the preview in between would put a different, lower-quality
        // image on screen for several seconds, which is exactly what makes the comparison useless.
        var changingFile = _fullPreviewState.IsChangingFile(wanted);

        _fullPreviewState.Begin(wanted);

        if (_fullPreviewCts is { } previous)
        {
            await previous.CancelAsync().ConfigureAwait(true);
            previous.Dispose();
        }

        if (changingFile)
        {
            DiscardFullPreview();
        }

        if (SelectedItem is not { } item || IsVideoSelected)
        {
            // Nothing to decode. The marker has to come off or every later request for this file
            // would be dismissed as already running.
            _fullPreviewState.Ended(wanted);
            return;
        }

        var cts = new CancellationTokenSource();
        _fullPreviewCts = cts;

        var developing = DevelopRawFiles && MediaTypeRegistry.IsRaw(item.File.FullPath);
        FullPreviewStatus = developing ? "Developing RAW…" : "Loading full resolution…";
        IsPreparingFullPreview = true;

        try
        {
            var decoded = await _fullImages.DecodeAsync(item.File, cts.Token).ConfigureAwait(true);

            // The selection can move while a 24MP file is decoding; a late arrival must not
            // replace what is now on screen.
            if (decoded is null || cts.IsCancellationRequested || !ReferenceEquals(SelectedItem, item))
            {
                return;
            }

            // Swapped, then the old one released: assigning first means the UI is already showing
            // the new bitmap before the previous one is disposed.
            var replaced = FullPreview;
            FullPreview = ToBitmap(decoded);
            FullPreviewRenderer = decoded.Renderer;
            _fullPreviewState.Delivering(item.File.FullPath);

            if (replaced is not null)
            {
                Dispatcher.UIThread.Post(replaced.Dispose, DispatcherPriority.Background);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load {File} at full size", item.File.FullPath);
        }
        finally
        {
            // Only the run that is still current may clear the indicator; an older one finishing
            // late must not hide the wait for the image now on screen.
            if (ReferenceEquals(_fullPreviewCts, cts))
            {
                IsPreparingFullPreview = false;
            }

            // Unconditionally, and whatever happened above: a run that returned early, threw, or was
            // cancelled must not leave this file looking like one already being worked on.
            _fullPreviewState.Ended(wanted);
        }
    }

    /// <summary>
    /// Switches between developing RAW files and showing their embedded preview. Bound to \ in the
    /// viewer, the way Lightroom uses it to flip between two renderings of the same shot.
    /// </summary>
    [RelayCommand]
    private void ToggleRawDevelopment() => DevelopRawFiles = !DevelopRawFiles;

    /// <summary>
    /// Gives up the full-size bitmap and forgets that it was ever asked for, so a later request
    /// decodes again rather than being swallowed by the guard.
    ///
    /// What the viewer calls when it closes. It cannot simply discard: the loupe in the main window
    /// wants the same bitmap, and leaving the request recorded would leave the loupe stuck on the
    /// preview for as long as that file stayed selected.
    /// </summary>
    public void ReleaseFullPreview()
    {
        DiscardFullPreview();
        InvalidateFullPreviewRequest();
    }

    /// <summary>
    /// Starts the full-resolution decode if it is cheap, so the loupe is sharp the moment it opens.
    ///
    /// Only for formats that decode quickly. Developing a RAW takes seconds and hundreds of megabytes,
    /// which is not worth spending on every photograph passed over on the way to somewhere else — for
    /// those the loupe asks when it is actually opened, and says what it is doing while it waits.
    /// </summary>
    private void PrepareLoupeSource()
    {
        var wanted = SelectedItem?.File.FullPath;

        // A full-size bitmap of the photograph we have just moved away from must go, or the loupe
        // would magnify the wrong picture. Skipped when a decode for this file is already in flight,
        // which is the case whenever the viewer window is open — clearing the guard there would
        // start a second develop of the same RAW.
        if (_fullPreviewState.InFlight != wanted && _fullPreviewState.Held != wanted)
        {
            ReleaseFullPreview();
        }

        if (SelectedItem is { } item
            && !IsVideoSelected
            && !MediaTypeRegistry.IsRaw(item.File.FullPath))
        {
            _ = EnsureFullPreviewAsync();
        }
    }

    /// <summary>Releases the full-size bitmap, which is far too large to keep around unseen.</summary>
    public void DiscardFullPreview()
    {
        FullPreview?.Dispose();
        FullPreview = null;
        _fullPreviewState.Forget();
    }

    /// <summary>
    /// Forgets what was last asked for, so the next request re-runs even for the same file. Needed
    /// when the *way* it is decoded changes rather than which file it is.
    /// </summary>
    private void InvalidateFullPreviewRequest() => _fullPreviewState.Invalidate();

    private static Bitmap ToBitmap(DecodedImage image)
    {
        var size = new PixelSize(image.Width, image.Height);
        var bitmap = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

        using (var locked = bitmap.Lock())
        {
            var stride = image.Width * 4;

            if (locked.RowBytes == stride)
            {
                System.Runtime.InteropServices.Marshal.Copy(image.Pixels, 0, locked.Address, image.Pixels.Length);
            }
            else
            {
                // Rows can be padded for alignment, so copy row by row rather than assuming a match.
                for (var row = 0; row < image.Height; row++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(
                        image.Pixels, row * stride, locked.Address + (row * locked.RowBytes), stride);
                }
            }
        }

        return bitmap;
    }

    /// <summary>
    /// True to develop RAW files for viewing, false to show the JPEG the camera embedded.
    /// Changing it reloads whatever is on screen, so the effect is visible immediately.
    /// </summary>
    [ObservableProperty]
    private bool _developRawFiles = true;

    partial void OnDevelopRawFilesChanged(bool value) => _ = ApplyDevelopRawFilesAsync(value);

    /// <summary>
    /// Saves the choice and re-renders what is on screen with it.
    ///
    /// The save has to be awaited. The decoder reads this from the settings service, and the service
    /// only publishes a new value once it has finished writing the file — so starting the decode
    /// without waiting raced the write and, about half the time, developed the file again with the
    /// value the toggle had just replaced. That was the "\ does not consistently toggle" bug: the
    /// picture and its badge would keep describing the old rendering while the controls showed the
    /// new one.
    ///
    /// This is what <see cref="SaveAndReloadDevelopAsync"/> already did for the develop settings; the
    /// two now behave the same way.
    /// </summary>
    private async Task ApplyDevelopRawFilesAsync(bool value)
    {
        await _settings.SaveAsync(_settings.Current with { DevelopRawFiles = value }).ConfigureAwait(true);

        // Same file, different rendering: the request guard has to be cleared or nothing happens.
        InvalidateFullPreviewRequest();
        await EnsureFullPreviewAsync().ConfigureAwait(true);
    }

    // ---- RAW develop controls -------------------------------------------------------------

    public static IReadOnlyList<RawHighlightMode> HighlightModes { get; } = Enum.GetValues<RawHighlightMode>();

    public static IReadOnlyList<RawWhiteBalance> WhiteBalanceModes { get; } = Enum.GetValues<RawWhiteBalance>();

    public static IReadOnlyList<RawNoiseReduction> NoiseReductionLevels { get; } = Enum.GetValues<RawNoiseReduction>();

    public static IReadOnlyList<RawQuality> RawQualities { get; } = Enum.GetValues<RawQuality>();

    [ObservableProperty]
    private RawHighlightMode _highlights;

    [ObservableProperty]
    private RawWhiteBalance _whiteBalance;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExposureDisplay))]
    private double _exposureStops;

    [ObservableProperty]
    private RawNoiseReduction _noiseReduction;

    [ObservableProperty]
    private RawQuality _rawQuality;

    /// <summary>Signed, because "+1 stop" and "1 stop" read differently at a glance.</summary>
    public string ExposureDisplay => Math.Abs(ExposureStops) < 0.01
        ? "As shot"
        : $"{ExposureStops:+0.0;-0.0} EV";

    /// <summary>True when the develop is untouched, so a Reset can be offered only when it does something.</summary>
    public bool HasDevelopAdjustments => !CurrentDevelop.IsDefault;

    /// <summary>Whether the develop controls apply to what is on screen.</summary>
    public bool IsRawSelected =>
        SelectedItem is { } item && MediaTypeRegistry.IsRaw(item.File.FullPath);

    private RawDevelopSettings CurrentDevelop => new()
    {
        Highlights = Highlights,
        WhiteBalance = WhiteBalance,
        ExposureStops = ExposureStops,
        NoiseReduction = NoiseReduction,
        Quality = RawQuality
    };

    partial void OnHighlightsChanged(RawHighlightMode value) => ApplyDevelopChange();

    partial void OnWhiteBalanceChanged(RawWhiteBalance value) => ApplyDevelopChange();

    partial void OnNoiseReductionChanged(RawNoiseReduction value) => ApplyDevelopChange();

    partial void OnRawQualityChanged(RawQuality value) => ApplyDevelopChange();

    // Dragged rather than picked, so it waits for the dragging to stop.
    partial void OnExposureStopsChanged(double value) => ApplyDevelopChange(debounce: true);

    [RelayCommand]
    private void ResetDevelop()
    {
        var defaults = RawDevelopSettings.Default;

        // Assigned through the properties so the UI follows, but the reload is left until the end
        // rather than firing once per field.
        _suppressDevelopReload = true;
        Highlights = defaults.Highlights;
        WhiteBalance = defaults.WhiteBalance;
        ExposureStops = defaults.ExposureStops;
        NoiseReduction = defaults.NoiseReduction;
        _suppressDevelopReload = false;

        ApplyDevelopChange();
    }

    private bool _suppressDevelopReload;
    private CancellationTokenSource? _developDebounceCts;

    /// <summary>
    /// Saves the develop settings and re-renders what is on screen.
    ///
    /// Debounced for anything dragged: a develop takes seconds, and starting one per slider tick
    /// would queue work faster than it could be cancelled.
    /// </summary>
    private void ApplyDevelopChange(bool debounce = false)
    {
        if (_suppressDevelopReload)
        {
            return;
        }

        OnPropertyChanged(nameof(HasDevelopAdjustments));

        _developDebounceCts?.Cancel();
        _developDebounceCts?.Dispose();
        _developDebounceCts = null;

        if (!debounce)
        {
            _ = SaveAndReloadDevelopAsync();
            return;
        }

        var cts = new CancellationTokenSource();
        _developDebounceCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400), cts.Token).ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(SaveAndReloadDevelopAsync);
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);
    }

    private async Task SaveAndReloadDevelopAsync()
    {
        await _settings.SaveAsync(_settings.Current with { RawDevelop = CurrentDevelop }).ConfigureAwait(true);

        // Same file, different rendering: the request guard has to be cleared or nothing happens.
        InvalidateFullPreviewRequest();
        await EnsureFullPreviewAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// True to open the viewer in real fullscreen, false to open it maximised on the current
    /// screen. Persisted, because it is a working preference rather than a per-item choice.
    /// </summary>
    [ObservableProperty]
    private bool _viewerOpensFullscreen;

    partial void OnViewerOpensFullscreenChanged(bool value)
        => _ = _settings.SaveAsync(_settings.Current with { ViewerOpensFullscreen = value });

    /// <summary>
    /// Collapses the folders panel so the thumbnails and preview get the whole window. Bound to the
    /// column width rather than the tree's visibility: a hidden child of a fixed-width column would
    /// leave the 240px gap behind.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderColumnWidth))]
    private bool _isFolderPanelVisible = true;

    public GridLength FolderColumnWidth => IsFolderPanelVisible ? new GridLength(240) : new GridLength(0);

    /// <summary>
    /// Moves the selection through the grid, for arrow-key browsing in the fullscreen viewer.
    /// Stops at the ends rather than wrapping: silently looping back to the first file gives no
    /// clue that the last one was reached.
    /// </summary>
    [RelayCommand]
    private void SelectNext() => MoveSelection(1);

    [RelayCommand]
    private void SelectPrevious() => MoveSelection(-1);

    private void MoveSelection(int delta)
    {
        if (MediaItems.Count == 0)
        {
            return;
        }

        var index = SelectedItem is null ? -1 : MediaItems.IndexOf(SelectedItem);
        var target = Math.Clamp(index + delta, 0, MediaItems.Count - 1);

        if (target != index)
        {
            SelectedItem = MediaItems[target];
        }
    }

    [RelayCommand]
    private void ToggleFolderPanel() => IsFolderPanelVisible = !IsFolderPanelVisible;

    /// <summary>
    /// Most recently opened first. Kept in step with the persisted list so the Open Recent menu can
    /// be rebuilt from it.
    /// </summary>
    public ObservableCollection<string> RecentWorkspaces { get; }

    /// <summary>
    /// Widens search past the workspace to the whole catalog. Off by default: a workspace that
    /// returned results from unrelated folders would not be much of a workspace.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchWatermark))]
    private bool _searchEverywhere;

    partial void OnSearchEverywhereChanged(bool value)
    {
        if (IsShowingSearchResults)
        {
            _ = SearchAsync();
        }
    }

    /// <summary>
    /// Shown in the preview pane only while a video is selected, so a missing FFmpeg stays out of
    /// the way during metadata work but is unmissable the moment someone wants to watch something.
    /// </summary>
    [ObservableProperty]
    private bool _showFfmpegNotice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingChanges))]
    private int _pendingChangeCount;

    public bool HasPendingChanges => PendingChangeCount > 0;

    [ObservableProperty]
    private bool _isWritingAll;

    /// <summary>
    /// Shown over the grid whenever it has nothing in it. There are three quite different reasons
    /// for an empty grid — nothing opened yet, a folder with no readable media, and a search that
    /// matched nothing — and each needs its own explanation to avoid looking like a failure.
    /// </summary>
    [ObservableProperty]
    private bool _showEmptyState = true;

    [ObservableProperty]
    private string _emptyStateTitle = string.Empty;

    [ObservableProperty]
    private string _emptyStateDetail = string.Empty;

    /// <summary>Only the opening prompt offers an action; the other two are explanations.</summary>
    [ObservableProperty]
    private bool _showEmptyStateAction;

    private void UpdateEmptyState()
    {
        // Scanning has its own progress indicator, and flashing "no media here" for the moment
        // before the first results arrive would be wrong as often as it is right.
        ShowEmptyState = MediaItems.Count == 0 && !IsScanning;
        if (!ShowEmptyState)
        {
            return;
        }

        if (IsShowingSearchResults)
        {
            EmptyStateTitle = "No matches";
            EmptyStateDetail = !SearchEverywhere && HasWorkspace
                ? $"Nothing in {WorkspaceName} matched. Search only finds files that have been "
                  + "indexed, and folders are indexed as you browse them. Turn on Search everywhere "
                  + "to look outside this workspace."
                : "Search only finds files that have been indexed. Folders are indexed in the "
                  + "background as you browse them, so open a folder once and its contents become "
                  + "searchable.";
            ShowEmptyStateAction = false;
        }
        else if (!HasWorkspace)
        {
            EmptyStateTitle = "No workspace open";
            EmptyStateDetail = "Open a folder to work in. It becomes the root of the tree, and "
                               + "searches are scoped to it.";
            ShowEmptyStateAction = true;
        }
        else
        {
            EmptyStateTitle = "Nothing to show here";
            EmptyStateDetail = Recursive
                ? "This folder and its subfolders contain no images or video BetterDAM can read."
                : "This folder contains no images or video BetterDAM can read. Turn on Recursive to "
                  + "include its subfolders.";
            ShowEmptyStateAction = false;
        }
    }

    partial void OnIsScanningChanged(bool value) => UpdateEmptyState();

    partial void OnCurrentFolderPathChanged(string? value) => UpdateEmptyState();

    partial void OnIsShowingSearchResultsChanged(bool value) => UpdateEmptyState();

    partial void OnWorkspacePathChanged(string? value) => UpdateEmptyState();

    /// <summary>
    /// Writes every pending edit to its XMP sidecar. Files are processed one at a time and failures
    /// are counted rather than aborting the run, so one unwritable file does not strand the rest.
    /// The media files themselves are never touched.
    /// </summary>
    [RelayCommand]
    private async Task WriteAllPendingSidecarsAsync()
    {
        if (!_writer.IsAvailable || _pending.Count == 0)
        {
            return;
        }

        var pending = _pending.GetAll();
        var byPath = MediaItems.ToDictionary(i => i.File.FullPath, StringComparer.Ordinal);

        IsWritingAll = true;
        var written = 0;
        var failed = 0;

        try
        {
            foreach (var change in pending)
            {
                if (!byPath.TryGetValue(change.FilePath, out var item))
                {
                    continue;
                }

                StatusText = $"Writing sidecars — {written + failed + 1} of {pending.Count}";

                var result = await _writer.WriteSidecarAsync(item.File, change.Edited, new SidecarWriteOptions());
                if (result.Success)
                {
                    _pending.Discard(change.FilePath);
                    item.HasPendingChanges = false;
                    item.HasSidecar = true;
                    written++;
                }
                else
                {
                    _logger.LogWarning("Sidecar write failed for {File}: {Error}", change.FilePath, result.Error);
                    failed++;
                }
            }

            StatusText = failed == 0
                ? $"Wrote {written} XMP sidecar(s). Original media untouched."
                : $"Wrote {written} sidecar(s), {failed} failed — see the log for details.";
        }
        finally
        {
            IsWritingAll = false;

            if (SelectedItem is { } selected)
            {
                await Inspector.LoadAsync(selected);
            }
        }
    }

    /// <summary>Called after the sync dialog closes: it clears whatever it committed.</summary>
    public void RefreshAfterSync()
    {
        PendingChangeCount = _pending.Count;

        foreach (var item in MediaItems)
        {
            item.HasPendingChanges = _pending.HasChanges(item.File.FullPath);
        }

        if (SelectedItem is { } selected)
        {
            _ = Inspector.LoadAsync(selected);
        }
    }

    [RelayCommand]
    private void DiscardAllPendingChanges()
    {
        _pending.DiscardAll();

        foreach (var item in MediaItems)
        {
            item.HasPendingChanges = false;
        }

        _ = Inspector.LoadAsync(SelectedItem);
    }

    public static string FfmpegInstallHint => OperatingSystem.IsMacOS()
        ? "Install it with:  brew install ffmpeg"
        : OperatingSystem.IsWindows()
            ? "Install it with:  winget install Gyan.FFmpeg"
            : "Install it with your package manager, e.g.  sudo apt install ffmpeg";

    partial void OnSelectedFolderChanged(FolderNodeViewModel? value)
    {
        if (value is null || value.IsPlaceholder)
        {
            return;
        }

        _ = ScanFolderAsync(value.FullPath);
    }

    partial void OnSelectedItemChanged(MediaItemViewModel? value)
    {
        ShowFfmpegNotice = value is { IsVideo: true } && !_ffmpeg.IsAvailable;
        IsVideoSelected = value is { IsVideo: true } && _ffmpeg.IsAvailable;
        OnPropertyChanged(nameof(IsRawSelected));
        OnPropertyChanged(nameof(PreviewSourceLabel));
        OnPropertyChanged(nameof(IsShowingDevelopedRaw));

        // A video is handed to the player; only stills use the static image preview, so the two
        // never fight over the same pane.
        _ = Player.LoadAsync(IsVideoSelected ? value!.File : null);
        _ = LoadPreviewAsync(IsVideoSelected ? null : value);
        _ = Inspector.LoadAsync(value);
    }

    [ObservableProperty]
    private bool _isVideoSelected;

    [ObservableProperty]
    private string? _searchText;

    /// <summary>
    /// Keeps the filter controls showing what the query actually says, however it was arrived at —
    /// typed, pasted, or clicked.
    /// </summary>
    partial void OnSearchTextChanged(string? value) => ReadFiltersFromQuery();

    /// <summary>True while showing search results rather than a folder's contents.</summary>
    [ObservableProperty]
    private bool _isShowingSearchResults;

    [ObservableProperty]
    private bool _isIndexing;

    [ObservableProperty]
    private double _indexProgress;

    [ObservableProperty]
    private string? _indexStatus;

    [ObservableProperty]
    private string? _searchWarning;

    /// <summary>
    /// Runs the search. Results come from the catalog, so this searches everything ever indexed
    /// rather than only the folder on screen — which is the entire point of having a catalog.
    /// </summary>
    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            ClearSearch();
            return;
        }

        if (_searchCts is { } previous)
        {
            await previous.CancelAsync();
            previous.Dispose();
        }

        var cts = new CancellationTokenSource();
        _searchCts = cts;

        var query = SearchQueryParser.Parse(SearchText);
        SearchWarning = query.UnrecognisedTerms.IsDefaultOrEmpty
            ? null
            : $"Ignored: {string.Join(", ", query.UnrecognisedTerms)}";

        if (query.IsEmpty)
        {
            StatusText = "Enter something to search for.";
            return;
        }

        try
        {
            var scope = SearchEverywhere ? null : WorkspacePath;
            var hits = await _catalog.SearchAsync(query, scope, cancellationToken: cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            await CancelActiveScanAsync();

            MediaItems.Clear();
            SelectedItem = null;
            IsShowingSearchResults = true;

            foreach (var hit in hits)
            {
                MediaItems.Add(new MediaItemViewModel(hit.ToMediaFile(), _thumbnails)
                {
                    HasPendingChanges = _pending.HasChanges(hit.FullPath)
                });
            }

            CurrentFolderPath = $"Search: {SearchText}";

            var where = scope is null ? "everywhere" : WorkspaceName ?? "this workspace";
            StatusText = hits.Count == 0
                ? $"No matches in {where}."
                : $"{hits.Count:N0} match(es) in {where}";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Search failed for {Query}", SearchText);
            StatusText = $"Search failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchText = null;
        SearchWarning = null;
        IsShowingSearchResults = false;

        if (SelectedFolder is { IsPlaceholder: false } folder)
        {
            _ = ScanFolderAsync(folder.FullPath);
            return;
        }

        // Nothing to fall back to. Leaving the hits on screen with an empty search box would
        // present them as a folder listing, and CurrentFolderPath still holds "Search: ..." rather
        // than a real path.
        MediaItems.Clear();
        SelectedItem = null;
        CurrentFolderPath = null;
    }

    [RelayCommand]
    private void CancelIndexing() => _indexCts?.Cancel();

    /// <summary>
    /// Every media file in the workspace, held between offering to index and the answer, so saying
    /// yes does not have to walk the tree again.
    /// </summary>
    private IReadOnlyList<MediaFile> _workspaceFiles = [];

    /// <summary>
    /// Set while the whole workspace is being indexed. Browsing a subfolder must not cancel that
    /// run — the workspace pass already covers those files.
    /// </summary>
    private bool _indexingWorkspace;

    /// <summary>
    /// Set from opening a workspace until the workspace pass has decided what to do. Without it the
    /// initial folder scan would index the top folder, only for the workspace pass to walk the same
    /// files again moments later.
    /// </summary>
    private bool _workspaceIndexPending;

    [ObservableProperty]
    private bool _showIndexPrompt;

    [ObservableProperty]
    private string _indexPromptText = string.Empty;

    /// <summary>
    /// True once indexing has been declined for this workspace, so there is still a way back in
    /// without reopening the folder.
    /// </summary>
    [ObservableProperty]
    private bool _canIndexWorkspace;

    /// <summary>
    /// Walks the whole workspace and decides whether to index it now or ask first. Runs after the
    /// initial scan so the grid fills before any of this starts.
    /// </summary>
    private async Task PrepareWorkspaceIndexAsync(string workspace, CancellationToken cancellationToken)
    {
        ShowIndexPrompt = false;
        CanIndexWorkspace = false;
        _workspaceFiles = [];

        try
        {
            var files = new List<MediaFile>();

            // Always recursive: the Recursive toggle controls what is *shown*, but the workspace is
            // the whole tree, and a search that only covered the top folder would be a poor promise.
            await foreach (var file in _scanner.ScanAsync(
                               workspace,
                               new ScanOptions { Recursive = true },
                               cancellationToken: cancellationToken))
            {
                files.Add(file);
            }

            if (files.Count == 0 || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _workspaceFiles = files;

            var choice = _settings.Current.WorkspaceIndexing.TryGetValue(workspace, out var stored)
                ? stored
                : (bool?)null;

            if (choice == false)
            {
                // Declined before. Stay quiet, but leave the door open.
                CanIndexWorkspace = true;
                return;
            }

            if (choice is null && files.Count > AppSettings.IndexPromptThreshold)
            {
                IndexPromptText =
                    $"{files.Count:N0} files in this workspace. Index them so you can search "
                    + "titles, keywords, ratings and camera details? Browsing works either way.";
                ShowIndexPrompt = true;
                return;
            }

            await IndexWorkspaceAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enumerate the workspace at {Path}", workspace);
        }
        finally
        {
            _workspaceIndexPending = false;
        }
    }

    [RelayCommand]
    private async Task AcceptIndexPromptAsync()
    {
        ShowIndexPrompt = false;

        if (WorkspacePath is { } workspace)
        {
            await _settings.SaveAsync(_settings.Current.WithIndexingChoice(workspace, true));
        }

        await IndexWorkspaceAsync(CancellationToken.None);
    }

    [RelayCommand]
    private async Task DeclineIndexPromptAsync()
    {
        ShowIndexPrompt = false;
        CanIndexWorkspace = true;

        if (WorkspacePath is { } workspace)
        {
            await _settings.SaveAsync(_settings.Current.WithIndexingChoice(workspace, false));
        }
    }

    /// <summary>Indexes every file in the workspace. Also the "changed my mind" entry point.</summary>
    [RelayCommand]
    private async Task IndexWorkspaceAsync(CancellationToken cancellationToken)
    {
        if (_workspaceFiles.Count == 0)
        {
            return;
        }

        ShowIndexPrompt = false;
        CanIndexWorkspace = false;

        if (WorkspacePath is { } workspace)
        {
            await _settings.SaveAsync(_settings.Current.WithIndexingChoice(workspace, true));
        }

        _indexingWorkspace = true;
        try
        {
            await RunIndexAsync(_workspaceFiles, cancellationToken);
        }
        finally
        {
            _indexingWorkspace = false;
        }
    }

    /// <summary>
    /// Indexes the files just scanned, in the background. Indexing reads metadata for every file,
    /// so it must never hold up browsing.
    /// </summary>
    private Task IndexScannedAsync(IReadOnlyList<MediaItemViewModel> items)
    {
        // The workspace pass covers every file beneath the root, so browsing into a subfolder while
        // it is running or pending must not cancel it or duplicate its work. CanIndexWorkspace
        // means indexing was declined for this workspace, which per-folder indexing would override.
        if (items.Count == 0 || _indexingWorkspace || _workspaceIndexPending || CanIndexWorkspace)
        {
            return Task.CompletedTask;
        }

        return RunIndexAsync(items.Select(i => i.File).ToList(), CancellationToken.None);
    }

    private async Task RunIndexAsync(IReadOnlyList<MediaFile> files, CancellationToken cancellationToken)
    {
        if (files.Count == 0)
        {
            return;
        }

        if (_indexCts is { } previous)
        {
            await previous.CancelAsync();
            previous.Dispose();
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _indexCts = cts;

        IsIndexing = true;
        IndexProgress = 0;

        try
        {
            var progress = new Progress<JobProgress>(p =>
            {
                IndexProgress = p.Fraction;
                IndexStatus = $"Indexing {p.Completed:N0} of {p.Total:N0}";
            });

            var result = await _indexer.IndexAsync(files, progress, cts.Token);

            // Distinguishing the two is worth a few words: "0 files" after opening a large
            // workspace reads as a failure, where "all 48,213 already current" reads as fast.
            IndexStatus = result switch
            {
                { Indexed: 0, Skipped: 0 } => null,
                { Indexed: 0 } => $"All {result.Skipped:N0} file(s) already indexed",
                { Skipped: 0 } => $"Indexed {result.Indexed:N0} file(s)",
                _ => $"Indexed {result.Indexed:N0} file(s), {result.Skipped:N0} already current"
            };
        }
        catch (OperationCanceledException)
        {
            // Chunks are committed as they go, so whatever finished is kept and the next run
            // skips it. Say so, rather than implying the work was lost.
            IndexStatus = "Indexing stopped — progress so far is kept";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Indexing failed");
            IndexStatus = null;
        }
        finally
        {
            IsIndexing = false;
        }
    }

    /// <summary>True once more than one file is selected, which swaps the inspector for batch mode.</summary>
    [ObservableProperty]
    private bool _isMultiSelection;

    /// <summary>
    /// Called by the view when the grid selection changes. Multi-selection lives here rather than
    /// in a two-way SelectedItems binding, which Avalonia does not make reliable.
    /// </summary>
    public void UpdateSelection(IReadOnlyList<MediaItemViewModel> items)
    {
        Batch.SetSelection(items);
        IsMultiSelection = items.Count > 1;
    }

    partial void OnRecursiveChanged(bool value)
    {
        if (CurrentFolderPath is { } path)
        {
            _ = ScanFolderAsync(path);
        }
    }

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        if (StorageProvider is null)
        {
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open media folder",
            AllowMultiple = false
        });

        var folder = folders.FirstOrDefault();
        var path = folder?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        await OpenPathAsync(path);
    }

    /// <summary>
    /// Opens <paramref name="path"/> as the workspace: it becomes the only root of the tree, the
    /// scope searches are restricted to, and what the application reopens next launch.
    ///
    /// Replacing the roots rather than adding to them is the point — the previous behaviour left
    /// every folder ever opened in the tree alongside Home, / and the volumes.
    /// </summary>
    public async Task OpenPathAsync(string path)
    {
        if (!Directory.Exists(path))
        {
            StatusText = $"Folder not found: {path}";

            // A workspace that has been moved or unmounted should not keep being offered.
            if (RecentWorkspaces.Remove(path))
            {
                await PersistRecentAsync();
            }

            return;
        }

        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        WorkspacePath = path;
        WorkspaceName = string.IsNullOrEmpty(name) ? path : name;

        FolderRoots.Clear();
        FolderRoots.Add(new FolderNodeViewModel(path, WorkspaceName, _folderBrowser));

        RecentWorkspaces.Remove(path);
        RecentWorkspaces.Insert(0, path);
        while (RecentWorkspaces.Count > AppSettings.MaxRecentWorkspaces)
        {
            RecentWorkspaces.RemoveAt(RecentWorkspaces.Count - 1);
        }

        await _settings.SaveAsync(_settings.Current.WithWorkspace(path));

        _workspaceIndexPending = true;
        await ScanFolderAsync(path);

        // After the scan, so the grid is populated before the tree walk for indexing begins.
        _workspaceIndexCts?.Cancel();
        _workspaceIndexCts?.Dispose();
        _workspaceIndexCts = new CancellationTokenSource();
        var token = _workspaceIndexCts.Token;
        _ = Task.Run(() => PrepareWorkspaceIndexAsync(path, token), token);
    }

    /// <summary>Returns to the no-workspace state, leaving the catalog and cache untouched.</summary>
    [RelayCommand]
    private async Task CloseWorkspaceAsync()
    {
        await CancelActiveScanAsync();

        _workspaceIndexCts?.Cancel();
        _indexCts?.Cancel();
        ShowIndexPrompt = false;
        CanIndexWorkspace = false;
        _workspaceFiles = [];

        WorkspacePath = null;
        WorkspaceName = null;
        FolderRoots.Clear();
        MediaItems.Clear();
        SelectedItem = null;
        SelectedFolder = null;
        CurrentFolderPath = null;
        SearchText = null;
        IsShowingSearchResults = false;

        await _settings.SaveAsync(_settings.Current with { LastWorkspacePath = null });
    }

    private Task PersistRecentAsync()
        => _settings.SaveAsync(_settings.Current with { RecentWorkspaces = RecentWorkspaces.ToList() });

    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();

    private async Task ScanFolderAsync(string path)
    {
        await CancelActiveScanAsync();

        var cts = new CancellationTokenSource();
        _scanCts = cts;

        CurrentFolderPath = path;
        MediaItems.Clear();
        SelectedItem = null;
        IsScanning = true;

        var stopwatch = Stopwatch.StartNew();
        var batch = new List<MediaItemViewModel>(BatchSize);
        var lastFlush = stopwatch.Elapsed;
        var count = 0;

        try
        {
            var options = new ScanOptions { Recursive = Recursive };

            await foreach (var file in _scanner.ScanAsync(path, options, cancellationToken: cts.Token))
            {
                // Re-scanning a folder must not lose the "modified" markers for edits already made.
                batch.Add(new MediaItemViewModel(file, _thumbnails)
                {
                    HasPendingChanges = _pending.HasChanges(file.FullPath)
                });
                count++;

                if (batch.Count >= BatchSize || stopwatch.Elapsed - lastFlush >= BatchInterval)
                {
                    Flush(batch);
                    lastFlush = stopwatch.Elapsed;
                    StatusText = $"Scanning {path} — {count} files";
                }
            }

            Flush(batch);
            StatusText = $"{count} media files in {path} ({stopwatch.Elapsed.TotalSeconds:0.0}s)";

            // Indexing happens after the grid is populated so browsing is never blocked by it.
            _ = IndexScannedAsync(MediaItems.ToList());
        }
        catch (OperationCanceledException)
        {
            Flush(batch);
            StatusText = $"Scan cancelled — {count} files found";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scan of {Folder} failed", path);
            StatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_scanCts, cts))
            {
                IsScanning = false;
                _scanCts = null;
            }

            cts.Dispose();
        }

        void Flush(List<MediaItemViewModel> pending)
        {
            foreach (var item in pending)
            {
                MediaItems.Add(item);
            }

            pending.Clear();
        }
    }

    private async Task CancelActiveScanAsync()
    {
        if (_scanCts is not { } active)
        {
            return;
        }

        await active.CancelAsync();
        _scanCts = null;
    }

    private async Task LoadPreviewAsync(MediaItemViewModel? item)
    {
        if (_previewCts is { } previous)
        {
            await previous.CancelAsync();
            previous.Dispose();
        }

        Preview = null;

        if (item is null)
        {
            _previewCts = null;
            IsPreviewLoading = false;
            return;
        }

        var cts = new CancellationTokenSource();
        _previewCts = cts;
        IsPreviewLoading = true;

        try
        {
            // Interactive: the user selected this file and is waiting, so it must not queue behind
            // the grid's background tile work.
            var bytes = await _thumbnails.GetThumbnailAsync(
                item.File, PreviewEdgePixels, ThumbnailPriority.Interactive, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            if (bytes is not null)
            {
                using var stream = new MemoryStream(bytes);
                Preview = new Bitmap(stream);
            }

            // After the pane has something in it, never before: the cheap preview is what makes
            // browsing feel immediate, and the full decode is only ever an upgrade to it.
            PrepareLoupeSource();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load preview for {File}", item.File.FullPath);
        }
        finally
        {
            if (ReferenceEquals(_previewCts, cts))
            {
                IsPreviewLoading = false;
                _previewCts = null;
                cts.Dispose();
            }
        }
    }
}
