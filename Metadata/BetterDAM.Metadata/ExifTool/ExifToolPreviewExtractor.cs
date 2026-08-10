using System.Diagnostics;
using BetterDAM.Core.Interfaces;
using BetterDAM.Core.Models;
using Microsoft.Extensions.Logging;

namespace BetterDAM.Metadata.ExifTool;

/// <summary>
/// Extracts the embedded preview with a one-shot ExifTool process.
///
/// This deliberately does not use the shared <c>-stay_open</c> session: that session reads stdout as
/// <b>text</b>, line by line, looking for the <c>{ready}</c> marker. Pushing JPEG bytes through it
/// would corrupt them. A separate process gives a clean binary stdout, and the cost is paid once per
/// file because the result is cached as a thumbnail.
///
/// The file is only ever read.
/// </summary>
public sealed class ExifToolPreviewExtractor : IEmbeddedPreviewExtractor
{
    private static readonly TimeSpan ExtractionTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Preview tags in descending order of usefulness. <c>PreviewImage</c> is typically a
    /// full-resolution JPEG; <c>ThumbnailImage</c> is a last resort at around 160px.
    /// </summary>
    private static readonly string[] PreviewTags =
    [
        "-PreviewImage",
        "-JpgFromRaw",
        "-OtherImage",
        "-ThumbnailImage"
    ];

    private readonly IExifToolLocator _locator;
    private readonly ILogger<ExifToolPreviewExtractor> _logger;

    public ExifToolPreviewExtractor(IExifToolLocator locator, ILogger<ExifToolPreviewExtractor> logger)
    {
        _locator = locator;
        _logger = logger;
    }

    public bool IsAvailable => _locator.IsAvailable;

    public async Task<byte[]?> ExtractAsync(MediaFile file, CancellationToken cancellationToken = default)
    {
        if (_locator.ExifToolPath is not { } exifTool)
        {
            return null;
        }

        foreach (var tag in PreviewTags)
        {
            var preview = await TryExtractAsync(exifTool, tag, file, cancellationToken).ConfigureAwait(false);
            if (preview is not null)
            {
                return preview;
            }
        }

        _logger.LogDebug("No embedded preview found in {File}", file.FullPath);
        return null;
    }

    private async Task<byte[]?> TryExtractAsync(
        string exifTool,
        string tag,
        MediaFile file,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = exifTool,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-b");
        startInfo.ArgumentList.Add(tag);
        startInfo.ArgumentList.Add(file.FullPath);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ExtractionTimeout);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return null;
        }

        try
        {
            using var buffer = new MemoryStream();
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);

            await process.StandardOutput.BaseStream.CopyToAsync(buffer, timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            if (buffer.Length == 0)
            {
                return null;
            }

            var bytes = buffer.ToArray();
            return LooksLikeJpeg(bytes) ? bytes : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("ExifTool timed out extracting {Tag} from {File}", tag, file.FullPath);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ExifTool failed extracting {Tag} from {File}", tag, file.FullPath);
            return null;
        }
        finally
        {
            KillIfRunning(process);
        }
    }

    /// <summary>
    /// Guards against ExifTool writing a diagnostic to stdout instead of image data — decoding
    /// that would fail later and much less clearly.
    /// </summary>
    private static bool LooksLikeJpeg(byte[] bytes)
        => bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;

    private void KillIfRunning(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or SystemException)
        {
            _logger.LogDebug(ex, "Unable to terminate the ExifTool process");
        }
    }
}
