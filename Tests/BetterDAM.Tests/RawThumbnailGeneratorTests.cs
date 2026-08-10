using BetterDAM.Core.Interfaces;
using BetterDAM.Core.Models;
using BetterDAM.Metadata.ExifTool;
using BetterDAM.Preview.Images;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace BetterDAM.Tests;

public class RawThumbnailGeneratorTests
{
    private sealed class StubExtractor(byte[]? preview, bool available = true) : IEmbeddedPreviewExtractor
    {
        public bool IsAvailable { get; } = available;

        public int Calls { get; private set; }

        public Task<byte[]?> ExtractAsync(MediaFile file, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(preview);
        }
    }

    private static byte[] EncodeJpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.OrangeRed);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }

    private static MediaFile RawFile(string name = "/library/IMG001.CR3") => new()
    {
        FullPath = name,
        FileName = Path.GetFileName(name),
        MediaType = MediaTypeRegistry.GetMediaType(name),
        SizeBytes = 1,
        ModifiedUtc = DateTimeOffset.UnixEpoch,
        CreatedUtc = DateTimeOffset.UnixEpoch
    };

    private static RawThumbnailGenerator Create(IEmbeddedPreviewExtractor extractor)
        => new(extractor, NullLogger<RawThumbnailGenerator>.Instance);

    [Theory]
    [InlineData("/library/IMG001.CR3", true)]
    [InlineData("/library/IMG001.NEF", true)]
    [InlineData("/library/IMG001.ARW", true)]
    [InlineData("/library/IMG001.RAF", true)]
    [InlineData("/library/IMG001.DNG", true)]
    [InlineData("/library/IMG001.tif", true)]
    // Skia handles these directly, so the RAW path must not claim them.
    [InlineData("/library/IMG001.jpg", false)]
    [InlineData("/library/IMG001.png", false)]
    // Not an image at all.
    [InlineData("/library/CLIP.mp4", false)]
    public void Handles_images_skia_cannot_decode(string path, bool expected)
        => Assert.Equal(expected, Create(new StubExtractor([])).CanHandle(RawFile(path)));

    [Fact]
    public void Declines_everything_when_exiftool_is_unavailable()
    {
        var generator = Create(new StubExtractor(null, available: false));

        Assert.False(generator.CanHandle(RawFile()));
    }

    [Fact]
    public async Task Renders_the_extracted_preview_down_to_the_requested_size()
    {
        var generator = Create(new StubExtractor(EncodeJpeg(2400, 1600)));

        var bytes = await generator.GenerateAsync(RawFile(), 320);

        Assert.NotNull(bytes);

        using var decoded = SKBitmap.Decode(bytes);
        Assert.Equal(320, decoded.Width);
        Assert.Equal(213, decoded.Height);
    }

    [Fact]
    public async Task Returns_null_when_the_file_has_no_embedded_preview()
    {
        var generator = Create(new StubExtractor(null));

        Assert.Null(await generator.GenerateAsync(RawFile(), 320));
    }

    [Fact]
    public async Task Returns_null_when_the_preview_is_not_a_decodable_image()
    {
        var generator = Create(new StubExtractor("not an image"u8.ToArray()));

        Assert.Null(await generator.GenerateAsync(RawFile(), 320));
    }
}

public class ExifToolPreviewExtractorTests
{
    private static ExifToolPreviewExtractor Create()
        => new(new RealExifTool.Locator(), NullLogger<ExifToolPreviewExtractor>.Instance);

    private static async Task RunExifToolAsync(params string[] arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = RealExifTool.Path!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(startInfo)!;
        await process.WaitForExitAsync();
    }

    private static string WriteJpeg(string path, int width, int height, SKColor colour)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(colour);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        using var stream = File.Create(path);
        data.SaveTo(stream);
        return path;
    }

    [Fact]
    public async Task Extracts_an_embedded_preview_as_jpeg_bytes()
    {
        if (!RealExifTool.IsAvailable)
        {
            return;
        }

        using var temp = new TempFolder();

        // A JPEG carrying an embedded thumbnail stands in for a RAW: the extraction path through
        // ExifTool's binary output is identical.
        var host = WriteJpeg(Path.Combine(temp.Path, "HOST.jpg"), 400, 300, SKColors.SlateGray);
        var embedded = WriteJpeg(Path.Combine(temp.Path, "embedded.jpg"), 160, 120, SKColors.OrangeRed);
        await RunExifToolAsync("-overwrite_original", $"-ThumbnailImage<={embedded}", host);

        var preview = await Create().ExtractAsync(MediaFile.FromFileInfo(new FileInfo(host)));

        Assert.NotNull(preview);

        // Binary data must survive the process pipe intact.
        Assert.Equal(0xFF, preview[0]);
        Assert.Equal(0xD8, preview[1]);

        using var decoded = SKBitmap.Decode(preview);
        Assert.Equal(160, decoded.Width);
        Assert.Equal(120, decoded.Height);
    }

    [Fact]
    public async Task Returns_null_when_there_is_no_preview_to_extract()
    {
        if (!RealExifTool.IsAvailable)
        {
            return;
        }

        using var temp = new TempFolder();
        var plain = WriteJpeg(Path.Combine(temp.Path, "PLAIN.jpg"), 200, 150, SKColors.SlateGray);

        Assert.Null(await Create().ExtractAsync(MediaFile.FromFileInfo(new FileInfo(plain))));
    }

    [Fact]
    public async Task Reports_unavailable_without_exiftool()
    {
        var extractor = new ExifToolPreviewExtractor(
            new UnavailableLocator(),
            NullLogger<ExifToolPreviewExtractor>.Instance);

        Assert.False(extractor.IsAvailable);
        Assert.Null(await extractor.ExtractAsync(new MediaFile
        {
            FullPath = "/library/IMG001.CR3",
            FileName = "IMG001.CR3",
            MediaType = MediaType.Image,
            SizeBytes = 1,
            ModifiedUtc = DateTimeOffset.UnixEpoch,
            CreatedUtc = DateTimeOffset.UnixEpoch
        }));
    }

    private sealed class UnavailableLocator : IExifToolLocator
    {
        public string? ExifToolPath => null;

        public bool IsAvailable => false;
    }
}
