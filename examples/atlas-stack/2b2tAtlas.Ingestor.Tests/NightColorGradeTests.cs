using Atlas.Ingestor.Rendering;
using Atlas.Ingestor.Security;
using SkiaSharp;

namespace Atlas.Ingestor.Tests;

public sealed class NightColorGradeTests
{
    [Fact]
    public async Task Colorizer_rejects_empty_inventories()
    {
        using var temporary = new TempDirectory();
        var day = Directory.CreateDirectory(Path.Combine(temporary.Path, "day")).FullName;
        var night = Directory.CreateDirectory(Path.Combine(temporary.Path, "night")).FullName;

        await Assert.ThrowsAsync<InputValidationException>(() =>
            NightTileColorizer.ApplyAsync(day, night, new NightColorGradeOptions(), CancellationToken.None));
    }

    [Fact]
    public void Grade_preserves_day_hue_while_using_night_luminance()
    {
        var result = NightColorGrade.Apply(20, 140, 40, 55, 55, 55, 1.2, 1);

        Assert.True(result.G > result.R);
        Assert.True(result.G > result.B);
        Assert.InRange(result.G, (byte)60, (byte)140);
    }

    [Fact]
    public async Task Colorizer_requires_identical_tile_inventories_and_preserves_alpha()
    {
        using var temporary = new TempDirectory();
        var dayRoot = temporary.Resolve("day");
        var nightRoot = temporary.Resolve("night");
        Directory.CreateDirectory(dayRoot);
        Directory.CreateDirectory(nightRoot);
        WriteTile(Path.Combine(dayRoot, "0.png"), new SKColor(20, 140, 40, 255), new SKColor(0, 0, 0, 0));
        WriteTile(Path.Combine(nightRoot, "0.png"), new SKColor(55, 55, 55, 255), new SKColor(0, 0, 0, 0));

        await NightTileColorizer.ApplyAsync(dayRoot, nightRoot,
            new NightColorGradeOptions { Saturation = 1.2, Lightness = 1 },
            TestContext.Current.CancellationToken);

        using var result = SKBitmap.Decode(Path.Combine(nightRoot, "0.png"));
        Assert.NotNull(result);
        Assert.True(result.GetPixel(0, 0).Green > result.GetPixel(0, 0).Red);
        Assert.Equal(0, result.GetPixel(1, 0).Alpha);
    }

    private static void WriteTile(string path, params SKColor[] pixels)
    {
        using var bitmap = new SKBitmap(pixels.Length, 1, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        bitmap.Pixels = pixels;
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }
}
