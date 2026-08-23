using Avalonia.Platform.Storage;
using BetterDAM.Core.Interfaces;
using BetterDAM.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace BetterDAM.UI.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    /// <summary>
    /// Limit tiers in megabytes. The small end matters as much as the large: a tight cap is the
    /// quickest way to see the rolling behaviour actually working.
    /// </summary>
    private static readonly int[] LimitChoicesMb = [50, 100, 250, 500, 1024, 2048, 5120, 10240, 20480, 51200];

    private readonly ISettingsService _settings;
    private readonly ICacheMaintenance _maintenance;
    private readonly IRenderCacheMaintenance _renderMaintenance;
    private readonly ICatalog _catalog;
    private readonly IAppPaths _paths;
    private readonly ILogger<SettingsViewModel> _logger;

    public SettingsViewModel(
        ISettingsService settings,
        ICacheMaintenance maintenance,
        IRenderCacheMaintenance renderMaintenance,
        ICatalog catalog,
        KeywordLibraryEditorViewModel keywords,
        IAppPaths paths,
        ILogger<SettingsViewModel> logger)
    {
        _settings = settings;
        _maintenance = maintenance;
        _renderMaintenance = renderMaintenance;
        _catalog = catalog;
        _paths = paths;
        _logger = logger;
        Keywords = keywords;

        var current = settings.Current;
        _isCacheLimited = current.IsCacheLimited;
        _selectedLimitIndex = current.IsCacheLimited
            ? NearestChoice(current.CacheSizeLimitBytes)
            : DefaultChoiceIndex;

        _restrictKeywordsToLibrary = current.RestrictKeywordsToLibrary;
        _renderCacheEnabled = current.RenderCacheEnabled;
        _selectedRenderLimitIndex = current.IsRenderCacheLimited
            ? NearestChoice(current.RenderCacheSizeLimitBytes)
            : DefaultRenderChoiceIndex;

        CachePath = paths.CacheRoot;
        IsUsingDefaultCachePath = string.IsNullOrWhiteSpace(current.CacheDirectoryOverride);
        CatalogPath = paths.CatalogPath;
        IsUsingDefaultCatalogPath = string.IsNullOrWhiteSpace(current.CatalogDirectoryOverride);
    }

    /// <summary>Supplied by the view; the ViewModel does not reach for the window itself.</summary>
    public IStorageProvider? StorageProvider { get; set; }

    /// <summary>The Keywords tab. Its own ViewModel — it shares nothing with cache housekeeping.</summary>
    public KeywordLibraryEditorViewModel Keywords { get; }

    /// <summary>
    /// The open workspace, so importing keywords can be scoped to it. Passed through rather than
    /// looked up: the settings dialog has no business knowing about the main window.
    /// </summary>
    public string? WorkspacePath
    {
        get => Keywords.WorkspacePath;
        set => Keywords.WorkspacePath = value;
    }

    public static IReadOnlyList<string> LimitChoices { get; } =
        LimitChoicesMb.Select(mb => mb < 1024 ? $"{mb} MB" : $"{mb / 1024} GB").ToList();

    [ObservableProperty]
    private string _cachePath = string.Empty;

    [ObservableProperty]
    private bool _isUsingDefaultCachePath;

    [ObservableProperty]
    private string _cacheSizeDisplay = "Calculating…";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isCacheLimited;

    [ObservableProperty]
    private int _selectedLimitIndex;

    /// <summary>Two-step confirmation, so a mis-click cannot throw away a large cache.</summary>
    [ObservableProperty]
    private bool _isConfirmingClear;

    [ObservableProperty]
    private string _catalogPath = string.Empty;

    [ObservableProperty]
    private bool _isUsingDefaultCatalogPath;

    [ObservableProperty]
    private string _catalogSizeDisplay = "Calculating…";

    [ObservableProperty]
    private bool _isConfirmingCatalogClear;

    public string LimitSummary => IsCacheLimited
        ? $"Oldest thumbnails are removed once the cache passes {ByteSize.Format(SelectedLimitBytes)}."
        : "The cache grows without limit.";

    private long SelectedLimitBytes
        => LimitChoicesMb[Math.Clamp(SelectedLimitIndex, 0, LimitChoicesMb.Length - 1)] * 1024L * 1024L;

    /// <summary>2 GB — a sensible default ceiling for a thumbnail cache.</summary>
    private static readonly int DefaultChoiceIndex = Array.IndexOf(LimitChoicesMb, 2048);

    private static int NearestChoice(long bytes)
    {
        var mb = bytes / (1024.0 * 1024.0);
        var best = 0;
        for (var i = 1; i < LimitChoicesMb.Length; i++)
        {
            if (Math.Abs(LimitChoicesMb[i] - mb) < Math.Abs(LimitChoicesMb[best] - mb))
            {
                best = i;
            }
        }

        return best;
    }

    partial void OnIsCacheLimitedChanged(bool value)
    {
        OnPropertyChanged(nameof(LimitSummary));
        _ = ApplyLimitAsync();
    }

    partial void OnSelectedLimitIndexChanged(int value)
    {
        OnPropertyChanged(nameof(LimitSummary));
        _ = ApplyLimitAsync();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var stats = await _maintenance.GetStatisticsAsync().ConfigureAwait(true);
            CacheSizeDisplay = stats.FileCount == 0
                ? "Empty"
                : $"{ByteSize.Format(stats.TotalBytes)} in {stats.FileCount:N0} files";
            CachePath = _paths.CacheRoot;

            var renders = await _renderMaintenance.GetStatisticsAsync().ConfigureAwait(true);
            RenderCacheSizeDisplay = renders.FileCount == 0
                ? "Empty"
                : $"{ByteSize.Format(renders.TotalBytes)} in {renders.FileCount:N0} renditions";

            var catalog = await _catalog.GetStatisticsAsync().ConfigureAwait(true);
            CatalogSizeDisplay = catalog.FileCount == 0
                ? "Empty"
                : $"{catalog.FileCount:N0} files, {catalog.KeywordCount:N0} keywords · {ByteSize.Format(catalog.SizeBytes)}";
            CatalogPath = _paths.CatalogPath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not measure the cache");
            CacheSizeDisplay = "Unavailable";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void BeginClearCache() => IsConfirmingClear = true;

    [RelayCommand]
    private void CancelClearCache() => IsConfirmingClear = false;

    [RelayCommand]
    private async Task ConfirmClearCacheAsync()
    {
        IsConfirmingClear = false;
        IsBusy = true;
        StatusMessage = null;

        try
        {
            var freed = await _maintenance.ClearAsync().ConfigureAwait(true);
            StatusMessage = $"Cleared {ByteSize.Format(freed)}. Thumbnails regenerate as you browse.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear the cache");
            StatusMessage = $"Could not clear the cache: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task ChooseCacheFolderAsync()
    {
        if (StorageProvider is null)
        {
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a cache location",
            AllowMultiple = false
        });

        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        await SaveAsync(_settings.Current with { CacheDirectoryOverride = path }).ConfigureAwait(true);
        StatusMessage = "Cache location changed. Existing thumbnails stay at the old location.";
        IsUsingDefaultCachePath = false;
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task UseDefaultCacheFolderAsync()
    {
        await SaveAsync(_settings.Current with { CacheDirectoryOverride = null }).ConfigureAwait(true);
        IsUsingDefaultCachePath = true;
        StatusMessage = "Using the default cache location.";
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void BeginClearCatalog() => IsConfirmingCatalogClear = true;

    [RelayCommand]
    private void CancelClearCatalog() => IsConfirmingCatalogClear = false;

    [RelayCommand]
    private async Task ConfirmClearCatalogAsync()
    {
        IsConfirmingCatalogClear = false;
        IsBusy = true;
        StatusMessage = null;

        try
        {
            await _catalog.ClearAsync().ConfigureAwait(true);
            StatusMessage = "Catalog cleared. Folders are re-indexed as you browse them.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear the catalog");
            StatusMessage = $"Could not clear the catalog: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Drops entries whose files are gone. Cheaper and far less disruptive than clearing when the
    /// catalog has simply accumulated references to files that were moved or deleted.
    /// </summary>
    [RelayCommand]
    private async Task PruneCatalogAsync()
    {
        IsBusy = true;
        StatusMessage = null;

        try
        {
            var removed = await _catalog.RemoveMissingAsync().ConfigureAwait(true);
            StatusMessage = removed == 0
                ? "Every catalog entry still points at a file that exists."
                : $"Removed {removed:N0} entr{(removed == 1 ? "y" : "ies")} for files that no longer exist.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to prune the catalog");
            StatusMessage = $"Could not prune the catalog: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task ChooseCatalogFolderAsync()
    {
        if (StorageProvider is null)
        {
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a catalog location",
            AllowMultiple = false
        });

        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        await SaveAsync(_settings.Current with { CatalogDirectoryOverride = path }).ConfigureAwait(true);
        IsUsingDefaultCatalogPath = false;
        StatusMessage = "Catalog location changed. A new, empty catalog is created there — the old one stays where it was.";
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task UseDefaultCatalogFolderAsync()
    {
        await SaveAsync(_settings.Current with { CatalogDirectoryOverride = null }).ConfigureAwait(true);
        IsUsingDefaultCatalogPath = true;
        StatusMessage = "Using the default catalog location.";
        await RefreshAsync().ConfigureAwait(true);
    }

    // ---- Keywords -----------------------------------------------------------------------------

    /// <summary>
    /// Whether keywords may only come from the library. Applied immediately — the metadata panel
    /// reads the setting rather than being told about it.
    /// </summary>
    [ObservableProperty]
    private bool _restrictKeywordsToLibrary;

    partial void OnRestrictKeywordsToLibraryChanged(bool value)
    {
        if (_settings.Current.RestrictKeywordsToLibrary != value)
        {
            _ = SaveAsync(_settings.Current with { RestrictKeywordsToLibrary = value });
        }
    }

    // ---- Render cache -------------------------------------------------------------------------

    /// <summary>
    /// Whether developed RAW files are kept on disk. Off by choice for anyone short of space: a
    /// rendition is tens of megabytes against a thumbnail's tens of kilobytes.
    /// </summary>
    [ObservableProperty]
    private bool _renderCacheEnabled;

    [ObservableProperty]
    private int _selectedRenderLimitIndex;

    [ObservableProperty]
    private string _renderCacheSizeDisplay = "—";

    [ObservableProperty]
    private bool _isConfirmingRenderClear;

    /// <summary>10 GB — roughly fifteen hundred 26MP renditions, a working set rather than a library.</summary>
    private static readonly int DefaultRenderChoiceIndex = Array.IndexOf(LimitChoicesMb, 10240);

    private long SelectedRenderLimitBytes
        => LimitChoicesMb[Math.Clamp(SelectedRenderLimitIndex, 0, LimitChoicesMb.Length - 1)] * 1024L * 1024L;

    public string RenderLimitSummary => RenderCacheEnabled
        ? $"Least recently opened renditions are removed once they pass {ByteSize.Format(SelectedRenderLimitBytes)}."
        : "RAW files are developed afresh every time they are opened.";

    partial void OnRenderCacheEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(RenderLimitSummary));
        _ = ApplyRenderCacheAsync(clearWhenDisabled: true);
    }

    partial void OnSelectedRenderLimitIndexChanged(int value)
    {
        OnPropertyChanged(nameof(RenderLimitSummary));
        _ = ApplyRenderCacheAsync(clearWhenDisabled: false);
    }

    private async Task ApplyRenderCacheAsync(bool clearWhenDisabled)
    {
        var limit = SelectedRenderLimitBytes;

        if (_settings.Current.RenderCacheEnabled == RenderCacheEnabled &&
            _settings.Current.RenderCacheSizeLimitBytes == limit)
        {
            return;
        }

        await SaveAsync(_settings.Current with
        {
            RenderCacheEnabled = RenderCacheEnabled,
            RenderCacheSizeLimitBytes = limit
        }).ConfigureAwait(true);

        // Turning it off is a request to stop spending disk, so the space comes back now rather than
        // sitting there unused. Trimming handles both cases: it empties a disabled cache.
        if (clearWhenDisabled || RenderCacheEnabled)
        {
            var freed = await _renderMaintenance.TrimAsync().ConfigureAwait(true);
            if (freed > 0)
            {
                StatusMessage = $"Released {ByteSize.Format(freed)} of developed RAW files.";
            }
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void BeginClearRenderCache() => IsConfirmingRenderClear = true;

    [RelayCommand]
    private void CancelClearRenderCache() => IsConfirmingRenderClear = false;

    [RelayCommand]
    private async Task ConfirmClearRenderCacheAsync()
    {
        IsConfirmingRenderClear = false;
        IsBusy = true;
        StatusMessage = null;

        try
        {
            var freed = await _renderMaintenance.ClearAsync().ConfigureAwait(true);
            StatusMessage = $"Released {ByteSize.Format(freed)}. RAW files develop again on first view.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear the render cache");
            StatusMessage = $"Could not clear the render cache: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    private async Task ApplyLimitAsync()
    {
        var limit = IsCacheLimited ? SelectedLimitBytes : AppSettings.UnlimitedCache;
        if (_settings.Current.CacheSizeLimitBytes == limit)
        {
            return;
        }

        await SaveAsync(_settings.Current with { CacheSizeLimitBytes = limit }).ConfigureAwait(true);

        if (!IsCacheLimited)
        {
            return;
        }

        // Applying a limit should take effect now, not at some later write.
        var freed = await _maintenance.TrimAsync().ConfigureAwait(true);
        if (freed > 0)
        {
            StatusMessage = $"Trimmed {ByteSize.Format(freed)} to fit the new limit.";
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task SaveAsync(AppSettings settings)
    {
        try
        {
            await _settings.SaveAsync(settings).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings");
            StatusMessage = $"Could not save settings: {ex.Message}";
        }
    }
}
