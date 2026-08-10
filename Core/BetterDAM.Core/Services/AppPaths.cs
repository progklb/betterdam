using BetterDAM.Core.Interfaces;

namespace BetterDAM.Core.Services;

public sealed class AppPaths : IAppPaths
{
    public AppPaths()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(baseDir))
        {
            baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }

        CacheRoot = Path.Combine(baseDir, "BetterDAM", "Cache");
        ThumbnailCacheRoot = Path.Combine(CacheRoot, "Thumbnails");
        LogRoot = Path.Combine(CacheRoot, "Logs");

        Directory.CreateDirectory(ThumbnailCacheRoot);
        Directory.CreateDirectory(LogRoot);
    }

    public string CacheRoot { get; }

    public string ThumbnailCacheRoot { get; }

    public string LogRoot { get; }
}
