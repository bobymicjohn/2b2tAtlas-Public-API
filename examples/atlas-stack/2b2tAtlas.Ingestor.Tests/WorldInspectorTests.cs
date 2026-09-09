using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Atlas.Ingestor.Configuration;
using Atlas.Ingestor.Minecraft;
using Atlas;

namespace Atlas.Ingestor.Tests;

public sealed class WorldInspectorTests
{
    [Fact]
    public void Dimension_contract_matches_map_and_ingestor()
    {
        Assert.Equal(0, (int)Dimension.Overworld);
        Assert.Equal(1, (int)Dimension.Nether);
        Assert.Equal(2, (int)Dimension.End);
    }

    [Fact]
    public async Task Inspect_reads_metadata_and_all_storage_eras()
    {
        using var temporary = new TempDirectory();
        var world = temporary.Resolve("nested", "world");
        Directory.CreateDirectory(System.IO.Path.Combine(world, "region"));
        Directory.CreateDirectory(System.IO.Path.Combine(world, "DIM-1", "region"));
        Directory.CreateDirectory(System.IO.Path.Combine(world, "DIM1", "0", "0"));
        await File.WriteAllBytesAsync(System.IO.Path.Combine(world, "level.dat"), ZipFixture.MinimalLevelDat("Spawn", 3955), TestContext.Current.CancellationToken);
        await WriteRegionAsync(System.IO.Path.Combine(world, "region", "r.0.0.mca"), 0, 0);
        await WriteRegionAsync(System.IO.Path.Combine(world, "DIM-1", "region", "r.0.0.mcr"), 0, 0);
        await WriteLegacyChunkAsync(System.IO.Path.Combine(world, "DIM1", "0", "0", "c.0.0.dat"), 0, 0);

        var roots = WorldInspector.FindWorldRoots(temporary.Path, new IngestLimits());
        var result = await WorldInspector.InspectAsync(Assert.Single(roots), new IngestLimits(), TestContext.Current.CancellationToken);

        Assert.Equal("Spawn", result.LevelName);
        Assert.Equal(3955, result.DataVersion);
        Assert.Equal("1.21.1", result.VersionName);
        Assert.Equal(1_754_179_200_000, result.LastPlayedUnixMilliseconds);
        Assert.Equal("mixed", result.StorageEra);
        Assert.Equal(["overworld", "nether", "end"], result.Dimensions.Select(value => value.Key));
    }

    [Fact]
    public async Task Inspect_recognizes_namespaced_vanilla_dimension_storage()
    {
        using var temporary = new TempDirectory();
        var world = temporary.Resolve("world");
        var netherRegion = System.IO.Path.Combine(world, "dimensions", "minecraft", "the_nether", "region");
        var endRegion = System.IO.Path.Combine(world, "dimensions", "minecraft", "the_end", "region");
        Directory.CreateDirectory(netherRegion);
        Directory.CreateDirectory(endRegion);
        await File.WriteAllBytesAsync(System.IO.Path.Combine(world, "level.dat"), ZipFixture.MinimalLevelDat("Namespaced", 3955), TestContext.Current.CancellationToken);
        await WriteRegionAsync(System.IO.Path.Combine(netherRegion, "r.-1.0.mca"), -32, 0);
        await WriteRegionAsync(System.IO.Path.Combine(endRegion, "r.1.0.mca"), 32, 0);

        var result = await WorldInspector.InspectAsync(world, new IngestLimits(), TestContext.Current.CancellationToken);

        Assert.Equal(["nether", "end"], result.Dimensions.Select(value => value.Key));
        Assert.Equal("dimensions/minecraft/the_nether", result.Dimensions[0].RelativePath);
        Assert.Equal("dimensions/minecraft/the_end", result.Dimensions[1].RelativePath);
    }

    [Fact]
    public async Task Normalize_moves_namespaced_vanilla_storage_to_renderer_paths()
    {
        using var temporary = new TempDirectory();
        var world = temporary.Resolve("world");
        var overworldRegion = System.IO.Path.Combine(world, "dimensions", "minecraft", "overworld", "region");
        var netherRegion = System.IO.Path.Combine(world, "dimensions", "minecraft", "the_nether", "region");
        var endRegion = System.IO.Path.Combine(world, "dimensions", "minecraft", "the_end", "region");
        Directory.CreateDirectory(overworldRegion);
        Directory.CreateDirectory(netherRegion);
        Directory.CreateDirectory(endRegion);
        await File.WriteAllBytesAsync(System.IO.Path.Combine(world, "level.dat"), ZipFixture.MinimalLevelDat("Normalize", 3955), TestContext.Current.CancellationToken);
        await WriteRegionAsync(System.IO.Path.Combine(overworldRegion, "r.0.0.mca"), 0, 0);
        await WriteRegionAsync(System.IO.Path.Combine(netherRegion, "r.0.0.mca"), 0, 0);
        await WriteRegionAsync(System.IO.Path.Combine(endRegion, "r.0.0.mca"), 0, 0);

        WorldLayoutNormalizer.NormalizeCanonicalNamespacedDimensions(world);
        var result = await WorldInspector.InspectAsync(world, new IngestLimits(), TestContext.Current.CancellationToken);

        Assert.Equal(["overworld", "nether", "end"], result.Dimensions.Select(value => value.Key));
        Assert.Equal([".", "DIM-1", "DIM1"], result.Dimensions.Select(value => value.RelativePath));
    }

