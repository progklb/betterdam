namespace BetterDAM.Core.Interfaces;

/// <summary>
/// Locations for application-owned, disposable data. Everything under <see cref="CacheRoot"/> must
/// be safe to delete: the application rebuilds it from the original media.
/// </summary>
public interface IAppPaths
{
    string CacheRoot { get; }

    string ThumbnailCacheRoot { get; }

    string LogRoot { get; }
}
