using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Atlas.Ingestor.Configuration;
using Atlas.Ingestor.Security;

namespace Atlas.Ingestor.Publishing;

/// <summary>Describes a deterministic, byte-bound tile-tree inventory.</summary>
/// <param name="TileCount">Number of image tiles.</param>
/// <param name="TotalBytes">Aggregate tile byte length.</param>
/// <param name="MinZoom">Minimum present zoom.</param>
/// <param name="MaxZoom">Maximum present zoom.</param>
/// <param name="TilesPerZoom">Tile count keyed by zoom.</param>
/// <param name="InventorySha256">SHA-256 over ordered relative paths, lengths, and file bytes.</param>
/// <param name="CoordinateScheme">Coordinate validation policy used for this inventory.</param>
/// <param name="MinTileX">Minimum tile X across all zooms.</param>
/// <param name="MinTileY">Minimum tile Y across all zooms.</param>
/// <param name="MaxTileX">Maximum tile X across all zooms.</param>
/// <param name="MaxTileY">Maximum tile Y across all zooms.</param>
public sealed record TileSetReport(
    int TileCount,
    long TotalBytes,
    int MinZoom,
    int MaxZoom,
    IReadOnlyDictionary<int, int> TilesPerZoom,
    string InventorySha256,
    string CoordinateScheme = TilePublisher.StandardXyzScheme,
    long MinTileX = 0,
    long MinTileY = 0,
    long MaxTileX = 0,
    long MaxTileY = 0);

/// <summary>Verifies immutable tile-only trees and promotes them by an atomic directory move.</summary>
public static partial class TilePublisher
{
    /// <summary>Coordinate policy requiring unsigned XYZ coordinates within each zoom's standard extent.</summary>
    public const string StandardXyzScheme = "xyz-v1";
    /// <summary>Coordinate policy allowing canonical signed Atlas coordinates outside standard XYZ extents.</summary>
    public const string SparseAtlasScheme = "atlas-sparse-v1";

    /// <summary>Validates a tile-only tree and hashes its ordered paths, lengths, and bytes.</summary>
    /// <param name="renderedRoot">Root laid out as <c>z/y/x.extension</c>.</param>
    /// <param name="limits">Entry-count and byte limits.</param>
    /// <param name="coordinateScheme">Coordinate validation policy.</param>
    /// <returns>A deterministic report suitable for later byte-for-byte comparison.</returns>
    /// <exception cref="InputValidationException">The tree contains unknown entries, invalid coordinates, mixed extensions, empty levels, or missing zooms.</exception>
    /// <exception cref="InputSecurityException">The tree contains reparse points or exceeds entry or byte limits.</exception>
    /// <remarks>Verification reads every tile byte and has no file-system side effects.</remarks>
    public static TileSetReport Verify(
        string renderedRoot,
        IngestLimits limits,
        string coordinateScheme = StandardXyzScheme)
    {
        var allowSignedCoordinates = coordinateScheme switch
        {
            StandardXyzScheme => false,
            SparseAtlasScheme => true,
            _ => throw new InputValidationException($"Unknown tile coordinate scheme: {coordinateScheme}"),
        };
        if (!Directory.Exists(renderedRoot))
            throw new InputValidationException($"Rendered tile root does not exist: {renderedRoot}");

        var root = Path.GetFullPath(renderedRoot);
        var count = 0;
        long bytes = 0;
        var minZoom = int.MaxValue;
        var maxZoom = int.MinValue;
        var tilesPerZoom = new Dictionary<int, int>();
        string? tileExtension = null;
        var outputEntries = 0;
        long minTileX = long.MaxValue, minTileY = long.MaxValue;
        long maxTileX = long.MinValue, maxTileY = long.MinValue;
        using var inventoryHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var zoomDirectories = new List<(int Zoom, string Path)>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            CountOutputEntry(ref outputEntries, limits);
            RejectReparsePoint(entry);
            var zoomName = Path.GetFileName(entry);
            if (!Directory.Exists(entry) ||
                !int.TryParse(zoomName, out var zoom) ||
                zoom is < 0 or > 30 ||
                !IsCanonicalInteger(zoomName, zoom))
                throw new InputValidationException($"Tile root contains an unexpected entry: {Path.GetRelativePath(root, entry)}");
            zoomDirectories.Add((zoom, entry));
        }

