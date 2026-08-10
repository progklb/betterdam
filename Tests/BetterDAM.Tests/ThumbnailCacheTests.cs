using BetterDAM.Core.Interfaces;
using BetterDAM.Core.Models;
using BetterDAM.Preview.Cache;
using Xunit;

namespace BetterDAM.Tests;

public class ThumbnailCacheTests
{
    private sealed class TestPaths : IAppPaths
    {
        public TestPaths(string root)
        {
            CacheRoot = root;
            ThumbnailCacheRoot = Path.Combine(root, "Thumbnails");
            LogRoot = Path.Combine(root, "Logs");
            Directory.CreateDirectory(ThumbnailCacheRoot);
        }

        public string CacheRoot { get; }

        public string ThumbnailCacheRoot { get; }

        public string LogRoot { get; }
    }

    private static MediaFile Sample(string path = "/library/IMG001.jpg", long size = 1234, long ticks = 5_000_000)
        => new()
        {
            FullPath = path,
            FileName = Path.GetFileName(path),
            MediaType = MediaType.Image,
            SizeBytes = size,
            ModifiedUtc = new DateTimeOffset(ticks, TimeSpan.Zero),
            CreatedUtc = new DateTimeOffset(ticks, TimeSpan.Zero)
        };

    [Fact]
    public void Cache_key_is_stable_for_identical_input()
    {
        using var temp = new TempFolder();
        var cache = new ThumbnailCache(new TestPaths(temp.Path));

        Assert.Equal(cache.GetCacheKey(Sample(), 320), cache.GetCacheKey(Sample(), 320));
    }

    [Fact]
    public void Cache_key_changes_when_the_source_file_changes()
    {
        using var temp = new TempFolder();
        var cache = new ThumbnailCache(new TestPaths(temp.Path));
        var original = cache.GetCacheKey(Sample(), 320);

        Assert.NotEqual(original, cache.GetCacheKey(Sample(size: 9999), 320));
        Assert.NotEqual(original, cache.GetCacheKey(Sample(ticks: 6_000_000), 320));
        Assert.NotEqual(original, cache.GetCacheKey(Sample(path: "/library/IMG002.jpg"), 320));
    }

    [Fact]
    public void Cache_key_changes_with_requested_size()
    {
        using var temp = new TempFolder();
        var cache = new ThumbnailCache(new TestPaths(temp.Path));

        Assert.NotEqual(cache.GetCacheKey(Sample(), 320), cache.GetCacheKey(Sample(), 1600));
    }

    [Fact]
    public async Task Round_trips_thumbnail_bytes()
    {
        using var temp = new TempFolder();
        var cache = new ThumbnailCache(new TestPaths(temp.Path));
        var key = cache.GetCacheKey(Sample(), 320);
        var payload = new byte[] { 1, 2, 3, 4, 5 };

        Assert.Null(await cache.TryReadAsync(key, CancellationToken.None));

        await cache.WriteAsync(key, payload, CancellationToken.None);

        Assert.Equal(payload, await cache.TryReadAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task Write_leaves_no_temporary_files_behind()
    {
        using var temp = new TempFolder();
        var paths = new TestPaths(temp.Path);
        var cache = new ThumbnailCache(paths);

        await cache.WriteAsync(cache.GetCacheKey(Sample(), 320), [9, 9, 9], CancellationToken.None);

        var leftovers = Directory.GetFiles(paths.ThumbnailCacheRoot, "*.tmp", SearchOption.AllDirectories);
        Assert.Empty(leftovers);
    }
}
