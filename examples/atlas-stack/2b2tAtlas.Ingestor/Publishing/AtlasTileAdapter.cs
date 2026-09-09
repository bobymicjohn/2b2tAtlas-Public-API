using System.Text.RegularExpressions;
using Atlas.Ingestor.Configuration;
using Atlas.Ingestor.Models;
using Atlas.Ingestor.Pipeline;
using Atlas.Ingestor.Rendering;
using Atlas.Ingestor.Security;
using SkiaSharp;

namespace Atlas.Ingestor.Publishing;

/// <summary>Defines a certified mapping from native uNmINeD tiles to an Atlas tile pyramid.</summary>
/// <param name="Key">Stable scheme identifier persisted in receipts.</param>
/// <param name="Dimension">Canonical Minecraft dimension supported by the scheme.</param>
/// <param name="TargetMaxZoom">Atlas URL zoom receiving native 256-block tiles.</param>
/// <param name="TileOffsetX">Offset added to native tile X.</param>
/// <param name="TileOffsetY">Offset added to native tile Z to produce Atlas tile Y.</param>
/// <param name="CoordinateScheme">Tile-coordinate validation policy.</param>
/// <param name="UrlZoomOffset">Offset subtracted when reporting Leaflet maximum native zoom.</param>
public sealed record AtlasTileScheme(
    string Key,
    string Dimension,
    int TargetMaxZoom,
    int TileOffsetX,
    int TileOffsetY,
    string CoordinateScheme,
    int UrlZoomOffset)
{
    /// <summary>Resolves a certified scheme by its stable identifier.</summary>
    /// <param name="value">Scheme identifier.</param>
    /// <returns>The exact dimension, zoom, offsets, and coordinate policy for the scheme.</returns>
    /// <exception cref="InputValidationException">The identifier is not a certified scheme.</exception>
    public static AtlasTileScheme Parse(string value) => value switch
    {
        "atlas-overworld-256k-v1" => new(value, "overworld", 10, 500, 500, TilePublisher.StandardXyzScheme, 1),
        "atlas-overworld-sparse-v1" => new(value, "overworld", 10, 500, 500, TilePublisher.SparseAtlasScheme, 1),
        "atlas-end-sparse-v1" => new(value, "end", 8, 82, 82, TilePublisher.SparseAtlasScheme, 0),
        "atlas-nether-sparse-v1" => new(value, "nether", 8, 84, 84, TilePublisher.SparseAtlasScheme, 0),
        _ => throw new InputValidationException($"Unknown Atlas tile scheme: {value}"),
    };
}

/// <summary>Binds adapted tile bytes to the job, render provenance, dimension, and coordinate scheme.</summary>
/// <param name="JobId">Lowercase content-addressed job identifier.</param>
/// <param name="PlanSha256">SHA-256 of the render plan used for adaptation.</param>
/// <param name="RenderProvenanceSha256">SHA-256 of completed renderer provenance.</param>
/// <param name="Dimension">Adapted canonical dimension.</param>
/// <param name="Scheme">Certified Atlas tile scheme identifier.</param>
/// <param name="OutputRelativePath">Adapted subtree relative to the dimension render root.</param>
/// <param name="Report">Verified deterministic tile inventory.</param>
/// <param name="AdaptedAtUtc">UTC completion timestamp.</param>
public sealed record AdaptationReceipt(
    string JobId,
    string PlanSha256,
    string RenderProvenanceSha256,
    string Dimension,
    string Scheme,
    string OutputRelativePath,
    TileSetReport Report,
    DateTimeOffset AdaptedAtUtc);

/// <summary>Adapts native uNmINeD PNGs into a verified Atlas pyramid.</summary>
/// <remarks>
/// Native tile coordinates are offset according to a certified scheme. Every chunk-derived native tile is required;
/// unexpected renderer tiles are accepted only when fully transparent, and parent coordinates use floor division so
/// signed sparse coordinates remain correct.
/// </remarks>
public static partial class AtlasTileAdapter
{
    /// <summary>Relative directory name used for adapted Atlas tiles.</summary>
    public const string OutputRelativePath = "atlas-tiles";

