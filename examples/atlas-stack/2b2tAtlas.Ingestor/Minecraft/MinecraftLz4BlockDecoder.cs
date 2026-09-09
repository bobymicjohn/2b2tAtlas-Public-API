using System.Buffers.Binary;
using System.Text;
using Atlas.Ingestor.Security;
using K4os.Compression.LZ4;
using K4os.Hash.xxHash;

namespace Atlas.Ingestor.Minecraft;

/// <summary>Decodes Minecraft's checksummed lz4-java block stream within a fixed expansion budget.</summary>
internal static class MinecraftLz4BlockDecoder
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("LZ4Block");
    private const int HeaderBytes = 21;
    private const byte RawMethod = 0x10;
    private const byte Lz4Method = 0x20;
    private const uint ChecksumSeed = 0x9747b28c;

    /// <summary>Decodes raw or compressed blocks until a validated end marker is reached.</summary>
    /// <param name="input">lz4-java block stream.</param>
    /// <param name="maxExpandedBytes">Maximum aggregate decoded length.</param>
    /// <param name="cancellationToken">Token that cancels reads and output writes.</param>
    /// <returns>A rewound memory stream containing the decoded bytes.</returns>
    /// <exception cref="InputValidationException">The framing, method, decoded length, end marker, or stream termination is invalid.</exception>
    /// <exception cref="InputSecurityException">A block exceeds its declared maximum or fails its xxHash checksum.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    /// <remarks>The partially decoded output is disposed on every failure.</remarks>
    public static async Task<MemoryStream> DecodeAsync(
        Stream input,
        long maxExpandedBytes,
        CancellationToken cancellationToken)
    {
        var output = new MemoryStream();
        var header = new byte[HeaderBytes];
        try
        {
            while (true)
            {
                await ReadExactlyAsync(input, header, cancellationToken);
                if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
                    throw new InputValidationException("Minecraft LZ4 block has invalid magic bytes.");
                var token = header[8];
                var method = (byte)(token & 0xf0);
                var compressionLevel = token & 0x0f;
                if (method is not (RawMethod or Lz4Method))
                    throw new InputValidationException("Minecraft LZ4 block uses an unsupported method.");
                var compressedLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(9, 4));
                var decompressedLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(13, 4));
                var expectedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(17, 4));
                if (compressedLength == 0 && decompressedLength == 0)
                {
                    if (expectedChecksum != 0)
                        throw new InputValidationException("Minecraft LZ4 end marker has a checksum.");
                    if (input.ReadByte() != -1)
                        throw new InputValidationException("Minecraft LZ4 stream has trailing data.");
                    output.Position = 0;
                    return output;
                }

                var maximumBlockLength = 1 << (compressionLevel + 10);
                if (compressedLength <= 0 || decompressedLength <= 0 ||
                    compressedLength > maximumBlockLength || decompressedLength > maximumBlockLength ||
                    output.Length + decompressedLength > maxExpandedBytes ||
                    method == RawMethod && compressedLength != decompressedLength)
                    throw new InputSecurityException("Minecraft LZ4 block length is invalid or exceeds the safety limit.");

                var compressed = new byte[compressedLength];
                await ReadExactlyAsync(input, compressed, cancellationToken);
                var decompressed = new byte[decompressedLength];
                if (method == RawMethod)
                    compressed.CopyTo(decompressed, 0);
                else
                {
                    var decoded = LZ4Codec.Decode(compressed, decompressed);
                    if (decoded != decompressedLength)
                        throw new InputValidationException("Minecraft LZ4 block decompressed to an unexpected length.");
                }
                var checksum = new XXH32(ChecksumSeed);
                checksum.Update(decompressed);
                if (checksum.Digest() != expectedChecksum)
                    throw new InputSecurityException("Minecraft LZ4 block checksum is invalid.");
                await output.WriteAsync(decompressed, cancellationToken);
            }
        }
        catch (EndOfStreamException exception)
        {
            throw new InputValidationException("Minecraft LZ4 stream is truncated.", exception);
        }
        catch
        {
            await output.DisposeAsync();
            throw;
        }
    }

    private static async Task ReadExactlyAsync(
        Stream input,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await input.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }
}