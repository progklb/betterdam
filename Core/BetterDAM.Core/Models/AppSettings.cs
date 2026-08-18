namespace BetterDAM.Core.Models;

/// <summary>
/// User preferences. Persisted outside the cache directory — settings must survive "Clear cache".
/// </summary>
public sealed record AppSettings
{
    public const long UnlimitedCache = 0;

    public static readonly AppSettings Default = new();

    /// <summary>
    /// Where derived data is written. Null means the platform default. Useful when the media lives
    /// on an external drive and the boot disk is small.
    /// </summary>
    public string? CacheDirectoryOverride { get; init; }

    /// <summary>
    /// Where the search catalog lives. Null means alongside the other application data. Useful when
    /// the library is large and the boot disk is not.
    /// </summary>
    public string? CatalogDirectoryOverride { get; init; }

    /// <summary>
    /// Size ceiling for the thumbnail cache in bytes. <see cref="UnlimitedCache"/> disables trimming.
    /// When exceeded, the least recently used entries are evicted — the cache is disposable, so
    /// discarding old entries only costs regenerating them if they are needed again.
    /// </summary>
    public long CacheSizeLimitBytes { get; init; } = UnlimitedCache;

    public bool IsCacheLimited => CacheSizeLimitBytes > UnlimitedCache;

    /// <summary>
    /// The workspace open when the application last closed, reopened on launch so browsing picks up
    /// where it left off. Null on a first run, or after the folder is closed.
    /// </summary>
    public string? LastWorkspacePath { get; init; }

    /// <summary>
    /// Recently opened workspaces, most recent first, for the Open Recent menu.
    /// </summary>
    public IReadOnlyList<string> RecentWorkspaces { get; init; } = [];

    /// <summary>
    /// Whether the viewer takes over the whole screen or just fills the current one.
    ///
    /// Two different things on macOS: real fullscreen hides the menu bar but moves the window to a
    /// Space of its own, which is heavy-handed for a quick look at a photo. Maximised stays put.
    /// </summary>
    public bool ViewerOpensFullscreen { get; init; }

    /// <summary>
    /// Whether RAW files are developed for viewing, or shown from the JPEG the camera embedded.
    ///
    /// Developing demosaics the sensor data: more pixels than the preview and no in-camera
    /// processing, at the cost of several seconds per image. The preview is instant and is what the
    /// camera thought the picture should look like.
    /// </summary>
    public bool DevelopRawFiles { get; init; } = true;

    /// <summary>Beyond this many entries, Open Recent stops being a shortcut and becomes a list.</summary>
    public const int MaxRecentWorkspaces = 10;

    /// <summary>
    /// Above this many files, indexing a workspace is offered rather than simply done. Below it the
    /// work is short enough that asking would be more disruptive than the indexing.
    /// </summary>
    public const int IndexPromptThreshold = 5000;

    /// <summary>
    /// Whether each workspace should be indexed, keyed by path — only recorded for workspaces large
    /// enough to have been asked about. Per workspace so the question is answered once, not on
    /// every open.
    /// </summary>
    public IReadOnlyDictionary<string, bool> WorkspaceIndexing { get; init; }
        = new Dictionary<string, bool>();

    public AppSettings WithIndexingChoice(string workspace, bool index)
    {
        var choices = new Dictionary<string, bool>(WorkspaceIndexing, StringComparer.Ordinal)
        {
            [workspace] = index
        };

        return this with { WorkspaceIndexing = choices };
    }

    /// <summary>
    /// Records <paramref name="path"/> as the current workspace and moves it to the front of the
    /// recent list, de-duplicating so reopening the same folder does not fill the menu with it.
    /// </summary>
    public AppSettings WithWorkspace(string path)
    {
        var recent = new List<string> { path };
        recent.AddRange(RecentWorkspaces.Where(p => !string.Equals(p, path, StringComparison.Ordinal)));

        return this with
        {
            LastWorkspacePath = path,
            RecentWorkspaces = recent.Take(MaxRecentWorkspaces).ToList()
        };
    }
}
