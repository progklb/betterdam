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
    private readonly IAppPaths _paths;
    private readonly ILogger<SettingsViewModel> _logger;

    public SettingsViewModel(
        ISettingsService settings,
        ICacheMaintenance maintenance,
        IAppPaths paths,
        ILogger<SettingsViewModel> logger)
    {
        _settings = settings;
        _maintenance = maintenance;
        _paths = paths;
        _logger = logger;

        var current = settings.Current;
        _isCacheLimited = current.IsCacheLimited;
        _selectedLimitIndex = current.IsCacheLimited
            ? NearestChoice(current.CacheSizeLimitBytes)
            : DefaultChoiceIndex;

        CachePath = paths.CacheRoot;
        IsUsingDefaultCachePath = string.IsNullOrWhiteSpace(current.CacheDirectoryOverride);
    }

    /// <summary>Supplied by the view; the ViewModel does not reach for the window itself.</summary>
    public IStorageProvider? StorageProvider { get; set; }

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
