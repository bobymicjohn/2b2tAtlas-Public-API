using Atlas.Ingestor.Configuration;
using Atlas.Ingestor.Models;
using Atlas.Ingestor.Pipeline;
using Atlas.Ingestor.Rendering;
using Atlas.Ingestor.Security;

namespace Atlas.Ingestor.Publishing;

/// <summary>Binds a published tile destination to its job, render plan, dimension, and byte inventory.</summary>
/// <param name="JobId">Lowercase content-addressed job identifier.</param>
/// <param name="PlanSha256">SHA-256 of the render plan.</param>
/// <param name="Dimension">Published canonical dimension.</param>
/// <param name="TileRootRelativePath">Adapted subtree relative to the dimension render root.</param>
/// <param name="Destination">Absolute immutable publication path.</param>
/// <param name="Report">Verified post-publication tile inventory.</param>
/// <param name="PublishedAtUtc">UTC publication timestamp.</param>
public sealed record PublicationReceipt(
    string JobId,
    string PlanSha256,
    string Dimension,
    string TileRootRelativePath,
    string Destination,
    TileSetReport Report,
    DateTimeOffset PublishedAtUtc);

/// <summary>Authenticates adapted output, atomically publishes it, and verifies persisted publication receipts.</summary>
public static class PublicationService
{
    /// <summary>Verifies all upstream bindings, moves one adapted tile tree, revalidates it, and writes a receipt.</summary>
    /// <param name="paths">Canonical job artifact paths.</param>
    /// <param name="jobId">Expected content-addressed job identifier.</param>
    /// <param name="dimension">Canonical dimension to publish.</param>
    /// <param name="tileRootRelativePath">Adapted subtree relative to the dimension render root.</param>
    /// <param name="destination">New immutable publication directory; cross-volume copies are verified before promotion.</param>
    /// <param name="limits">Tile verification limits.</param>
    /// <param name="cancellationToken">Token that cancels artifact reads, hashing, and receipt persistence.</param>
    /// <returns>The persisted post-publication receipt.</returns>
    /// <exception cref="InputValidationException">The dimension, path, plan, destination, or tile layout is invalid.</exception>
    /// <exception cref="InputSecurityException">Adaptation, provenance, or pre/post-move inventories do not match.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    /// <remarks>
    /// The create-only directory move consumes the staged tile root. If later verification or receipt persistence fails,
    /// the destination remains present for operator inspection and is never silently overwritten.
    /// </remarks>
    public static async Task<PublicationReceipt> PublishAsync(
        JobPaths paths,
        string jobId,
        string dimension,
        string tileRootRelativePath,
        string destination,
        IngestLimits limits,
        CancellationToken cancellationToken)
    {
        ValidateDimension(dimension);
        var plan = await JobStore.ReadJsonAsync<RenderPlan>(paths.RenderPlan, cancellationToken);
        if (!plan.Dimensions.Any(value => value.Key == dimension))
            throw new InputValidationException($"Dimension is not in the render plan: {dimension}");
        await AtlasTileAdapter.VerifyReceiptAsync(
            paths, jobId, dimension, tileRootRelativePath, limits, cancellationToken);

        var planHash = await JobStore.ComputeSha256Async(paths.RenderPlan, cancellationToken);
        var provenance = await JobStore.ReadJsonAsync<RenderProvenance>(
            paths.RenderProvenance(dimension), cancellationToken);
        if (provenance.Status != "completed" ||
            provenance.Dimension != dimension ||
            !provenance.PlanSha256.Equals(planHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InputSecurityException("Completed renderer provenance does not match this job, plan, and dimension.");
        }

        var dimensionRoot = Path.Combine(paths.Render, dimension);
        var tileRoot = ResolveContainedDirectory(dimensionRoot, tileRootRelativePath);
        var destinationPath = Path.GetFullPath(destination);
        var adaptation = await JobStore.ReadJsonAsync<AdaptationReceipt>(
            paths.AdaptationReceipt(dimension), cancellationToken);
        var coordinateScheme = AtlasTileScheme.Parse(adaptation.Scheme).CoordinateScheme;
        var before = TilePublisher.Verify(tileRoot, limits, coordinateScheme);
        if (Path.GetPathRoot(tileRoot)!.Equals(Path.GetPathRoot(destinationPath), StringComparison.OrdinalIgnoreCase))
            TilePublisher.PublishAtomically(tileRoot, destinationPath);
        else
            await CopyVerifiedAtomicallyAsync(tileRoot, destinationPath, before, limits, cancellationToken);
        var after = TilePublisher.Verify(destinationPath, limits, coordinateScheme);
        if (!TilePublisher.ReportsMatch(before, after))
            throw new InputSecurityException("Published tile inventory changed during atomic publication.");

        var receipt = new PublicationReceipt(
            jobId.ToLowerInvariant(),
            planHash,
            dimension,
            NormalizeRelativePath(tileRootRelativePath),
            destinationPath,
            after,
            DateTimeOffset.UtcNow);
        await JobStore.WriteJsonAsync(paths.PublicationReceipt(dimension), receipt, cancellationToken);
        return receipt;
    }

    // Rendering stays on the scratch SSD. Only completed output crosses to the
    // serving volume, where a verified private directory is atomically promoted.
    internal static async Task CopyVerifiedAtomicallyAsync(
        string source, string destination, TileSetReport expected, IngestLimits limits,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new InputValidationException($"Published path already exists: {destination}");
        var staging = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            Directory.CreateDirectory(staging);
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = Path.Combine(staging, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1_048_576, true);
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1_048_576, true);
                await input.CopyToAsync(output, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var copied = TilePublisher.Verify(staging, limits, expected.CoordinateScheme);
            if (!TilePublisher.ReportsMatch(expected, copied))
                throw new InputSecurityException("Cross-volume publication failed tile inventory verification.");
            TilePublisher.PublishAtomically(staging, destination);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    /// <summary>Reads a publication receipt and re-hashes its current destination.</summary>
    /// <param name="paths">Canonical job artifact paths.</param>
    /// <param name="jobId">Expected content-addressed job identifier.</param>
    /// <param name="dimension">Expected canonical dimension.</param>
    /// <param name="limits">Tile verification limits.</param>
    /// <param name="cancellationToken">Token that cancels receipt, plan, and tile reads.</param>
    /// <returns>The receipt after all job and byte-inventory bindings pass.</returns>
    /// <exception cref="InputValidationException">The dimension, receipt, or tile tree is invalid.</exception>
    /// <exception cref="InputSecurityException">The receipt belongs to another job/plan or published bytes changed.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    /// <remarks>This method is read-only and is the required gate before registration or worker completion.</remarks>
    public static async Task<PublicationReceipt> ReadAndVerifyAsync(
        JobPaths paths,
        string jobId,
        string dimension,
        IngestLimits limits,
        CancellationToken cancellationToken)
    {
        ValidateDimension(dimension);
        var receipt = await JobStore.ReadJsonAsync<PublicationReceipt>(
            paths.PublicationReceipt(dimension), cancellationToken);
        var planHash = await JobStore.ComputeSha256Async(paths.RenderPlan, cancellationToken);
        if (!receipt.JobId.Equals(jobId, StringComparison.OrdinalIgnoreCase) ||
            !receipt.Dimension.Equals(dimension, StringComparison.Ordinal) ||
            !receipt.PlanSha256.Equals(planHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InputSecurityException("Publication receipt does not match this job and render plan.");
        }

        var current = TilePublisher.Verify(
            receipt.Destination, limits, receipt.Report.CoordinateScheme);
        if (!TilePublisher.ReportsMatch(current, receipt.Report))
            throw new InputSecurityException("Published tile inventory no longer matches its receipt.");
        return receipt;
    }

    private static string ResolveContainedDirectory(string root, string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        var rootPath = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(rootPath, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = Path.TrimEndingDirectorySeparator(rootPath) + Path.DirectorySeparatorChar;
        if (!candidate.Equals(rootPath, StringComparison.OrdinalIgnoreCase) &&
            !candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InputSecurityException("Tile root escapes the dimension render directory.");
        }
        return candidate;
    }

    private static string NormalizeRelativePath(string value)
    {
        var normalized = value.Replace('\\', '/').Trim();
        if (normalized is "" or ".")
            return ".";
        if (Path.IsPathRooted(normalized) || normalized.Split('/').Any(part => part is "" or "." or ".."))
            throw new InputValidationException("tile-root must be a safe relative directory path.");
        return normalized;
    }

    private static void ValidateDimension(string dimension)
    {
        if (dimension is not ("overworld" or "nether" or "end"))
            throw new InputValidationException("Dimension must be overworld, nether, or end.");
    }
}
