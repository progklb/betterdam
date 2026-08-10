using BetterDAM.Core.Interfaces;

namespace BetterDAM.Tests;

/// <summary>An <see cref="IAppPaths"/> rooted in a temp folder, shared by the cache tests.</summary>
internal sealed class TestPaths : IAppPaths
{
    public TestPaths(string root)
    {
        AppDataRoot = root;
        DefaultCacheRoot = Path.Combine(root, "Cache");
        CacheRoot = DefaultCacheRoot;
        ThumbnailCacheRoot = Path.Combine(CacheRoot, "Thumbnails");
        LogRoot = Path.Combine(root, "Logs");

        Directory.CreateDirectory(ThumbnailCacheRoot);
        Directory.CreateDirectory(LogRoot);
    }

    public string AppDataRoot { get; }

    public string CacheRoot { get; }

    public string DefaultCacheRoot { get; }

    public string ThumbnailCacheRoot { get; }

    public string LogRoot { get; }
}
