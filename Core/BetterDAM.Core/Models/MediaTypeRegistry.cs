namespace BetterDAM.Core.Models;

/// <summary>
/// Maps file extensions to <see cref="MediaType"/>. This is a starting set covering the formats
/// listed in the project README; the format list is expected to grow as ExifTool/FFmpeg coverage
/// is wired in rather than being treated as a hard application limit.
/// </summary>
public static class MediaTypeRegistry
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".tif", ".tiff",
        ".dng", ".cr2", ".cr3", ".nef", ".arw", ".raf", ".orf", ".rw2", ".srw", ".pef"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".avi", ".mxf", ".m4v"
    };

    public static MediaType GetMediaType(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        if (ImageExtensions.Contains(extension))
        {
            return MediaType.Image;
        }

        if (VideoExtensions.Contains(extension))
        {
            return MediaType.Video;
        }

        return MediaType.Unsupported;
    }

    public static bool IsSupported(string filePath) => GetMediaType(filePath) != MediaType.Unsupported;
}