        foreach (var (zoom, zoomPath) in zoomDirectories.OrderBy(value => value.Zoom))
        {
            var yDirectories = new List<(long Y, string Path)>();
            foreach (var entry in Directory.EnumerateFileSystemEntries(zoomPath))
            {
                CountOutputEntry(ref outputEntries, limits);
                RejectReparsePoint(entry);
                var yName = Path.GetFileName(entry);
                if (!Directory.Exists(entry) ||
                    !long.TryParse(yName, out var y) ||
                    (!allowSignedCoordinates && (y < 0 || y >= 1L << zoom)) ||
                    !IsCanonicalInteger(yName, y))
                    throw new InputValidationException($"Tile root contains an unexpected entry: {Path.GetRelativePath(root, entry)}");
                yDirectories.Add((y, entry));
            }
            if (yDirectories.Count == 0)
                throw new InputValidationException($"Tile root contains an empty zoom directory: {zoom}");

            foreach (var (y, yPath) in yDirectories.OrderBy(value => value.Y))
            {
                var tiles = new List<(long X, string Path, string Relative)>();
                foreach (var entry in Directory.EnumerateFileSystemEntries(yPath))
                {
                    CountOutputEntry(ref outputEntries, limits);
                    RejectReparsePoint(entry);
                    if (Directory.Exists(entry))
                        throw new InputValidationException($"Tile root contains an unexpected directory: {Path.GetRelativePath(root, entry)}");
                    var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
                    var match = TilePattern().Match(relative);
                    if (!match.Success)
                        throw new InputValidationException($"Tile root contains an unexpected non-tile file: {relative}");
                    var xName = match.Groups[3].Value;
                    var x = long.Parse(xName, System.Globalization.CultureInfo.InvariantCulture);
                    if ((!allowSignedCoordinates && (x < 0 || x >= 1L << zoom)) || !IsCanonicalInteger(xName, x))
                        throw new InputValidationException($"Tile coordinate is outside XYZ bounds: {relative}");
                    var extension = match.Groups[4].Value.ToLowerInvariant();
                    tileExtension ??= extension;
                    if (tileExtension != extension)
                        throw new InputValidationException("Tile root must use one consistent image extension.");
                    tiles.Add((x, entry, relative));
                }
                if (tiles.Count == 0)
                    throw new InputValidationException($"Tile root contains an empty tile row: {zoom}/{y}");

                long? previousX = null;
                foreach (var tile in tiles.OrderBy(value => value.X))
                {
                    if (previousX == tile.X)
                        throw new InputValidationException($"Tile root contains duplicate XYZ coordinates: {tile.Relative}");
                    previousX = tile.X;
                    var tileBytes = AppendTileToInventory(inventoryHash, tile.Relative, tile.Path);
                    count++;
                    minTileX = Math.Min(minTileX, tile.X);
                    minTileY = Math.Min(minTileY, y);
                    maxTileX = Math.Max(maxTileX, tile.X);
                    maxTileY = Math.Max(maxTileY, y);
                    bytes = checked(bytes + tileBytes);
                    if (bytes > limits.MaxOutputBytes)
                        throw new InputSecurityException("Rendered output exceeds the configured size limit.");
                    minZoom = Math.Min(minZoom, zoom);
                    maxZoom = Math.Max(maxZoom, zoom);
                    tilesPerZoom[zoom] = tilesPerZoom.GetValueOrDefault(zoom) + 1;
                }
            }
        }

