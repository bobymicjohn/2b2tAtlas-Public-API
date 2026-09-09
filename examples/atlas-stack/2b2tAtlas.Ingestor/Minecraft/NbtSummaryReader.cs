using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Atlas.Ingestor.Security;

namespace Atlas.Ingestor.Minecraft;

/// <summary>Contains the bounded metadata extracted from a Minecraft <c>level.dat</c> compound.</summary>
/// <param name="LevelName">Optional world display name.</param>
/// <param name="DataVersion">Optional Java data version.</param>
/// <param name="VersionName">Optional Java version name.</param>
/// <param name="LastPlayedUnixMilliseconds">Optional last-played timestamp in Unix milliseconds.</param>
/// <param name="PlayerDimension">Optional dimension stored for the downloaded player.</param>
/// <param name="PlayerX">Optional downloaded-player X coordinate.</param>
/// <param name="PlayerY">Optional downloaded-player Y coordinate.</param>
/// <param name="PlayerZ">Optional downloaded-player Z coordinate.</param>
public sealed record LevelDatSummary(
    string? LevelName,
    int? DataVersion,
    string? VersionName,
    long? LastPlayedUnixMilliseconds,
    string? PlayerDimension = null,
    double? PlayerX = null,
    double? PlayerY = null,
    double? PlayerZ = null);
/// <summary>Contains chunk coordinates read from NBT.</summary>
/// <param name="X">Chunk X coordinate.</param>
/// <param name="Z">Chunk Z coordinate.</param>
public sealed record ChunkCoordinates(int X, int Z);

