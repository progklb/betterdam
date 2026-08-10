namespace BetterDAM.Metadata.Xmp;

/// <summary>
/// Locates the XMP sidecar for a media file.
///
/// Two conventions are in the wild and both must be recognised:
///   IMG001.CR3 → IMG001.xmp      (Adobe's convention — replaces the extension)
///   IMG001.CR3 → IMG001.CR3.xmp  (used by some tools — appends to the full name)
/// The Adobe form is preferred when creating new sidecars, but either is read.
/// </summary>
public static class XmpSidecar
{
    /// <summary>The path this application uses when it creates a sidecar.</summary>
    public static string GetPreferredPath(string mediaPath)
        => Path.ChangeExtension(mediaPath, ".xmp");

    /// <summary>Both recognised sidecar locations, most-preferred first.</summary>
    public static IEnumerable<string> GetCandidatePaths(string mediaPath)
    {
        yield return GetPreferredPath(mediaPath);
        yield return mediaPath + ".xmp";
    }

    /// <summary>The existing sidecar for a media file, or null when there is none.</summary>
    public static string? Find(string mediaPath)
    {
        foreach (var candidate in GetCandidatePaths(mediaPath))
        {
            // A file whose own extension is .xmp would otherwise "find itself".
            if (string.Equals(candidate, mediaPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
