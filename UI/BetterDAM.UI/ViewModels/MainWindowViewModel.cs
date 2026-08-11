using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using BetterDAM.Core.Interfaces;
using BetterDAM.Core.Models;
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
    private readonly IFfmpegLocator _ffmpeg;
    private readonly IPendingChangeStore _pending;
    private readonly IMetadataWriter _writer;
    private readonly ILogger<MainWindowViewModel> _logger;

    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _previewCts;

    public MainWindowViewModel(
        IMediaScanner scanner,
        IFolderBrowser folderBrowser,
        IThumbnailService thumbnails,
        IFfmpegLocator ffmpeg,
        IPendingChangeStore pending,
        IMetadataWriter writer,
        MetadataInspectorViewModel inspector,
        VideoPlayerViewModel player,
        BatchEditViewModel batch,
        ILogger<MainWindowViewModel> logger)
    {
        _scanner = scanner;
        _folderBrowser = folderBrowser;
        _thumbnails = thumbnails;
        _ffmpeg = ffmpeg;
        _pending = pending;
        _writer = writer;
        _logger = logger;
        Inspector = inspector;
        Player = player;
        Batch = batch;

        // A batch run marks many files at once; refresh whatever is on screen afterwards.
        Batch.Applied += () => PendingChangeCount = _pending.Count;

        _pending.Changed += (_, _) => PendingChangeCount = _pending.Count;

        foreach (var root in _folderBrowser.GetRoots())
        {
            FolderRoots.Add(new FolderNodeViewModel(root.FullPath, root.Name, _folderBrowser));
        }

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
    private Bitmap? _preview;

    [ObservableProperty]
    private bool _isPreviewLoading;

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

        // A video is handed to the player; only stills use the static image preview, so the two
        // never fight over the same pane.
        _ = Player.LoadAsync(IsVideoSelected ? value!.File : null);
        _ = LoadPreviewAsync(IsVideoSelected ? null : value);
        _ = Inspector.LoadAsync(value);
    }

    [ObservableProperty]
    private bool _isVideoSelected;

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
    /// Adds <paramref name="path"/> as a tree root and scans it. Used by the folder picker and by
    /// the optional folder argument passed on the command line.
    /// </summary>
    public async Task OpenPathAsync(string path)
    {
        if (!Directory.Exists(path))
        {
            StatusText = $"Folder not found: {path}";
            return;
        }

        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        FolderRoots.Insert(0, new FolderNodeViewModel(path, string.IsNullOrEmpty(name) ? path : name, _folderBrowser));
        await ScanFolderAsync(path);
    }

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