/// <summary>Reads a small, bounded metadata projection from untrusted Minecraft NBT.</summary>
/// <remarks>The parser enforces expanded-byte, nesting, collection-length, and total-element budgets.</remarks>
public static class NbtSummaryReader
{
    /// <summary>Decompresses a chunk payload and reads its stored coordinates.</summary>
    /// <param name="compressedInput">Chunk payload positioned after the region compression byte.</param>
    /// <param name="compressionType">Minecraft compression identifier: gzip, zlib, raw, or lz4-java.</param>
    /// <param name="maxExpandedBytes">Maximum decompressed NBT bytes.</param>
    /// <param name="cancellationToken">Token that cancels decompression and bounded copying.</param>
    /// <returns>The NBT <c>xPos</c> and <c>zPos</c> coordinates.</returns>
    /// <exception cref="InputValidationException">Compression is unsupported or NBT is malformed, conflicting, truncated, or over budget.</exception>
    /// <exception cref="InputSecurityException">An LZ4 block length or checksum violates the safety contract.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public static async Task<ChunkCoordinates> ReadChunkCoordinatesAsync(
        Stream compressedInput,
        byte compressionType,
        long maxExpandedBytes,
        CancellationToken cancellationToken = default)
    {
        Stream input = compressionType switch
        {
            1 => new GZipStream(compressedInput, CompressionMode.Decompress, leaveOpen: true),
            2 => new ZLibStream(compressedInput, CompressionMode.Decompress, leaveOpen: true),
            3 => compressedInput,
            4 => await MinecraftLz4BlockDecoder.DecodeAsync(
                compressedInput, maxExpandedBytes, cancellationToken),
            _ => throw new InputValidationException($"Unsupported Minecraft chunk compression type: {compressionType}"),
        };
        try
        {
            await using var bounded = await ReadBoundedAsync(
                input, maxExpandedBytes, "Chunk NBT expands beyond the configured safety limit.", cancellationToken);
            var reader = ReadRootCompound(bounded);
            var x = reader.IntValues.GetValueOrDefault("xPos")
                ?? reader.IntValues.GetValueOrDefault("Level/xPos");
            var z = reader.IntValues.GetValueOrDefault("zPos")
                ?? reader.IntValues.GetValueOrDefault("Level/zPos");
            return x.HasValue && z.HasValue
                ? new ChunkCoordinates(x.Value, z.Value)
                : throw new InputValidationException("Chunk NBT does not contain xPos and zPos coordinates.");
        }
        finally
        {
            if (!ReferenceEquals(input, compressedInput))
                await input.DisposeAsync();
        }
    }

    /// <summary>Opens a <c>level.dat</c> file and reads its bounded metadata projection.</summary>
    /// <param name="path">Path to the <c>level.dat</c> file.</param>
    /// <param name="maxExpandedBytes">Maximum decompressed NBT bytes.</param>
    /// <param name="cancellationToken">Token that cancels file reading and decompression.</param>
    /// <returns>The available world metadata fields.</returns>
    /// <exception cref="InputValidationException">NBT is malformed, truncated, or over budget.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public static async Task<LevelDatSummary> ReadLevelDatAsync(
        string path,
        long maxExpandedBytes,
        CancellationToken cancellationToken = default)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await ReadLevelDatAsync(file, maxExpandedBytes, cancellationToken);
    }

    /// <summary>Reads a bounded metadata projection from a seekable raw or gzip-compressed <c>level.dat</c> stream.</summary>
    /// <param name="seekableInput">Seekable stream positioned anywhere; it is rewound after compression detection.</param>
    /// <param name="maxExpandedBytes">Maximum decompressed NBT bytes.</param>
    /// <param name="cancellationToken">Token that cancels reading and decompression.</param>
    /// <returns>The available world metadata fields.</returns>
    /// <exception cref="InputValidationException">The stream is not seekable or NBT is malformed, truncated, or over budget.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public static async Task<LevelDatSummary> ReadLevelDatAsync(
        Stream seekableInput,
        long maxExpandedBytes,
        CancellationToken cancellationToken = default)
    {
        if (!seekableInput.CanSeek)
            throw new InputValidationException("level.dat input must be seekable.");
        Stream input = seekableInput;
        var prefix = new byte[2];
        var prefixRead = await seekableInput.ReadAsync(prefix, cancellationToken);
        seekableInput.Position = 0;
        if (prefixRead == 2 && prefix[0] == 0x1f && prefix[1] == 0x8b)
            input = new GZipStream(seekableInput, CompressionMode.Decompress, leaveOpen: true);

        await using var bounded = await ReadBoundedAsync(
            input, maxExpandedBytes, "level.dat expands beyond the configured safety limit.", cancellationToken);
        var reader = ReadRootCompound(bounded);
        var position = reader.DoubleListValues.GetValueOrDefault("Data/Player/Pos");
        var playerDimension = reader.StringValues.GetValueOrDefault("Data/Player/Dimension");
        if (playerDimension is null && reader.IntValues.GetValueOrDefault("Data/Player/Dimension") is int legacyDimension)
        {
            playerDimension = legacyDimension switch
            {
                -1 => "minecraft:the_nether",
                0 => "minecraft:overworld",
                1 => "minecraft:the_end",
                _ => $"legacy:{legacyDimension}",
            };
        }
        return new LevelDatSummary(
            reader.StringValues.GetValueOrDefault("Data/LevelName"),
            reader.IntValues.GetValueOrDefault("Data/DataVersion"),
            reader.StringValues.GetValueOrDefault("Data/Version/Name"),
            reader.LongValues.GetValueOrDefault("Data/LastPlayed"),
            playerDimension,
            position is { Count: >= 3 } ? position[0] : null,
            position is { Count: >= 3 } ? position[1] : null,
            position is { Count: >= 3 } ? position[2] : null);
    }

    private static async Task<MemoryStream> ReadBoundedAsync(
        Stream input,
        long maxExpandedBytes,
        string limitMessage,
        CancellationToken cancellationToken)
    {
        var bounded = new MemoryStream();
        var buffer = new byte[64 * 1024];
        long total = 0;
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                    break;
                total += read;
                if (total > maxExpandedBytes)
                    throw new InputValidationException(limitMessage);
                await bounded.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            bounded.Position = 0;
            return bounded;
        }
        catch
        {
            await bounded.DisposeAsync();
            throw;
        }
    }

    private static Reader ReadRootCompound(Stream bounded)
    {
        var reader = new Reader(bounded);
        if (reader.ReadByte() != 10)
            throw new InputValidationException("NBT root must be a compound.");
        _ = reader.ReadString();
        reader.ReadCompound([], 0);
        return reader;
    }

    private sealed class Reader(Stream stream)
    {
        private const int MaxDepth = 64;
        private const int MaxCollectionLength = 2_000_000;
        private const int MaxElements = 4_000_000;
        private int elements;

        public Dictionary<string, string?> StringValues { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int?> IntValues { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, long?> LongValues { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<double>> DoubleListValues { get; } = new(StringComparer.Ordinal);

        public byte ReadByte()
        {
            var value = stream.ReadByte();
            if (value < 0)
                throw new InputValidationException("Truncated NBT payload.");
            return (byte)value;
        }

        public string ReadString()
        {
            var length = ReadUInt16();
            var bytes = ReadBytes(length);
            return DecodeModifiedUtf8(bytes);
        }

        // Minecraft NBT strings use Java "Modified UTF-8" (DataInput.readUTF): U+0000 is encoded as
        // 0xC0 0x80 and supplementary characters as CESU-8 surrogate pairs, neither of which is valid
        // standard UTF-8. Decode leniently — real 2b2t worlds carry exotic/garbage bytes in item,
        // book, and sign names this summary never surfaces, so a malformed sequence yields U+FFFD
        // instead of aborting the whole ingest.
        private static string DecodeModifiedUtf8(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length);
            var index = 0;
            while (index < bytes.Length)
            {
                int first = bytes[index];
                if ((first & 0x80) == 0)
                {
                    builder.Append((char)first);
                    index += 1;
                }
                else if ((first & 0xE0) == 0xC0)
                {
                    if (index + 1 < bytes.Length && (bytes[index + 1] & 0xC0) == 0x80)
                    {
                        builder.Append((char)(((first & 0x1F) << 6) | (bytes[index + 1] & 0x3F)));
                        index += 2;
                    }
                    else { builder.Append('\uFFFD'); index += 1; }
                }
                else if ((first & 0xF0) == 0xE0)
                {
                    if (index + 2 < bytes.Length
                        && (bytes[index + 1] & 0xC0) == 0x80
                        && (bytes[index + 2] & 0xC0) == 0x80)
                    {
                        builder.Append((char)(((first & 0x0F) << 12) | ((bytes[index + 1] & 0x3F) << 6) | (bytes[index + 2] & 0x3F)));
                        index += 3;
                    }
                    else { builder.Append('\uFFFD'); index += 1; }
                }
                else { builder.Append('\uFFFD'); index += 1; }
            }
            return builder.ToString();
        }

        public void ReadCompound(IReadOnlyList<string> path, int depth)
        {
            EnsureDepth(depth);
            while (true)
            {
                var tag = ReadByte();
                if (tag == 0)
                    return;
                var name = ReadString();
                var childPath = path.Append(name).ToArray();
                ReadPayload(tag, childPath, depth + 1);
            }
        }

        private void ReadPayload(byte tag, IReadOnlyList<string> path, int depth)
        {
            EnsureDepth(depth);
            var joined = string.Join('/', path);
            switch (tag)
            {
                case 1: Skip(1); break;
                case 2: Skip(2); break;
                case 3:
                    var integer = ReadInt32();
                    if (joined is "Data/DataVersion" or "Data/Player/Dimension" or "xPos" or "zPos" or "Level/xPos" or "Level/zPos")
                    {
                        if (IntValues.TryGetValue(joined, out var existing) && existing != integer)
                            throw new InputValidationException($"NBT contains conflicting {joined} values.");
                        IntValues[joined] = integer;
                    }
                    break;
                case 4:
                    var longInteger = ReadInt64();
                    if (joined == "Data/LastPlayed")
                        LongValues[joined] = longInteger;
                    break;
                case 5: Skip(4); break;
                case 6: Skip(8); break;
                case 7: Skip(CheckedByteLength(ReadLength(), 1)); break;
                case 8:
                    var text = ReadString();
                    if (joined is "Data/LevelName" or "Data/Version/Name" or "Data/Player/Dimension")
                        StringValues[joined] = text;
                    break;
                case 9:
                    var childTag = ReadByte();
                    var listLength = ReadLength();
                    if (joined == "Data/Player/Pos" && childTag == 6)
                    {
                        var values = new List<double>(Math.Min(listLength, 3));
                        for (var index = 0; index < listLength; index++)
                        {
                            var value = ReadDouble();
                            if (index < 3) values.Add(value);
                        }
                        DoubleListValues[joined] = values;
                        break;
                    }
                    var listPath = path.Append("*").ToArray();
                    for (var index = 0; index < listLength; index++)
                        ReadPayload(childTag, listPath, depth + 1);
                    break;
                case 10: ReadCompound(path, depth + 1); break;
                case 11: Skip(CheckedByteLength(ReadLength(), 4)); break;
                case 12: Skip(CheckedByteLength(ReadLength(), 8)); break;
                default: throw new InputValidationException($"Unknown NBT tag type: {tag}");
            }
        }

        private int ReadLength()
        {
            var length = ReadInt32();
            if (length < 0 || length > MaxCollectionLength)
                throw new InputValidationException("NBT collection exceeds the safety limit.");
            elements = checked(elements + length);
            if (elements > MaxElements)
                throw new InputValidationException("NBT payload exceeds the element budget.");
            return length;
        }

        private static int CheckedByteLength(int count, int width)
        {
            try { return checked(count * width); }
            catch (OverflowException exception)
            {
                throw new InputValidationException("NBT array size overflowed.", exception);
            }
        }

        private ushort ReadUInt16()
        {
            Span<byte> bytes = stackalloc byte[2];
            ReadExactly(bytes);
            return BinaryPrimitives.ReadUInt16BigEndian(bytes);
        }

        private int ReadInt32()
        {
            Span<byte> bytes = stackalloc byte[4];
            ReadExactly(bytes);
            return BinaryPrimitives.ReadInt32BigEndian(bytes);
        }

        private long ReadInt64()
        {
            Span<byte> bytes = stackalloc byte[8];
            ReadExactly(bytes);
            return BinaryPrimitives.ReadInt64BigEndian(bytes);
        }

        private double ReadDouble() => BitConverter.Int64BitsToDouble(ReadInt64());

        private byte[] ReadBytes(int length)
        {
            var bytes = new byte[length];
            ReadExactly(bytes);
            return bytes;
        }

        private void Skip(int length)
        {
            if (length < 0)
                throw new InputValidationException("NBT skip length is invalid.");
            Span<byte> buffer = stackalloc byte[Math.Min(length, 4096)];
            var remaining = length;
            while (remaining > 0)
            {
                var chunk = Math.Min(remaining, buffer.Length);
                ReadExactly(buffer[..chunk]);
                remaining -= chunk;
            }
        }

        private void ReadExactly(Span<byte> buffer)
        {
            var read = 0;
            while (read < buffer.Length)
            {
                var count = stream.Read(buffer[read..]);
                if (count == 0)
                    throw new InputValidationException("Truncated NBT payload.");
                read += count;
            }
        }

        private static void EnsureDepth(int depth)
        {
            if (depth > MaxDepth)
                throw new InputValidationException("NBT nesting exceeds the safety limit.");
        }
    }
}
