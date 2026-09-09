using System.Buffers.Binary;

namespace Atlas.Ingestor.Security;

/// <summary>Validates ZIP and ZIP64 central-directory structure before managed archive traversal.</summary>
internal static class ZipCentralDirectory
{
    private const uint EndSignature = 0x06054b50;
    private const uint Zip64LocatorSignature = 0x07064b50;
    private const uint Zip64EndSignature = 0x06064b50;
    private const uint CentralEntrySignature = 0x02014b50;
    private const int MaximumEndRecordLength = ushort.MaxValue + 22;

    /// <summary>Rejects malformed, multi-disk, encrypted, unsupported, or over-count central directories.</summary>
    /// <param name="path">ZIP file to inspect.</param>
    /// <param name="maxEntries">Maximum accepted central-directory entry count.</param>
    /// <exception cref="InputValidationException">ZIP/ZIP64 records or bounds are malformed or use multiple disks.</exception>
    /// <exception cref="InputSecurityException">The entry count is excessive or an entry is encrypted or uses a disallowed method.</exception>
    public static void Validate(string path, int maxEntries)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 22)
            throw new InputValidationException("ZIP archive is too short to contain an end record.");

        var tailLength = (int)Math.Min(stream.Length, MaximumEndRecordLength);
        var tail = new byte[tailLength];
        stream.Position = stream.Length - tailLength;
        stream.ReadExactly(tail);
        var endIndex = FindEndRecord(tail);
        if (endIndex < 0)
            throw new InputValidationException("ZIP end record is missing or malformed.");

        var end = tail.AsSpan(endIndex);
        var diskNumber = ReadUInt16(end, 4);
        var centralDisk = ReadUInt16(end, 6);
        var entriesOnDisk = ReadUInt16(end, 8);
        var totalEntries16 = ReadUInt16(end, 10);
        var centralSize32 = ReadUInt32(end, 12);
        var centralOffset32 = ReadUInt32(end, 16);

        ulong totalEntries = totalEntries16;
        ulong centralSize = centralSize32;
        ulong centralOffset = centralOffset32;
        var requiresZip64 = totalEntries16 == ushort.MaxValue ||
            centralSize32 == uint.MaxValue || centralOffset32 == uint.MaxValue;
        if (requiresZip64)
        {
            var endAbsoluteOffset = stream.Length - tailLength + endIndex;
            (totalEntries, centralSize, centralOffset) = ReadZip64End(stream, endAbsoluteOffset);
        }
        else if (diskNumber != 0 || centralDisk != 0 || entriesOnDisk != totalEntries16)
        {
            throw new InputValidationException("Multi-disk ZIP archives are not accepted.");
        }

        if (totalEntries > (ulong)maxEntries || totalEntries > int.MaxValue)
            throw new InputSecurityException($"Archive has {totalEntries:N0} entries; limit is {maxEntries:N0}.");
        if (centralOffset > (ulong)stream.Length || centralSize > (ulong)stream.Length ||
            centralOffset + centralSize > (ulong)stream.Length)
        {
            throw new InputValidationException("ZIP central-directory bounds are invalid.");
        }

        stream.Position = checked((long)centralOffset);
        Span<byte> header = stackalloc byte[46];
        for (ulong index = 0; index < totalEntries; index++)
        {
            stream.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != CentralEntrySignature)
                throw new InputValidationException("ZIP central-directory entry is malformed.");

            var flags = ReadUInt16(header, 8);
            var method = ReadUInt16(header, 10);
            if ((flags & 0x0001) != 0 || (flags & 0x0040) != 0)
                throw new InputSecurityException("Encrypted ZIP entries are not accepted.");
            if (method is not (0 or 8))
                throw new InputSecurityException($"ZIP compression method {method} is not accepted; only stored and deflate are supported.");

            var nameLength = ReadUInt16(header, 28);
            var extraLength = ReadUInt16(header, 30);
            var commentLength = ReadUInt16(header, 32);
            var variableLength = checked(nameLength + extraLength + commentLength);
            if (stream.Position + variableLength > stream.Length)
                throw new InputValidationException("ZIP central-directory entry extends beyond the archive.");
            stream.Position += variableLength;
        }
    }

    private static int FindEndRecord(ReadOnlySpan<byte> tail)
    {
        for (var index = tail.Length - 22; index >= 0; index--)
        {
            if (ReadUInt32(tail, index) != EndSignature)
                continue;
            var commentLength = ReadUInt16(tail, index + 20);
            if (index + 22 + commentLength == tail.Length)
                return index;
        }
        return -1;
    }

    private static (ulong Entries, ulong Size, ulong Offset) ReadZip64End(FileStream stream, long endOffset)
    {
        if (endOffset < 20)
            throw new InputValidationException("ZIP64 locator is missing.");
        Span<byte> locator = stackalloc byte[20];
        stream.Position = endOffset - locator.Length;
        stream.ReadExactly(locator);
        if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != Zip64LocatorSignature ||
            ReadUInt32(locator, 4) != 0 || ReadUInt32(locator, 16) != 1)
        {
            throw new InputValidationException("Multi-disk or malformed ZIP64 archive is not accepted.");
        }

        var zip64Offset = BinaryPrimitives.ReadUInt64LittleEndian(locator[8..]);
        if (zip64Offset > (ulong)(stream.Length - 56))
            throw new InputValidationException("ZIP64 end-record offset is invalid.");
        Span<byte> end = stackalloc byte[56];
        stream.Position = checked((long)zip64Offset);
        stream.ReadExactly(end);
        if (BinaryPrimitives.ReadUInt32LittleEndian(end) != Zip64EndSignature ||
            ReadUInt32(end, 16) != 0 || ReadUInt32(end, 20) != 0)
        {
            throw new InputValidationException("Multi-disk or malformed ZIP64 archive is not accepted.");
        }
        var entriesOnDisk = BinaryPrimitives.ReadUInt64LittleEndian(end[24..]);
        var totalEntries = BinaryPrimitives.ReadUInt64LittleEndian(end[32..]);
        if (entriesOnDisk != totalEntries)
            throw new InputValidationException("Multi-disk ZIP64 archives are not accepted.");
        return (
            totalEntries,
            BinaryPrimitives.ReadUInt64LittleEndian(end[40..]),
            BinaryPrimitives.ReadUInt64LittleEndian(end[48..]));
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
}
