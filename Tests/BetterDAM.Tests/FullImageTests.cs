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

    /// <summary>Stands in for LibRaw, recording whether it was consulted.</summary>
    private sealed class StubRawDecoder(DecodedImage? result, bool available = true) : IRawDecoder
    {
        public int Calls { get; private set; }

        public bool IsAvailable => available;

        public Task<DecodedImage?> DevelopAsync(MediaFile file, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private sealed class StubSettings(AppSettings settings) : ISettingsService
    {
        public AppSettings Current { get; private set; } = settings;

        public event EventHandler<AppSettings>? Changed;

        public Task SaveAsync(AppSettings value, CancellationToken cancellationToken = default)
        {
            Current = value;
            Changed?.Invoke(this, value);
            return Task.CompletedTask;
        }
    }

    private static SkiaFullImageDecoder Create(IRawDecoder? raw = null, bool developRaw = true)
        => new(
            new NoPreviews(),
            raw ?? new StubRawDecoder(null),
            new StubSettings(AppSettings.Default with { DevelopRawFiles = developRaw }),
            NullLogger<SkiaFullImageDecoder>.Instance);

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

public class RawDevelopmentTests
{
    private static MediaFile Raw(string name = "shot.raf") => new()
    {
        FullPath = "/library/" + name,
        FileName = name,
        MediaType = MediaType.Image,
        SizeBytes = 27_000_000,
        ModifiedUtc = DateTimeOffset.UnixEpoch,
        CreatedUtc = DateTimeOffset.UnixEpoch
    };

    [Fact]
    public void The_camera_white_balance_is_used()
    {
        var args = LibRawImageDecoder.BuildStartInfo("/usr/bin/dcraw_emu", "/library/shot.raf").ArgumentList;

        // Without -w the develop is LibRaw's guess at neutral, not the picture as shot.
        Assert.Contains("-w", args);
    }

    [Fact]
    public void Output_goes_to_stdout_in_srgb()
    {
        var args = LibRawImageDecoder.BuildStartInfo("/usr/bin/dcraw_emu", "/library/shot.raf").ArgumentList;

        Assert.Equal("1", args[args.IndexOf("-o") + 1]);
        Assert.Equal("-", args[args.IndexOf("-Z") + 1]);
        Assert.Equal("/library/shot.raf", args[^1]);
    }

    [Fact]
    public void A_minimal_ppm_is_parsed_to_bgra()
    {
        // Two pixels: pure red then pure blue, as RGB.
        var ppm = "P6\n2 1\n255\n"u8.ToArray()
            .Concat<byte>([255, 0, 0, 0, 0, 255])
            .ToArray();

        var image = LibRawImageDecoder.ParsePpm(ppm);

        Assert.NotNull(image);
        Assert.Equal(2, image.Width);
        Assert.Equal(1, image.Height);

        // BGRA: the red pixel's blue channel is 0 and its red channel is 255.
        Assert.Equal([0, 0, 255, 255, 255, 0, 0, 255], image.Pixels);
    }

    [Fact]
    public void Comments_and_extra_whitespace_in_the_header_are_tolerated()
    {
        var ppm = "P6\n# written by something\n1  1\n255\n"u8.ToArray()
            .Concat<byte>([10, 20, 30])
            .ToArray();

        var image = LibRawImageDecoder.ParsePpm(ppm);

        Assert.NotNull(image);
        Assert.Equal([30, 20, 10, 255], image.Pixels);
    }

    [Theory]
    [InlineData("")]
    [InlineData("P5\n1 1\n255\n")]
    [InlineData("P6\n1 1\n65535\n")]
    [InlineData("P6\n1 1\n255\n")]
    public void Anything_unexpected_is_rejected_rather_than_misread(string header)
    {
        // Not a PPM, the greyscale variant, 16-bit, and a truncated body: none should produce a
        // half-decoded image.
        Assert.Null(LibRawImageDecoder.ParsePpm(System.Text.Encoding.ASCII.GetBytes(header)));
    }

    [Fact]
    public void Zero_dimensions_are_rejected()
        => Assert.Null(LibRawImageDecoder.ParsePpm("P6\n0 0\n255\n"u8));
}
