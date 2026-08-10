using BetterDAM.Core.Interfaces;
using BetterDAM.Core.Services;
using BetterDAM.Metadata.ExifTool;
using BetterDAM.Metadata.Xmp;
using BetterDAM.Preview;
using BetterDAM.Preview.Cache;
using BetterDAM.Preview.Images;
using BetterDAM.Preview.Video;
using BetterDAM.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace BetterDAM.UI.Services;

internal static class ServiceCollectionExtensions
{
    public static IServiceCollection AddBetterDam(this IServiceCollection services, IAppPaths paths)
    {
        services.AddSingleton(paths);
        services.AddLogging(builder => builder.AddSerilog(Log.Logger, dispose: false));

        services.AddSingleton<IMediaScanner, MediaScanner>();
        services.AddSingleton<IFolderBrowser, FolderBrowser>();
        services.AddSingleton<IFfmpegLocator, FfmpegLocator>();

        services.AddSingleton<ThumbnailCache>();
        services.AddSingleton<IThumbnailGenerator, SkiaImageThumbnailGenerator>();
        services.AddSingleton<IThumbnailGenerator, FfmpegVideoThumbnailGenerator>();
        services.AddSingleton<IThumbnailService, ThumbnailService>();

        services.AddSingleton<IExifToolLocator, ExifToolLocator>();
        // One ExifTool process, shared by the reader and the writer.
        services.AddSingleton<ExifToolHost>();
        services.AddSingleton<IMetadataProvider, ExifToolMetadataProvider>();
        services.AddSingleton<IMetadataWriter, ExifToolSidecarWriter>();
        services.AddSingleton<IPendingChangeStore, PendingChangeStore>();

        services.AddTransient<MetadataInspectorViewModel>();
        services.AddTransient<MainWindowViewModel>();

        return services;
    }
}
