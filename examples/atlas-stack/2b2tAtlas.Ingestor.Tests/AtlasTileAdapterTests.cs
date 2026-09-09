using Atlas.Ingestor.Configuration;
using Atlas.Ingestor.Models;
using Atlas.Ingestor.Publishing;
using Atlas.Ingestor.Security;
using SkiaSharp;

namespace Atlas.Ingestor.Tests;

public sealed class AtlasTileAdapterTests
{
    [Fact]
    public async Task Adapt_maps_signed_native_tiles_and_builds_aligned_parents()
    {
        using var temporary = new TempDirectory();
        var source = temporary.Resolve("native");
        WriteNativeTile(Path.Combine(source, "0", "0", "tile.0.0.png"), SKColors.Red);
        WriteNativeTile(Path.Combine(source, "-1", "-1", "tile.-1.-1.png"), SKColors.Blue);
        var output = temporary.Resolve("atlas");

        var report = await AtlasTileAdapter.AdaptAsync(
            source,
            output,
            AtlasTileScheme.Parse("atlas-overworld-256k-v1"),
            new IngestLimits(),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, report.MinZoom);
        Assert.Equal(10, report.MaxZoom);
        Assert.True(File.Exists(Path.Combine(output, "10", "500", "500.png")));
        Assert.True(File.Exists(Path.Combine(output, "10", "499", "499.png")));
        Assert.True(File.Exists(Path.Combine(output, "9", "250", "250.png")));
        Assert.True(File.Exists(Path.Combine(output, "9", "249", "249.png")));
        Assert.True(File.Exists(Path.Combine(output, "0", "0", "0.png")));
    }

    [Fact]
    public async Task Adapt_rejects_native_tile_in_wrong_floor_divided_group()
    {
        using var temporary = new TempDirectory();
        var source = temporary.Resolve("native");
        WriteNativeTile(Path.Combine(source, "0", "0", "tile.-1.-1.png"), SKColors.Red);

        await Assert.ThrowsAsync<InputValidationException>(() => AtlasTileAdapter.AdaptAsync(
            source,
            temporary.Resolve("atlas"),
            AtlasTileScheme.Parse("atlas-overworld-256k-v1"),
            new IngestLimits(),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Adapt_sparse_preserves_unbounded_global_coordinates()
    {
        using var temporary = new TempDirectory();
        var source = temporary.Resolve("native");
        WriteNativeTile(Path.Combine(source, "-100", "200", "tile.-1000.2000.png"), SKColors.Green);
        var output = temporary.Resolve("atlas");

        var report = await AtlasTileAdapter.AdaptAsync(
            source,
            output,
            AtlasTileScheme.Parse("atlas-overworld-sparse-v1"),
            new IngestLimits(),
            TestContext.Current.CancellationToken,
            BoundsForNativeTile(-1000, 2000));

        Assert.Equal(TilePublisher.SparseAtlasScheme, report.CoordinateScheme);
        Assert.True(File.Exists(Path.Combine(output, "10", "2500", "-500.png")));
        Assert.True(File.Exists(Path.Combine(output, "9", "1250", "-250.png")));
        Assert.True(File.Exists(Path.Combine(output, "8", "625", "-125.png")));
    }

    [Fact]
    public async Task Adapt_end_sparse_preserves_global_coordinates()
    {
        using var temporary = new TempDirectory();
        var source = temporary.Resolve("native");
        WriteNativeTile(Path.Combine(source, "30", "-44", "tile.300.-438.png"), SKColors.Purple);
        var output = temporary.Resolve("atlas");
        var scheme = AtlasTileScheme.Parse("atlas-end-sparse-v1");

        var report = await AtlasTileAdapter.AdaptAsync(
            source,
            output,
            scheme,
            new IngestLimits(),
            TestContext.Current.CancellationToken,
            BoundsForNativeTile(300, -438));

        Assert.Equal("end", scheme.Dimension);
        Assert.Equal(TilePublisher.SparseAtlasScheme, report.CoordinateScheme);
        Assert.True(File.Exists(Path.Combine(output, "8", "-356", "382.png")));
    }

    [Fact]
    public async Task Adapt_sparse_rejects_renderer_tiles_not_backed_by_chunks()
    {
        using var temporary = new TempDirectory();
        var source = temporary.Resolve("native");
        WriteNativeTile(Path.Combine(source, "0", "0", "tile.0.0.png"), SKColors.Red);
        WriteNativeTile(Path.Combine(source, "0", "0", "tile.1.0.png"), SKColors.Blue);

        await Assert.ThrowsAsync<InputSecurityException>(() => AtlasTileAdapter.AdaptAsync(
            source,
            temporary.Resolve("atlas"),
            AtlasTileScheme.Parse("atlas-overworld-sparse-v1"),
            new IngestLimits(),
            TestContext.Current.CancellationToken,
            BoundsForNativeTile(0, 0)));
    }

    [Fact]
    public async Task Adapt_sparse_prunes_transparent_renderer_padding()
    {
        using var temporary = new TempDirectory();
        var source = temporary.Resolve("native");
        WriteNativeTile(Path.Combine(source, "0", "0", "tile.0.0.png"), SKColors.Red);
        WriteNativeTile(Path.Combine(source, "0", "0", "tile.1.0.png"), SKColors.Transparent);
        var output = temporary.Resolve("atlas");

        var report = await AtlasTileAdapter.AdaptAsync(
            source,
            output,
            AtlasTileScheme.Parse("atlas-overworld-sparse-v1"),
            new IngestLimits(),
            TestContext.Current.CancellationToken,
            BoundsForNativeTile(0, 0));

        Assert.True(File.Exists(Path.Combine(output, "10", "500", "500.png")));
        Assert.False(File.Exists(Path.Combine(output, "10", "500", "501.png")));
        Assert.Equal(1, report.TilesPerZoom[10]);
    }

    private static WorldBounds BoundsForNativeTile(int tileX, int tileZ)
    {
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.ASCII.GetBytes($"{tileX},{tileZ}\n")));
        return new WorldBounds(
            tileX * 16,
            tileZ * 16,
            tileX * 16 + 15,
            tileZ * 16 + 15,
            256,
            1,
            hash,
            [new NativeTileCoordinate(tileX, tileZ)]);
    }

    private static void WriteNativeTile(string path, SKColor color)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bitmap = new SKBitmap(new SKImageInfo(256, 256, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        using var output = File.Create(path);
        encoded.SaveTo(output);
    }
}