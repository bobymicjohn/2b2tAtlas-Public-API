using System.Buffers.Binary;
using System.Text;
using Atlas.Ingestor.Configuration;
using Atlas.Ingestor.Minecraft;
using Atlas.Ingestor.Security;
using K4os.Compression.LZ4;
using K4os.Hash.xxHash;

namespace Atlas.Ingestor.Tests;

public sealed class MinecraftLz4ChunkTests
{
    [Fact]
    public async Task Chunk_reader_accepts_compressed_and_raw_lz4_java_blocks()
    {
        var nbt = BuildChunkNbt(17, -23);
        await using var compressed = new MemoryStream(BuildLz4Stream(nbt, compressed: true));
        await using var raw = new MemoryStream(BuildLz4Stream(nbt, compressed: false));

        Assert.Equal(new ChunkCoordinates(17, -23), await NbtSummaryReader.ReadChunkCoordinatesAsync(
            compressed, 4, 64 * IngestLimits.MiB, TestContext.Current.CancellationToken));
        Assert.Equal(new ChunkCoordinates(17, -23), await NbtSummaryReader.ReadChunkCoordinatesAsync(
            raw, 4, 64 * IngestLimits.MiB, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Chunk_reader_rejects_corrupted_lz4_checksum()
    {
        var stream = BuildLz4Stream(BuildChunkNbt(1, 2), compressed: true);
        stream[17] ^= 0xff;
        await using var input = new MemoryStream(stream);

        await Assert.ThrowsAsync<InputSecurityException>(() => NbtSummaryReader.ReadChunkCoordinatesAsync(
            input, 4, 64 * IngestLimits.MiB, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Chunk_reader_tolerates_modified_utf8_and_garbage_strings()
    {
        // A real 2b2t item/sign name: a Modified-UTF-8 NUL (0xC0 0x80) followed by a lone 0xFF that
        // is invalid standard UTF-8. Such unsurfaced strings must not abort the whole ingest.
        var nbt = BuildChunkNbtWithStringTag(5, -9, [0xC0, 0x80, 0xFF, 0x41]);
        await using var raw = new MemoryStream(BuildLz4Stream(nbt, compressed: false));

        Assert.Equal(new ChunkCoordinates(5, -9), await NbtSummaryReader.ReadChunkCoordinatesAsync(
            raw, 4, 64 * IngestLimits.MiB, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Bounds_inspector_accepts_external_lz4_mcc_chunk()
    {
        using var temporary = new TempDirectory();
        var regions = temporary.Resolve("region");
        Directory.CreateDirectory(regions);
        var region = new byte[3 * 4096];
        region[2] = 2;
        region[3] = 1;
        BinaryPrimitives.WriteInt32BigEndian(region.AsSpan(8192, 4), 1);
        region[8196] = 0x84;
        await File.WriteAllBytesAsync(Path.Combine(regions, "r.0.0.mca"), region, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(regions, "c.0.0.mcc"),
            BuildLz4Stream(BuildChunkNbt(0, 0), compressed: true),
            TestContext.Current.CancellationToken);

        var bounds = await ChunkBoundsInspector.InspectAsync(
            temporary.Path, new IngestLimits(), TestContext.Current.CancellationToken);

        Assert.Equal(1, bounds.ChunkCount);
        Assert.Equal(0, bounds.MinBlockX);
        Assert.Equal(16, bounds.MaxBlockXExclusive);
    }

    private static byte[] BuildLz4Stream(byte[] source, bool compressed)
    {
        const int level = 4;
        byte[] payload;
        byte method;
        if (compressed)
        {
            payload = new byte[LZ4Codec.MaximumOutputSize(source.Length)];
            var length = LZ4Codec.Encode(source, payload, LZ4Level.L00_FAST);
            Assert.True(length > 0 && length < source.Length);
            Array.Resize(ref payload, length);
            method = 0x20;
        }
        else
        {
            payload = source;
            method = 0x10;
        }

        using var output = new MemoryStream();
        WriteHeader(output, (byte)(method | level), payload.Length, source.Length, Checksum(source));
        output.Write(payload);
        WriteHeader(output, (byte)(0x10 | level), 0, 0, 0);
        return output.ToArray();
    }

    private static void WriteHeader(Stream output, byte token, int compressed, int decompressed, uint checksum)
    {
        output.Write(Encoding.ASCII.GetBytes("LZ4Block"));
        output.WriteByte(token);
        Span<byte> values = stackalloc byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(values[..4], compressed);
        BinaryPrimitives.WriteInt32LittleEndian(values.Slice(4, 4), decompressed);
        BinaryPrimitives.WriteUInt32LittleEndian(values.Slice(8, 4), checksum);
        output.Write(values);
    }

    private static uint Checksum(byte[] source)
    {
        var checksum = new XXH32(0x9747b28c);
        checksum.Update(source);
        return checksum.Digest();
    }

    private static byte[] BuildChunkNbt(int x, int z)
    {
        using var output = new MemoryStream();
        output.WriteByte(10);
        WriteString(output, string.Empty);
        output.WriteByte(7);
        WriteString(output, "padding");
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, 2048);
        output.Write(length);
        output.Write(new byte[2048]);
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

    private static byte[] BuildChunkNbtWithStringTag(int x, int z, byte[] rawStringValue)
    {
        using var output = new MemoryStream();
        output.WriteByte(10);
        WriteString(output, string.Empty);
        output.WriteByte(8);
        WriteString(output, "CustomName");
        Span<byte> valueLength = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(valueLength, checked((ushort)rawStringValue.Length));
        output.Write(valueLength);
        output.Write(rawStringValue);
        WriteIntTag(output, "xPos", x);
        WriteIntTag(output, "zPos", z);
        output.WriteByte(0);
        return output.ToArray();
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
