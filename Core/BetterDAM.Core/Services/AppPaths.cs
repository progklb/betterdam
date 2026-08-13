using BetterDAM.Core.Interfaces;

namespace BetterDAM.Core.Services;

/// <summary>
/// Application-owned locations.
///
/// Layout:
/// <code>
/// &lt;LocalAppData&gt;/BetterDAM/
///     settings.json          preferences — never inside Cache
///     catalog.db             SQLite search catalog — never inside Cache
///     Logs/                  diagnostics — never inside Cache
///     Cache/Thumbnails/      disposable derived data (relocatable)
///     Cache/VideoProxy/      generated low-resolution video
/// </code>
///
/// Logs and settings sit <b>outside</b> Cache so that clearing or relocating the cache cannot take
/// them with it.
/// </summary>
public sealed class AppPaths : IAppPaths
{
    private readonly ISettingsService? _settings;

    public AppPaths()
        : this(null)
    {
    }

    public AppPaths(ISettingsService? settings)
    {
        _settings = settings;
        AppDataRoot = GetAppDataRoot();
        LogRoot = Path.Combine(AppDataRoot, "Logs");

        Directory.CreateDirectory(LogRoot);
        Directory.CreateDirectory(ThumbnailCacheRoot);
        Directory.CreateDirectory(VideoProxyCacheRoot);
    }

    public string AppDataRoot { get; }

    public string LogRoot { get; }

    /// <summary>Honours the user's override, so the value tracks settings without a restart.</summary>
    public string CacheRoot
    {
        get
        {
            var over = _settings?.Current.CacheDirectoryOverride;
            return string.IsNullOrWhiteSpace(over) ? DefaultCacheRoot : over;
        }
    }

    public string DefaultCacheRoot => Path.Combine(AppDataRoot, "Cache");

    public string CatalogPath => Path.Combine(AppDataRoot, "catalog.db");

    public string ThumbnailCacheRoot => Path.Combine(CacheRoot, "Thumbnails");

    public string VideoProxyCacheRoot => Path.Combine(CacheRoot, "VideoProxy");

    public static string GetAppDataRoot()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(baseDir))
        {
            baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }

        return Path.Combine(baseDir, "BetterDAM");
    }
}