    [Fact]
    public async Task Inspect_rejects_duplicate_legacy_and_namespaced_storage_for_one_dimension()
    {
        using var temporary = new TempDirectory();
        var world = temporary.Resolve("world");
        var legacyRegion = System.IO.Path.Combine(world, "DIM-1", "region");
        var namespacedRegion = System.IO.Path.Combine(world, "dimensions", "minecraft", "the_nether", "region");
        Directory.CreateDirectory(legacyRegion);
        Directory.CreateDirectory(namespacedRegion);
        await File.WriteAllBytesAsync(System.IO.Path.Combine(world, "level.dat"), ZipFixture.MinimalLevelDat("Ambiguous", 3955), TestContext.Current.CancellationToken);
        await WriteRegionAsync(System.IO.Path.Combine(legacyRegion, "r.0.0.mca"), 0, 0);
        await WriteRegionAsync(System.IO.Path.Combine(namespacedRegion, "r.0.0.mca"), 0, 0);

        var error = await Assert.ThrowsAsync<Atlas.Ingestor.Security.InputValidationException>(
            () => WorldInspector.InspectAsync(world, new IngestLimits(), TestContext.Current.CancellationToken));
        Assert.Contains("more than one recognized storage root", error.Message);
    }

    [Fact]
    public async Task Inspect_preserves_worldtools_player_position_and_raw_custom_dimension()
    {
        using var temporary = new TempDirectory();
        var world = temporary.Resolve("world");
        var region = System.IO.Path.Combine(world, "region");
        var customRegion = System.IO.Path.Combine(world, "dimensions", "archive", "museum_2017", "region");
        Directory.CreateDirectory(region);
        Directory.CreateDirectory(customRegion);
        await File.WriteAllBytesAsync(
            System.IO.Path.Combine(world, "level.dat"),
            ZipFixture.MinimalLevelDat(
                "Temple_of_the_Talion_2017-03-06", 3955,
                playerDimension: "archive:museum_2017", playerX: -169902.5, playerY: 71, playerZ: 311836.25),
            TestContext.Current.CancellationToken);
        await WriteRegionAsync(System.IO.Path.Combine(region, "r.-332.609.mca"), -10_624, 19_488);

        var result = await WorldInspector.InspectAsync(world, new IngestLimits(), TestContext.Current.CancellationToken);

        Assert.NotNull(result.ArchiveEvidence);
        Assert.Equal("archive:museum_2017", result.ArchiveEvidence.PlayerDimension);
        Assert.Equal(-169902.5, result.ArchiveEvidence.PlayerX);
        Assert.Contains("archive:museum_2017", result.ArchiveEvidence.RawDimensionIds);
        Assert.Contains("Temple_of_the_Talion_2017-03-06", result.ArchiveEvidence.NameCandidates);
        Assert.Single(result.Dimensions); // The unknown custom dimension remains evidence; it is not guessed as vanilla.
    }

    [Fact]
    public async Task Missing_level_dat_world_is_discovered_inspected_and_given_renderer_only_metadata()
    {
        using var temporary = new TempDirectory();
        var world = temporary.Resolve("old-worldtools-export");
        var region = System.IO.Path.Combine(world, "region");
        Directory.CreateDirectory(region);
        await WriteRegionAsync(System.IO.Path.Combine(region, "r.0.0.mca"), 0, 0);

        var roots = WorldInspector.FindWorldRoots(temporary.Path, new IngestLimits());
        var result = await WorldInspector.InspectAsync(
            Assert.Single(roots), new IngestLimits(), TestContext.Current.CancellationToken);
        WorldLayoutNormalizer.EnsureRendererLevelDat(world, "Recovered Base");
        var recovered = await NbtSummaryReader.ReadLevelDatAsync(
            System.IO.Path.Combine(world, "level.dat"), new IngestLimits().MaxLevelDatBytes,
            TestContext.Current.CancellationToken);

        Assert.Null(result.LevelName);
        Assert.Single(result.Dimensions);
        Assert.Equal("Recovered Base", recovered.LevelName);
        Assert.Equal(4189, recovered.DataVersion);
    }

    private static async Task WriteRegionAsync(string path, int x, int z)
    {
        var nbt = BuildChunkNbt(x, z);
        await using var compressed = new MemoryStream();
        await using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            await zlib.WriteAsync(nbt, TestContext.Current.CancellationToken);
        var payload = compressed.ToArray();
        var region = new byte[3 * 4096];
        region[2] = 2;
        region[3] = 1;
        BinaryPrimitives.WriteInt32BigEndian(region.AsSpan(8192, 4), payload.Length + 1);
        region[8196] = 2;
        payload.CopyTo(region, 8197);
        await File.WriteAllBytesAsync(path, region, TestContext.Current.CancellationToken);
    }

    private static async Task WriteLegacyChunkAsync(string path, int x, int z)
    {
        await using var output = File.Create(path);
        await using var gzip = new GZipStream(output, CompressionLevel.SmallestSize);
        await gzip.WriteAsync(BuildChunkNbt(x, z), TestContext.Current.CancellationToken);
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
