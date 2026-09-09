using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Atlas.Ingestor.Configuration;
using Atlas.Ingestor.Models;
using Atlas.Ingestor.Security;

namespace Atlas.Ingestor.Minecraft;

/// <summary>Inspects Java chunk storage to derive verified occupied bounds and native tile coordinates.</summary>
public static partial class ChunkBoundsInspector
{
    private const int SectorBytes = 4096;
    private const int HeaderBytes = 8192;

    /// <summary>Reads recognized chunk storage and computes an authenticated occupied-chunk inventory.</summary>
    /// <param name="dimensionRoot">Root of the Minecraft dimension to inspect.</param>
    /// <param name="limits">NBT expansion and occupied-chunk ceilings.</param>
    /// <param name="cancellationToken">Token checked between files, chunks, and asynchronous reads.</param>
    /// <returns>Exact chunk bounds and the canonical occupied 256-block native-tile inventory.</returns>
    /// <exception cref="InputValidationException">Storage is malformed, unsupported, truncated, or contains no readable occupied chunks.</exception>
    /// <exception cref="InputSecurityException">Allocations overlap, coordinates disagree, entries are duplicated or linked, or a safety limit is exceeded.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    /// <remarks>
    /// Region-slot or legacy-path coordinates must match each chunk's NBT <c>xPos</c> and <c>zPos</c> before the
    /// chunk contributes to bounds. Chunk coordinates map to native tiles by floor division by 16, including for negatives.
    /// </remarks>
    public static async Task<WorldBounds> InspectAsync(
        string dimensionRoot,
        IngestLimits limits,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(dimensionRoot);
        var chunks = new HashSet<(int X, int Z)>();
        var skippedRegions = 0;
        var skippedChunks = 0;
        var regionRoot = Path.Combine(root, "region");
        if (Directory.Exists(regionRoot))
        {
            foreach (var path in Directory.EnumerateFiles(regionRoot, "r.*.*.*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var match = RegionNamePattern().Match(Path.GetFileName(path));
                if (!match.Success)
                    continue;
                RejectReparsePoint(path);
                var regionX = ParseCoordinate(match.Groups[1].Value);
                var regionZ = ParseCoordinate(match.Groups[2].Value);
                try
                {
                    skippedChunks += await InspectRegionAsync(path, regionX, regionZ, chunks, limits, cancellationToken);
                }
                catch (InputValidationException)
                {
                    // A structurally corrupt or truncated region (interrupted download, half-written file) is
                    // skipped so the remaining good regions still ingest. Security anomalies still propagate.
                    skippedRegions++;
                }
            }
        }

        if (chunks.Count == 0)
        {
            foreach (var path in Directory.EnumerateFiles(root, "c.*.dat", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var match = LegacyChunkNamePattern().Match(Path.GetFileName(path));
                if (!match.Success)
                    continue;
                RejectReparsePoint(path);
                var expected = (X: ParseBase36(match.Groups[1].Value), Z: ParseBase36(match.Groups[2].Value));
                try
                {
                    await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var actual = await NbtSummaryReader.ReadChunkCoordinatesAsync(
                        input, 1, limits.MaxChunkNbtBytes, cancellationToken);
                    AddVerified(chunks, expected, actual, limits);
                }
                catch (Exception exception) when (exception is InputValidationException or EndOfStreamException)
                {
                    // Skip an unreadable legacy Alpha chunk file; keep the rest.
                    skippedChunks++;
                }
            }
        }

        if (chunks.Count == 0)
            throw new InputValidationException("Dimension contains no readable occupied chunks.");
        var nativeTiles = chunks
            .Select(value => (X: FloorDivide(value.X, 16), Z: FloorDivide(value.Z, 16)))
            .Distinct()
            .OrderBy(value => value.Z)
            .ThenBy(value => value.X)
            .ToArray();
        using var inventory = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var tile in nativeTiles)
            inventory.AppendData(Encoding.ASCII.GetBytes($"{tile.X},{tile.Z}\n"));
        return new WorldBounds(
            chunks.Min(value => value.X),
            chunks.Min(value => value.Z),
            chunks.Max(value => value.X),
            chunks.Max(value => value.Z),
            chunks.Count,
            nativeTiles.Length,
            Convert.ToHexStringLower(inventory.GetHashAndReset()),
            nativeTiles.Select(tile => new NativeTileCoordinate(tile.X, tile.Z)).ToArray(),
            skippedRegions,
            skippedChunks);
    }

    /// <summary>
    /// Reads one region's chunk slots, skipping structurally corrupt or truncated chunks (common in
    /// interrupted WDL downloads) while still hard-failing on security anomalies (overlapping
    /// allocations, coordinate tampering, reparse points, or resource-limit breaches).
    /// </summary>
    /// <returns>The number of chunk slots that were skipped as corrupt.</returns>
    private static async Task<int> InspectRegionAsync(
        string path,
        int regionX,
        int regionZ,
        HashSet<(int X, int Z)> chunks,
        IngestLimits limits,
        CancellationToken cancellationToken)
    {
        await using var region = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (region.Length < HeaderBytes)
            throw new InputValidationException($"Region file is smaller than its header: {path}");
        // Floor to whole sectors so a truncated final sector (partial download) drops only its own chunk.
        var sectorLength = checked((int)(region.Length / SectorBytes));
        var header = new byte[SectorBytes];
        await region.ReadExactlyAsync(header, cancellationToken);
        var occupiedSectors = new HashSet<int> { 0, 1 };
        var skippedChunks = 0;
        for (var index = 0; index < 1024; index++)
        {
            var offset = index * 4;
            var sectorOffset = header[offset] << 16 | header[offset + 1] << 8 | header[offset + 2];
            var sectorCount = header[offset + 3];
            if (sectorOffset == 0 && sectorCount == 0)
                continue;
            // Out-of-range allocations mean corruption/truncation: skip the slot, do not read it.
            if (sectorOffset < 2 || sectorCount == 0 || sectorOffset + sectorCount > sectorLength)
            {
                skippedChunks++;
                continue;
            }
            for (var sector = sectorOffset; sector < sectorOffset + sectorCount; sector++)
                if (!occupiedSectors.Add(sector))
                    throw new InputSecurityException($"Region header contains overlapping chunk allocations: {path}");

            var localX = index % 32;
            var localZ = index / 32;
            var expected = (X: checked(regionX * 32 + localX), Z: checked(regionZ * 32 + localZ));
            try
            {
                var actual = await ReadChunkCoordinatesAtAsync(
                    region, path, sectorOffset, sectorCount, expected, limits, cancellationToken);
                AddVerified(chunks, expected, actual, limits);
            }
            catch (Exception exception) when (exception is InputValidationException or EndOfStreamException)
            {
                // A single unreadable chunk (bad length, truncated payload, malformed/unsupported NBT, or a
                // missing external .mcc) is skipped so the rest of the region still contributes bounds.
                skippedChunks++;
            }
        }
        return skippedChunks;
    }

    /// <summary>Reads and decompresses one chunk's stored coordinates from its region slot or external file.</summary>
    private static async Task<ChunkCoordinates> ReadChunkCoordinatesAtAsync(
        FileStream region,
        string path,
        int sectorOffset,
        int sectorCount,
        (int X, int Z) expected,
        IngestLimits limits,
        CancellationToken cancellationToken)
    {
        region.Position = (long)sectorOffset * SectorBytes;
        var chunkHeader = new byte[5];
        await region.ReadExactlyAsync(chunkHeader, cancellationToken);
        var storedLength = BinaryPrimitives.ReadInt32BigEndian(chunkHeader.AsSpan(0, 4));
        var external = (chunkHeader[4] & 0x80) != 0;
        var compression = (byte)(chunkHeader[4] & 0x7f);
        if (storedLength < 1 || storedLength > sectorCount * SectorBytes - 4)
            throw new InputValidationException($"Region chunk length exceeds its allocation: {path}");

        if (external)
        {
            var externalPath = Path.Combine(Path.GetDirectoryName(path)!, $"c.{expected.X}.{expected.Z}.mcc");
            if (!File.Exists(externalPath))
                throw new InputValidationException($"External chunk payload is missing: {externalPath}");
            RejectReparsePoint(externalPath);
            await using var externalInput = new FileStream(externalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (externalInput.Length > limits.MaxChunkNbtBytes)
                throw new InputSecurityException("External chunk payload exceeds the safety limit.");
            return await NbtSummaryReader.ReadChunkCoordinatesAsync(
                externalInput, compression, limits.MaxChunkNbtBytes, cancellationToken);
        }

        var payload = new byte[storedLength - 1];
        await region.ReadExactlyAsync(payload, cancellationToken);
        await using var payloadStream = new MemoryStream(payload, writable: false);
        return await NbtSummaryReader.ReadChunkCoordinatesAsync(
            payloadStream, compression, limits.MaxChunkNbtBytes, cancellationToken);
    }

    private static void AddVerified(
        HashSet<(int X, int Z)> chunks,
        (int X, int Z) expected,
        ChunkCoordinates actual,
        IngestLimits limits)
    {
        if (actual.X != expected.X || actual.Z != expected.Z)
            throw new InputSecurityException(
                $"Chunk coordinate mismatch: storage says {expected.X},{expected.Z}; NBT says {actual.X},{actual.Z}.");
        if (!chunks.Add(expected))
            throw new InputSecurityException($"Duplicate chunk coordinate: {expected.X},{expected.Z}.");
        if (chunks.Count > limits.MaxChunkCount)
            throw new InputSecurityException("Dimension exceeds the configured occupied-chunk limit.");
    }

    private static int ParseCoordinate(string value) =>
        int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) &&
        value == parsed.ToString(CultureInfo.InvariantCulture)
            ? parsed
            : throw new InputValidationException($"Invalid region coordinate: {value}");

    private static int FloorDivide(int value, int divisor)
    {
        var quotient = value / divisor;
        return value < 0 && value % divisor != 0 ? quotient - 1 : quotient;
    }

    private static int ParseBase36(string value)
    {
        var negative = value.StartsWith('-');
        var digits = negative ? value[1..] : value;
        if (digits.Length == 0) throw new InputValidationException("Invalid legacy chunk coordinate.");
        long result = 0;
        foreach (var character in digits)
        {
            var digit = character is >= '0' and <= '9' ? character - '0'
                : character is >= 'a' and <= 'z' ? character - 'a' + 10
                : -1;
            if (digit is < 0 or >= 36)
                throw new InputValidationException("Invalid legacy chunk coordinate.");
            result = checked(result * 36 + digit);
        }
        if (negative) result = -result;
        return checked((int)result);
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InputSecurityException($"Chunk storage contains a reparse point: {path}");
    }

    [GeneratedRegex(@"^r\.(-?(?:0|[1-9][0-9]*))\.(-?(?:0|[1-9][0-9]*))\.(?:mca|mcr)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex RegionNamePattern();

    [GeneratedRegex(@"^c\.(-?[0-9a-z]+)\.(-?[0-9a-z]+)\.dat$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex LegacyChunkNamePattern();
}