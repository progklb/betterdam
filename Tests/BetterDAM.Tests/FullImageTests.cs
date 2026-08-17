using BetterDAM.Core.Interfaces;
using BetterDAM.Core.Models;
using BetterDAM.Preview.Images;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace BetterDAM.Tests;

public class FullImageDecoderTests
{
    private sealed class NoPreviews : IEmbeddedPreviewExtractor
    {
        public bool IsAvailable => false;

        public Task<byte[]?> ExtractAsync(MediaFile file, CancellationToken cancellationToken = default)
            => Task.FromResult<byte[]?>(null);
    }

    private static SkiaFullImageDecoder Create()
        => new(new NoPreviews(), NullLogger<SkiaFullImageDecoder>.Instance);

    private static MediaFile Write(TempFolder temp, string name, int width, int height,
                                   SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.CornflowerBlue);
            using var paint = new SKPaint { Color = SKColors.Orange };
            canvas.DrawRect(0, 0, width / 2f, height / 2f, paint);
        }

        var path = Path.Combine(temp.Path, name);
        using (var stream = File.Create(path))
        using (var image = SKImage.FromBitmap(bitmap))
        using (var data = image.Encode(format, 95))
        {
            data.SaveTo(stream);
        }

        var info = new FileInfo(path);
        return new MediaFile
        {
            FullPath = path,
            FileName = name,
            MediaType = MediaType.Image,
            SizeBytes = info.Length,
            ModifiedUtc = info.LastWriteTimeUtc,
            CreatedUtc = info.CreationTimeUtc
        };
    }

    [Fact]
    public async Task An_image_is_decoded_at_its_native_size()
    {
        using var temp = new TempFolder();

        // The whole point: not the 1600px the preview pipeline produces.
        var decoded = await Create().DecodeAsync(Write(temp, "big.png", 2400, 1600));

        Assert.NotNull(decoded);
        Assert.Equal(2400, decoded.Width);
        Assert.Equal(1600, decoded.Height);
    }

    [Fact]
    public async Task The_pixel_buffer_matches_the_dimensions()
    {
        using var temp = new TempFolder();

        var decoded = await Create().DecodeAsync(Write(temp, "sized.png", 320, 200));

        // Tightly packed BGRA: four bytes a pixel, no row padding.
        Assert.Equal(320 * 200 * 4, decoded!.Pixels.Length);
        Assert.Equal(320L * 200 * 4, decoded.SizeBytes);
    }

    [Fact]
    public async Task Jpeg_sources_decode_too()
    {
        using var temp = new TempFolder();

        var decoded = await Create().DecodeAsync(Write(temp, "photo.jpg", 800, 600, SKEncodedImageFormat.Jpeg));

        Assert.NotNull(decoded);
        Assert.Equal(800, decoded.Width);
    }

    [Fact]
    public async Task Video_is_not_an_image()
    {
        using var temp = new TempFolder();
        var file = Write(temp, "clip.png", 64, 64) with { MediaType = MediaType.Video };

        Assert.Null(await Create().DecodeAsync(file));
    }

    [Fact]
    public async Task A_missing_file_returns_null_rather_than_throwing()
    {
        var file = new MediaFile
        {
            FullPath = "/nowhere/gone.jpg",
            FileName = "gone.jpg",
            MediaType = MediaType.Image,
            SizeBytes = 1,
            ModifiedUtc = DateTimeOffset.UnixEpoch,
            CreatedUtc = DateTimeOffset.UnixEpoch
        };

        Assert.Null(await Create().DecodeAsync(file));
    }

    [Fact]
    public async Task A_raw_file_without_an_extractor_yields_nothing()
    {
        using var temp = new TempFolder();

        // Skia cannot decode RAW, so with no preview extractor there is nothing to show.
        var file = Write(temp, "shot.raf", 100, 100) with { FileName = "shot.raf" };

        Assert.Null(await Create().DecodeAsync(file));
    }
}
