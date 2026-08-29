using BetterDAM.Core.Interfaces;
using BetterDAM.Core.Models;
using BetterDAM.Metadata.ExifTool;
using BetterDAM.Metadata.Xmp;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace BetterDAM.Tests;

/// <summary>
/// Cull flags, against a real ExifTool.
///
/// These run the actual round trip rather than asserting on argument lists, because the whole value
/// of the feature is that another application can read what this one writes. An assertion about the
/// arguments would have passed just as happily while ExifTool refused the value — which is exactly
/// what <c>XMP-photomech:Tagged</c> does without its <c>#</c> suffix.
/// </summary>
public class MediaFlagTests : IAsyncDisposable
{
    private readonly TempFolder _temp = new();
    private readonly ExifToolHost _host;
    private readonly ExifToolMetadataProvider _reader;
    private readonly ExifToolSidecarWriter _writer;

    public MediaFlagTests()
    {
        _host = new ExifToolHost(new RealExifTool.Locator(), NullLogger<ExifToolHost>.Instance);
        _reader = new ExifToolMetadataProvider(_host, NullLogger<ExifToolMetadataProvider>.Instance);
        _writer = new ExifToolSidecarWriter(_host, _reader, NullLogger<ExifToolSidecarWriter>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _temp.Dispose();
        GC.SuppressFinalize(this);
    }

    private MediaFile CreateJpeg(string name)
    {
        var path = Path.Combine(_temp.Path, name);

        using (var bitmap = new SKBitmap(32, 24))
        using (var image = SKImage.FromBitmap(bitmap))
        using (var data = image.Encode(SKEncodedImageFormat.Jpeg, 80))
        using (var stream = File.OpenWrite(path))
        {
            data.SaveTo(stream);
        }

        return MediaFile.FromFileInfo(new FileInfo(path));
    }

    private async Task<EditableMetadata> RoundTrip(string name, EditableMetadata metadata)
    {
        var file = CreateJpeg(name);

        var result = await _writer.WriteSidecarAsync(file, metadata, new SidecarWriteOptions());
        Assert.True(result.Success, result.Error);

        var read = await _reader.ReadAsync(file);
        Assert.NotNull(read);

        return read.Effective;
    }

    [Fact]
    public async Task An_accepted_flag_survives_a_round_trip()
    {
        if (!RealExifTool.IsAvailable)
        {
            return;
        }

        var effective = await RoundTrip("pick.jpg", new EditableMetadata { Flag = MediaFlag.Accepted, Rating = 4 });

        Assert.Equal(MediaFlag.Accepted, effective.Flag);

        // Accepting says nothing about the stars, so they are left alone.
        Assert.Equal(4, effective.Rating);
    }

    /// <summary>
    /// Rejecting has to take the rating over, because Adobe expresses rejection <i>as</i> a rating of
    /// -1. The stars are not representable alongside it, and the reader must not read that -1 back as
    /// a real rating.
    /// </summary>
    [Fact]
    public async Task A_rejected_flag_becomes_a_negative_rating_and_reads_back_as_rejected()
    {
        if (!RealExifTool.IsAvailable)
        {
            return;
        }

        var effective = await RoundTrip("reject.jpg", new EditableMetadata { Flag = MediaFlag.Rejected, Rating = 4 });

        Assert.Equal(MediaFlag.Rejected, effective.Flag);
        Assert.Null(effective.Rating);
    }

    [Fact]
    public async Task No_flag_writes_no_flag()
    {
        if (!RealExifTool.IsAvailable)
        {
            return;
        }

        var effective = await RoundTrip("none.jpg", new EditableMetadata { Rating = 3 });

        Assert.Null(effective.Flag);
        Assert.Equal(3, effective.Rating);
    }

    /// <summary>
    /// A file rejected in Bridge carries only a rating of -1. Before this, the rating was clamped to
    /// zero — which lost the rejection and invented a nought-star rating nobody had given.
    /// </summary>
    [Fact]
    public async Task A_rejection_made_elsewhere_is_understood()
    {
        if (!RealExifTool.IsAvailable)
        {
            return;
        }

        var file = CreateJpeg("bridge.jpg");
        var sidecar = Path.Combine(_temp.Path, "bridge.xmp");

        // Written the way another application would, with nothing but Adobe's convention in it —
        // driving ExifTool directly rather than through this application's own writer.
        using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = RealExifTool.Path!,
            ArgumentList = { "-o", sidecar, "-XMP:Rating=-1", file.FullPath },
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!)
        {
            await process.WaitForExitAsync();
        }

        Assert.True(File.Exists(sidecar));

        var read = await _reader.ReadAsync(file);

        Assert.NotNull(read);
        Assert.Equal(MediaFlag.Rejected, read.Effective.Flag);
        Assert.Null(read.Effective.Rating);
    }
}
