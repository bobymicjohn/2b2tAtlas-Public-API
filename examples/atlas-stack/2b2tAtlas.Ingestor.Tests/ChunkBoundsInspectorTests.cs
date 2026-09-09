using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Atlas.Ingestor.Configuration;
using Atlas.Ingestor.Minecraft;
using Atlas.Ingestor.Security;

namespace Atlas.Ingestor.Tests;

public sealed class ChunkBoundsInspectorTests
{
    [Fact]
    public async Task Inspect_cross_checks_chunks_and_calculates_exact_bounds()
    {
        using var temporary = new TempDirectory();
        var regions = temporary.Resolve("region");
        Directory.CreateDirectory(regions);
        await WriteRegionAsync(Path.Combine(regions, "r.-1.2.mca"), 31, 0, -1, 64);
        await WriteRegionAsync(Path.Combine(regions, "r.1.-2.mca"), 0, 31, 32, -33);

        var bounds = await ChunkBoundsInspector.InspectAsync(
            temporary.Path, new IngestLimits(), TestContext.Current.CancellationToken);

        Assert.Equal(-1, bounds.MinChunkX);
        Assert.Equal(-33, bounds.MinChunkZ);
        Assert.Equal(32, bounds.MaxChunkX);
        Assert.Equal(64, bounds.MaxChunkZ);
        Assert.Equal(-16, bounds.MinBlockX);
        Assert.Equal(-528, bounds.MinBlockZ);
        Assert.Equal(528, bounds.MaxBlockXExclusive);
        Assert.Equal(1040, bounds.MaxBlockZExclusive);
        Assert.Equal(2, bounds.ChunkCount);
    }

    [Fact]
    public async Task Inspect_rejects_chunk_nbt_that_disagrees_with_region_slot()
    {
        using var temporary = new TempDirectory();
        var regions = temporary.Resolve("region");
        Directory.CreateDirectory(regions);
        await WriteRegionAsync(Path.Combine(regions, "r.0.0.mca"), 3, 2, 999, 2);

        await Assert.ThrowsAsync<InputSecurityException>(() => ChunkBoundsInspector.InspectAsync(
            temporary.Path, new IngestLimits(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Inspect_skips_a_truncated_region_and_keeps_good_regions()
    {
        using var temporary = new TempDirectory();
        var regions = temporary.Resolve("region");
        Directory.CreateDirectory(regions);
        await WriteRegionAsync(Path.Combine(regions, "r.0.0.mca"), 5, 6, 5, 6);
        // A truncated region file (interrupted download): smaller than its 8 KiB header.
        await File.WriteAllBytesAsync(
            Path.Combine(regions, "r.1.0.mca"), new byte[100], TestContext.Current.CancellationToken);

        var bounds = await ChunkBoundsInspector.InspectAsync(
            temporary.Path, new IngestLimits(), TestContext.Current.CancellationToken);

        Assert.Equal(1, bounds.ChunkCount);
        Assert.Equal(5, bounds.MinChunkX);
        Assert.Equal(6, bounds.MinChunkZ);
        Assert.Equal(1, bounds.SkippedRegions);
    }

    [Fact]
    public async Task Inspect_skips_a_corrupt_chunk_slot_and_keeps_good_slots()
    {
        using var temporary = new TempDirectory();
        var regions = temporary.Resolve("region");
        Directory.CreateDirectory(regions);

        var nbt = BuildChunkNbt(0, 0);
        await using var compressed = new MemoryStream();
        await using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            await zlib.WriteAsync(nbt, TestContext.Current.CancellationToken);
        var payload = compressed.ToArray();

        var region = new byte[3 * 4096];
        // Valid slot (0,0): sector 2, count 1.
        region[2] = 2;
        region[3] = 1;
        BinaryPrimitives.WriteInt32BigEndian(region.AsSpan(8192, 4), payload.Length + 1);
        region[8196] = 2;
        payload.CopyTo(region, 8197);
        // Corrupt slot index 1: allocation points to sector 99, well past this 3-sector file.
        region[6] = 99;
        region[7] = 1;
        await File.WriteAllBytesAsync(
            Path.Combine(regions, "r.0.0.mca"), region, TestContext.Current.CancellationToken);

        var bounds = await ChunkBoundsInspector.InspectAsync(
            temporary.Path, new IngestLimits(), TestContext.Current.CancellationToken);

        Assert.Equal(1, bounds.ChunkCount);
        Assert.Equal(0, bounds.MinChunkX);
        Assert.Equal(0, bounds.MinChunkZ);
        Assert.Equal(1, bounds.SkippedChunks);
    }

    private static async Task WriteRegionAsync(
        string path,
        int localX,
        int localZ,
        int nbtX,
        int nbtZ)
    {
        var nbt = BuildChunkNbt(nbtX, nbtZ);
        await using var compressed = new MemoryStream();
        await using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            await zlib.WriteAsync(nbt, TestContext.Current.CancellationToken);
        var payload = compressed.ToArray();
        Assert.True(payload.Length + 5 <= 4096);

        var region = new byte[3 * 4096];
        var headerOffset = (localZ * 32 + localX) * 4;
        region[headerOffset + 2] = 2;
        region[headerOffset + 3] = 1;
        BinaryPrimitives.WriteInt32BigEndian(region.AsSpan(8192, 4), payload.Length + 1);
        region[8196] = 2;
        payload.CopyTo(region, 8197);
        await File.WriteAllBytesAsync(path, region, TestContext.Current.CancellationToken);
    }

    private static byte[] BuildChunkNbt(int x, int z)
    {
        using var output = new MemoryStream();
        output.WriteByte(10);
        WriteString(output, string.Empty);
        WriteIntTag(output, "xPos", x);
        WriteIntTag(output, "zPos", z);
        output.WriteByte(0);
        return output.ToArray();
    }

    private static void WriteIntTag(Stream output, string name, int value)
    {
        output.WriteByte(3);
        WriteString(output, name);
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        output.Write(bytes);
    }

    private static void WriteString(Stream output, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)bytes.Length));
        output.Write(length);
        output.Write(bytes);
    }
}