    /// <summary>Authenticates render provenance, adapts one job dimension, and writes its receipt.</summary>
    /// <param name="paths">Canonical job artifact paths.</param>
    /// <param name="jobId">Content-addressed job identifier.</param>
    /// <param name="dimension">Canonical dimension to adapt.</param>
    /// <param name="schemeName">Certified Atlas tile scheme identifier.</param>
    /// <param name="limits">Renderer-output and adapted-output limits.</param>
    /// <param name="cancellationToken">Token checked during reads, copies, and parent generation.</param>
    /// <returns>The persisted receipt for the verified adapted tile set.</returns>
    /// <exception cref="InputValidationException">The scheme, dimension, plan, renderer output, or image layout is invalid.</exception>
    /// <exception cref="InputSecurityException">Provenance, coordinates, visible output, or occupied native inventory does not match expectations.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    /// <remarks>Creates the adapted output subtree and atomically writes an adaptation receipt.</remarks>
    public static async Task<AdaptationReceipt> AdaptJobAsync(
        JobPaths paths,
        string jobId,
        string dimension,
        string schemeName,
        IngestLimits limits,
        CancellationToken cancellationToken)
    {
        var scheme = AtlasTileScheme.Parse(schemeName);
        if (scheme.Dimension != dimension)
            throw new InputValidationException($"Tile scheme {scheme.Key} does not support dimension {dimension}.");

        var plan = await JobStore.ReadJsonAsync<RenderPlan>(paths.RenderPlan, cancellationToken);
        var dimensionInfo = plan.Dimensions.SingleOrDefault(value => value.Key == dimension);
        if (dimensionInfo is null)
            throw new InputValidationException($"Dimension is not in the render plan: {dimension}");
        var planHash = await JobStore.ComputeSha256Async(paths.RenderPlan, cancellationToken);
        var provenancePath = paths.RenderProvenance(dimension);
        var provenance = await JobStore.ReadJsonAsync<RenderProvenance>(provenancePath, cancellationToken);
        if (provenance.Status != "completed" ||
            provenance.Dimension != dimension ||
            !provenance.PlanSha256.Equals(planHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InputSecurityException("Completed renderer provenance does not match this job, plan, and dimension.");
        }

        var dimensionRoot = Path.Combine(paths.Render, dimension);
        var report = await AdaptAsync(
            Path.Combine(dimensionRoot, "tiles", "zoom.0"),
            Path.Combine(dimensionRoot, OutputRelativePath),
            scheme,
            limits,
                cancellationToken,
                dimensionInfo.Bounds);
        var receipt = new AdaptationReceipt(
            jobId.ToLowerInvariant(),
            planHash,
            await JobStore.ComputeSha256Async(provenancePath, cancellationToken),
            dimension,
            scheme.Key,
            OutputRelativePath,
            report,
            DateTimeOffset.UtcNow);
        await JobStore.WriteJsonAsync(paths.AdaptationReceipt(dimension), receipt, cancellationToken);
        return receipt;
    }

    /// <summary>Revalidates an adaptation receipt against current plan, provenance, path, and tile bytes.</summary>
    /// <param name="paths">Canonical job artifact paths.</param>
    /// <param name="jobId">Expected content-addressed job identifier.</param>
    /// <param name="dimension">Expected canonical dimension.</param>
    /// <param name="tileRootRelativePath">Expected adapted subtree relative to the dimension render root.</param>
    /// <param name="limits">Tile verification limits.</param>
    /// <param name="cancellationToken">Token that cancels receipt and hash reads.</param>
    /// <returns>A task that completes only when all bindings and tile bytes match.</returns>
    /// <exception cref="InputSecurityException">Any receipt binding or deterministic tile inventory differs.</exception>
    /// <exception cref="InputValidationException">A receipt, scheme, or tile-tree value is invalid.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public static async Task VerifyReceiptAsync(
        JobPaths paths,
        string jobId,
        string dimension,
        string tileRootRelativePath,
        IngestLimits limits,
        CancellationToken cancellationToken)
    {
        var receipt = await JobStore.ReadJsonAsync<AdaptationReceipt>(
            paths.AdaptationReceipt(dimension), cancellationToken);
        var planHash = await JobStore.ComputeSha256Async(paths.RenderPlan, cancellationToken);
        var provenanceHash = await JobStore.ComputeSha256Async(paths.RenderProvenance(dimension), cancellationToken);
        if (!receipt.JobId.Equals(jobId, StringComparison.OrdinalIgnoreCase) ||
            receipt.Dimension != dimension ||
            !receipt.PlanSha256.Equals(planHash, StringComparison.OrdinalIgnoreCase) ||
            !receipt.RenderProvenanceSha256.Equals(provenanceHash, StringComparison.OrdinalIgnoreCase) ||
            receipt.OutputRelativePath != tileRootRelativePath.Replace('\\', '/').Trim())
        {
            throw new InputSecurityException("Tile adaptation receipt does not match this job, render, dimension, and tile root.");
        }

        var scheme = AtlasTileScheme.Parse(receipt.Scheme);
        var current = TilePublisher.Verify(
            Path.Combine(paths.Render, dimension, receipt.OutputRelativePath), limits, scheme.CoordinateScheme);
        if (!TilePublisher.ReportsMatch(current, receipt.Report))
            throw new InputSecurityException("Adapted tile inventory no longer matches its receipt.");
    }

