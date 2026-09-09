using _2b2tAtlas.Server.Services;
using SkiaSharp;

namespace Atlas.Ingestor.Tests;

public sealed class RenderTileComposerTests
{
    [Fact]
    public async Task Compose_uses_highest_detail_zoom_and_preserves_tile_coordinates()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "atlas-compose-" + Guid.NewGuid().ToString("N"));
        var shallow = Path.Combine(root, "tiles", "zoom.0", "0", "0");
        var deep = Path.Combine(root, "tiles", "zoom.1", "0", "0");
        Directory.CreateDirectory(shallow);
        Directory.CreateDirectory(deep);

        try
        {
            WriteTile(Path.Combine(shallow, "tile.-1.2.png"), SKColors.Red);
            WriteTile(Path.Combine(shallow, "tile.0.2.png"), SKColors.Green);
            WriteTile(Path.Combine(shallow, "tile.-1.3.png"), SKColors.Blue);
            WriteTile(Path.Combine(shallow, "tile.0.3.png"), SKColors.Yellow);
            WriteTile(Path.Combine(deep, "tile.-1.2.png"), SKColors.Red);

            var result = await RenderTileComposer.ComposeAsync(root, cancellationToken);

            Assert.Equal(8, result.Width);
            Assert.Equal(8, result.Height);
            Assert.Equal(-256, result.MinX);
            Assert.Equal(512, result.MinZ);
            Assert.Equal(256, result.MaxXExclusive);
            Assert.Equal(1024, result.MaxZExclusive);
            using var mosaic = SKBitmap.Decode(result.Png);
            Assert.Equal(SKColors.Red, mosaic.GetPixel(1, 1));
            Assert.Equal(SKColors.Green, mosaic.GetPixel(5, 1));
            Assert.Equal(SKColors.Blue, mosaic.GetPixel(1, 5));
            Assert.Equal(SKColors.Yellow, mosaic.GetPixel(5, 5));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void WriteTile(string path, SKColor color)
    {
        using var bitmap = new SKBitmap(4, 4, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(color);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }
}