        if (count == 0)
            throw new InputValidationException("Renderer output contains no recognized z/y/x image tiles.");
        if (minZoom != 0)
            throw new InputValidationException("Atlas map pyramids must include zoom 0.");
        for (var zoom = minZoom; zoom <= maxZoom; zoom++)
        {
            if (!tilesPerZoom.ContainsKey(zoom))
                throw new InputValidationException($"Tile pyramid is missing zoom level {zoom}.");
        }
        return new TileSetReport(
            count,
            bytes,
            minZoom,
            maxZoom,
            tilesPerZoom,
            Convert.ToHexStringLower(inventoryHash.GetHashAndReset()),
            coordinateScheme,
            minTileX,
            minTileY,
            maxTileX,
            maxTileY);
    }

            /// <summary>Compares all semantic and byte-inventory fields of two tile reports.</summary>
            /// <param name="left">First report.</param>
            /// <param name="right">Second report.</param>
            /// <returns><see langword="true"/> when both reports bind the same tile tree.</returns>
    public static bool ReportsMatch(TileSetReport left, TileSetReport right) =>
        left.TileCount == right.TileCount &&
        left.TotalBytes == right.TotalBytes &&
        left.MinZoom == right.MinZoom &&
        left.MaxZoom == right.MaxZoom &&
        left.CoordinateScheme == right.CoordinateScheme &&
        left.MinTileX == right.MinTileX && left.MinTileY == right.MinTileY &&
        left.MaxTileX == right.MaxTileX && left.MaxTileY == right.MaxTileY &&
        left.InventorySha256.Equals(right.InventorySha256, StringComparison.OrdinalIgnoreCase) &&
        left.TilesPerZoom.OrderBy(pair => pair.Key).SequenceEqual(
            right.TilesPerZoom.OrderBy(pair => pair.Key));

    private static void CountOutputEntry(ref int count, IngestLimits limits)
    {
        count = checked(count + 1);
        if (count > limits.MaxOutputEntries)
            throw new InputSecurityException("Rendered output exceeds the configured entry-count limit.");
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InputSecurityException($"Rendered output contains a symbolic link or reparse point: {path}");
    }

    private static bool IsCanonicalInteger(string value, long parsed) =>
        value == parsed.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static long AppendTileToInventory(IncrementalHash inventoryHash, string relativePath, string tilePath)
    {
        inventoryHash.AppendData(Encoding.UTF8.GetBytes(relativePath));
        inventoryHash.AppendData([0]);
        using var input = new FileStream(
            tilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.SequentialScan);
        Span<byte> length = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(length, input.Length);
        inventoryHash.AppendData(length);
        var buffer = new byte[1024 * 1024];
        while (true)
        {
            var read = input.Read(buffer);
            if (read == 0)
                break;
            inventoryHash.AppendData(buffer.AsSpan(0, read));
        }
        inventoryHash.AppendData([byte.MaxValue]);
        return input.Length;
    }

    /// <summary>Publishes a staged tile directory with a create-only atomic move.</summary>
    /// <param name="renderedRoot">Existing staged tile directory.</param>
    /// <param name="publishedRoot">New immutable destination path.</param>
    /// <exception cref="InputValidationException">The destination exists or source and destination are on different volumes.</exception>
    /// <remarks>The source directory is consumed. Callers must verify before and after the move and write a receipt separately.</remarks>
    public static void PublishAtomically(string renderedRoot, string publishedRoot)
    {
        var source = Path.GetFullPath(renderedRoot);
        var destination = Path.GetFullPath(publishedRoot);
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new InputValidationException($"Published path already exists: {destination}");
        if (!Path.GetPathRoot(source)!.Equals(Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase))
            throw new InputValidationException("Atomic publication requires staging and destination on the same volume.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        Directory.Move(source, destination);
    }

    [GeneratedRegex(@"^([0-9]{1,2})/(-?(?:0|[1-9][0-9]*))/(-?(?:0|[1-9][0-9]*))\.(png|webp|jpg|jpeg)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex TilePattern();
}