            /// <summary>Copies certified native tiles, builds all parent zooms, and verifies the resulting pyramid.</summary>
            /// <param name="sourceRoot">uNmINeD <c>zoom.0</c> directory.</param>
            /// <param name="outputRoot">Final Atlas tile output directory, which must not exist.</param>
            /// <param name="scheme">Certified coordinate mapping.</param>
            /// <param name="limits">Source traversal and output limits.</param>
            /// <param name="cancellationToken">Token checked throughout traversal and parent generation.</param>
            /// <param name="expectedBounds">Optional authoritative occupied native-tile inventory.</param>
            /// <returns>A task containing the verified deterministic tile report.</returns>
            /// <exception cref="InputValidationException">The source layout, image format, coordinates, or destination is invalid.</exception>
            /// <exception cref="InputSecurityException">Output exceeds limits or fails collision, visibility, inventory, or zoom checks.</exception>
            /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
            /// <remarks>A partial output directory is deleted on every failure and renamed to <paramref name="outputRoot"/> only after verification.</remarks>
    public static Task<TileSetReport> AdaptAsync(
        string sourceRoot,
        string outputRoot,
        AtlasTileScheme scheme,
        IngestLimits limits,
        CancellationToken cancellationToken,
        WorldBounds? expectedBounds = null)
    {
        if (!Directory.Exists(sourceRoot))
            throw new InputValidationException($"uNmINeD zoom.0 tile root does not exist: {sourceRoot}");
        if (Directory.Exists(outputRoot) || File.Exists(outputRoot))
            throw new InputValidationException($"Atlas tile output already exists: {outputRoot}");

        var outputParent = Directory.GetParent(Path.GetFullPath(outputRoot))
            ?? throw new InputValidationException("Atlas tile output requires a parent directory.");
        outputParent.Create();
        var temporary = Path.Combine(outputParent.FullName, $".{Path.GetFileName(outputRoot)}.{Guid.NewGuid():N}.partial");
        try
        {
            Directory.CreateDirectory(temporary);
            var current = CopyMaximumZoom(sourceRoot, temporary, scheme, limits, cancellationToken, expectedBounds);
            for (var zoom = scheme.TargetMaxZoom - 1; zoom >= 0; zoom--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                current = BuildParentZoom(temporary, zoom, current, limits, cancellationToken);
            }

            var report = TilePublisher.Verify(temporary, limits, scheme.CoordinateScheme);
            if (report.MaxZoom != scheme.TargetMaxZoom)
                throw new InputSecurityException("Adapted tile pyramid has an unexpected maximum zoom.");
            Directory.Move(temporary, outputRoot);
            return Task.FromResult(report);
        }
        catch
        {
            if (Directory.Exists(temporary))
                Directory.Delete(temporary, recursive: true);
            throw;
        }
    }

