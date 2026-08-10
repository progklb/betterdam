using BetterDAM.Core.Models;

namespace BetterDAM.Core.Interfaces;

public sealed record SidecarWriteOptions
{
    /// <summary>
    /// Read the sidecar back after writing and confirm it says what we asked for. Cheap next to the
    /// write itself, and it is the difference between "we ran a command" and "the data is there".
    /// </summary>
    public bool ValidateAfterWrite { get; init; } = true;
}

public sealed record SidecarWriteResult(
    string FilePath,
    bool Success,
    string? SidecarPath = null,
    string? Error = null)
{
    public static SidecarWriteResult Failed(string filePath, string error)
        => new(filePath, false, Error: error);
}

/// <summary>
/// Writes user-editable metadata to an XMP sidecar.
///
/// The contract that matters: <b>this never modifies the media file.</b> Ordinary metadata editing
/// writes only to <c>&lt;name&gt;.xmp</c>, leaving the original bytes and its modification time
/// untouched. Embedding into the media file is a separate, explicit operation (Phase 6 Sync).
///
/// Updating an existing sidecar preserves tags this application does not understand — only the
/// fields it manages are touched.
/// </summary>
public interface IMetadataWriter
{
    bool IsAvailable { get; }

    Task<SidecarWriteResult> WriteSidecarAsync(
        MediaFile file,
        EditableMetadata metadata,
        SidecarWriteOptions options,
        CancellationToken cancellationToken = default);
}