    private static Dictionary<(int X, int Y), string> CopyMaximumZoom(
        string sourceRoot,
        string temporary,
        AtlasTileScheme scheme,
        IngestLimits limits,
        CancellationToken cancellationToken,
        WorldBounds? expectedBounds)
    {
        var result = new Dictionary<(int X, int Y), string>();
        var nativeCoordinates = new HashSet<(int X, int Z)>();
        var seenNativeCoordinates = new HashSet<(int X, int Z)>();
        var expectedNativeCoordinates = expectedBounds?.NativeTiles
            .Select(tile => (tile.X, tile.Z))
            .ToHashSet();
        var sourceEntries = 0;
        foreach (var xGroupPath in Directory.EnumerateFileSystemEntries(sourceRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CountEntry(ref sourceEntries, limits);
            RejectReparsePoint(xGroupPath);
            var xGroupName = Path.GetFileName(xGroupPath);
            if (xGroupName == "metadata" && Directory.Exists(xGroupPath))
                continue;
            if (!Directory.Exists(xGroupPath) || !TryParseCanonicalInteger(xGroupName, out var xGroup))
                throw new InputValidationException($"Unexpected uNmINeD zoom directory entry: {xGroupName}");

            foreach (var yGroupPath in Directory.EnumerateFileSystemEntries(xGroupPath))
            {
                CountEntry(ref sourceEntries, limits);
                RejectReparsePoint(yGroupPath);
                var yGroupName = Path.GetFileName(yGroupPath);
                if (!Directory.Exists(yGroupPath) || !TryParseCanonicalInteger(yGroupName, out var yGroup))
                    throw new InputValidationException($"Unexpected uNmINeD tile-group entry: {yGroupName}");

                foreach (var sourceTile in Directory.EnumerateFileSystemEntries(yGroupPath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    CountEntry(ref sourceEntries, limits);
                    RejectReparsePoint(sourceTile);
                    if (Directory.Exists(sourceTile))
                        throw new InputValidationException($"Unexpected directory in uNmINeD tile group: {sourceTile}");
                    var match = NativeTilePattern().Match(Path.GetFileName(sourceTile));
                    if (!match.Success ||
                        !TryParseCanonicalInteger(match.Groups[1].Value, out var nativeX) ||
                        !TryParseCanonicalInteger(match.Groups[2].Value, out var nativeY) ||
                        FloorDivide(nativeX, 10) != xGroup ||
                        FloorDivide(nativeY, 10) != yGroup)
                    {
                        throw new InputValidationException($"Invalid uNmINeD tile path: {sourceTile}");
                    }

                    var targetX = checked(nativeX + scheme.TileOffsetX);
                    var targetY = checked(nativeY + scheme.TileOffsetY);
                    if (!seenNativeCoordinates.Add((nativeX, nativeY)))
                        throw new InputSecurityException($"Renderer output duplicates native tile {nativeX},{nativeY}.");
                    var coordinateLimit = 1 << scheme.TargetMaxZoom;
                    if (scheme.CoordinateScheme == TilePublisher.StandardXyzScheme &&
                        (targetX < 0 || targetY < 0 || targetX >= coordinateLimit || targetY >= coordinateLimit))
                        throw new InputValidationException($"Native tile is outside scheme {scheme.Key}: {nativeX},{nativeY}");
                    using var input = File.OpenRead(sourceTile);
                    using var codec = SKCodec.Create(input)
                        ?? throw new InputValidationException($"Native tile is not a valid image: {sourceTile}");
                    if (codec.Info.Width != 256 || codec.Info.Height != 256 ||
                        codec.EncodedFormat != SKEncodedImageFormat.Png)
                        throw new InputValidationException($"Native tile is not 256x256 pixels: {sourceTile}");
                    if (expectedNativeCoordinates is not null &&
                        !expectedNativeCoordinates.Contains((nativeX, nativeY)))
                    {
                        if (!IsFullyTransparent(sourceTile))
                            throw new InputSecurityException(
                                $"Renderer produced visible pixels outside occupied chunks at {nativeX},{nativeY}.");
                        continue;
                    }
                    nativeCoordinates.Add((nativeX, nativeY));
                    if (result.ContainsKey((targetX, targetY)))
                        throw new InputSecurityException($"Native tiles collide at Atlas coordinate {targetX},{targetY}.");
                    var target = TilePath(temporary, scheme.TargetMaxZoom, targetX, targetY);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(sourceTile, target);
                    result.Add((targetX, targetY), target);
                }
            }
        }
        if (result.Count == 0)
            throw new InputValidationException("uNmINeD zoom.0 output contains no native PNG tiles.");
        if (expectedBounds is not null)
            VerifyNativeInventory(nativeCoordinates, expectedBounds);
        return result;
    }

    private static bool IsFullyTransparent(string path)
    {
        using var bitmap = SKBitmap.Decode(path)
            ?? throw new InputValidationException($"Native tile is not a valid image: {path}");
        return bitmap.Pixels.All(pixel => pixel.Alpha == 0);
    }

    private static void VerifyNativeInventory(
        IReadOnlyCollection<(int X, int Z)> nativeCoordinates,
        WorldBounds expectedBounds)
    {
        using var inventory = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (var tile in nativeCoordinates.OrderBy(value => value.Z).ThenBy(value => value.X))
            inventory.AppendData(System.Text.Encoding.ASCII.GetBytes($"{tile.X},{tile.Z}\n"));
        var actualHash = Convert.ToHexStringLower(inventory.GetHashAndReset());
        if (nativeCoordinates.Count != expectedBounds.NativeTileCount ||
            !actualHash.Equals(expectedBounds.NativeTileInventorySha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InputSecurityException(
                "Renderer native tile coordinates do not match the occupied chunk inventory.");
        }
    }

    private static Dictionary<(int X, int Y), string> BuildParentZoom(
        string root,
        int zoom,
        IReadOnlyDictionary<(int X, int Y), string> children,
        IngestLimits limits,
        CancellationToken cancellationToken)
    {
        var parents = children.Keys
            .Select(value => (X: FloorDivide(value.X, 2), Y: FloorDivide(value.Y, 2)))
            .Distinct()
            .OrderBy(value => value.Y)
            .ThenBy(value => value.X)
            .ToArray();
        if (parents.Length > limits.MaxOutputEntries)
            throw new InputSecurityException("Adapted output exceeds the configured entry-count limit.");
        var result = new Dictionary<(int X, int Y), string>(parents.Length);
        foreach (var parent in parents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var mosaic = new SKBitmap(new SKImageInfo(512, 512, SKColorType.Rgba8888, SKAlphaType.Premul));
            mosaic.Erase(SKColors.Transparent);
            using var mosaicCanvas = new SKCanvas(mosaic);
            for (var childY = 0; childY < 2; childY++)
            {
                for (var childX = 0; childX < 2; childX++)
                {
                    if (!children.TryGetValue((parent.X * 2 + childX, parent.Y * 2 + childY), out var childPath))
                        continue;
                    using var child = DecodeBitmap(childPath);
                    mosaicCanvas.DrawBitmap(
                        child,
                        childX * 256,
                        childY * 256,
                        new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
                }
            }
            using var parentBitmap = new SKBitmap(new SKImageInfo(256, 256, SKColorType.Rgba8888, SKAlphaType.Premul));
            using var parentCanvas = new SKCanvas(parentBitmap);
            parentCanvas.Clear(SKColors.Transparent);
            parentCanvas.DrawBitmap(
                mosaic,
                new SKRect(0, 0, 512, 512),
                new SKRect(0, 0, 256, 256),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            var target = TilePath(root, zoom, parent.X, parent.Y);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var image = SKImage.FromBitmap(parentBitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            encoded.SaveTo(output);
            result.Add(parent, target);
        }
        return result;
    }

    private static SKBitmap DecodeBitmap(string path)
    {
        using var input = File.OpenRead(path);
        using var codec = SKCodec.Create(input)
            ?? throw new InputValidationException($"Adapted child tile is not a valid image: {path}");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        if (codec.GetPixels(info, bitmap.GetPixels()) != SKCodecResult.Success)
        {
            bitmap.Dispose();
            throw new InputValidationException($"Adapted child tile pixels could not be decoded: {path}");
        }
        return bitmap;
    }

    private static string TilePath(string root, int zoom, int x, int y) =>
        Path.Combine(root, zoom.ToString(), y.ToString(), $"{x}.png");

    private static bool TryParseCanonicalInteger(string value, out int parsed) =>
        int.TryParse(value, System.Globalization.NumberStyles.AllowLeadingSign,
            System.Globalization.CultureInfo.InvariantCulture, out parsed) &&
        value == parsed.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static int FloorDivide(int value, int divisor)
    {
        var quotient = value / divisor;
        return value < 0 && value % divisor != 0 ? quotient - 1 : quotient;
    }

    private static void CountEntry(ref int count, IngestLimits limits)
    {
        count = checked(count + 1);
        if (count > limits.MaxOutputEntries)
            throw new InputSecurityException("Native renderer output exceeds the configured entry-count limit.");
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InputSecurityException($"Native renderer output contains a reparse point: {path}");
    }

    [GeneratedRegex("^tile\\.(-?(?:0|[1-9][0-9]*))\\.(-?(?:0|[1-9][0-9]*))\\.png$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex NativeTilePattern();
}