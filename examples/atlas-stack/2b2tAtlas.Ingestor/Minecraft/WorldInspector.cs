using Atlas.Ingestor.Configuration;
using Atlas.Ingestor.Models;
using Atlas.Ingestor.Security;

namespace Atlas.Ingestor.Minecraft;

/// <summary>Discovers and inspects recognized Minecraft Java worlds and dimensions.</summary>
public static class WorldInspector
{
    private static readonly (string Key, int RendererId, string[] RelativePaths)[] Dimensions =
    [
        ("overworld", 0, [".", "dimensions/minecraft/overworld"]),
        ("nether", 1, ["DIM-1", "dimensions/minecraft/the_nether"]),
        ("end", 2, ["DIM1", "dimensions/minecraft/the_end"]),
    ];

    /// <summary>Finds shallow candidate world roots from level metadata or canonical region storage.</summary>
    /// <param name="extractedPath">Root of the safely extracted archive.</param>
    /// <param name="limits">Limits including the maximum candidate-world count.</param>
    /// <returns>Absolute candidate roots ordered by depth and then path.</returns>
    /// <exception cref="InputValidationException">The candidate count exceeds <see cref="IngestLimits.MaxWorlds"/>.</exception>
    /// <remarks>Deep paths and macOS metadata trees are ignored; discovery alone does not establish world provenance.</remarks>
    public static IReadOnlyList<string> FindWorldRoots(string extractedPath, IngestLimits limits)
    {
        var extracted = Path.GetFullPath(extractedPath);
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var levelDat in Directory.EnumerateFiles(extracted, "level.dat", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(extracted, levelDat);
            var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Length > 7 || parts.Contains("__MACOSX", StringComparer.OrdinalIgnoreCase))
                continue;
            roots.Add(Path.GetDirectoryName(levelDat)!);
            if (roots.Count > limits.MaxWorlds)
                throw new InputValidationException($"Archive contains more than {limits.MaxWorlds} candidate worlds.");
        }

        // Old WorldTools exports sometimes omitted level.dat even though their region files are usable.
        // Recover only structurally recognizable roots; the immutable ZIP remains untouched.
        foreach (var regionDirectory in Directory.EnumerateDirectories(extracted, "region", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(extracted, regionDirectory);
            var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Length > 8 || parts.Contains("__MACOSX", StringComparer.OrdinalIgnoreCase) ||
                !Directory.EnumerateFiles(regionDirectory, "r.*.*.mca", SearchOption.TopDirectoryOnly).Any() &&
                !Directory.EnumerateFiles(regionDirectory, "r.*.*.mcr", SearchOption.TopDirectoryOnly).Any())
                continue;
            var regionParent = Path.GetDirectoryName(regionDirectory)!;
            string candidate;
            var dimensionsIndex = Array.FindIndex(parts, part => part.Equals("dimensions", StringComparison.OrdinalIgnoreCase));
            if (dimensionsIndex >= 0)
            {
                candidate = parts[..dimensionsIndex].Aggregate(extracted, Path.Combine);
            }
            else if (Path.GetFileName(regionParent) is var dimensionFolder &&
                (dimensionFolder.Equals("DIM-1", StringComparison.OrdinalIgnoreCase) ||
                 dimensionFolder.Equals("DIM1", StringComparison.OrdinalIgnoreCase)))
            {
                candidate = Path.GetDirectoryName(regionParent)!;
            }
            else
            {
                candidate = regionParent;
            }
            roots.Add(Path.GetFullPath(candidate));
            if (roots.Count > limits.MaxWorlds)
                throw new InputValidationException($"Archive contains more than {limits.MaxWorlds} candidate worlds.");
        }

        return roots
            .OrderBy(root => Path.GetRelativePath(extracted, root).Split(Path.DirectorySeparatorChar).Length)
            .ThenBy(root => root, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

            /// <summary>Reads level metadata and verifies all recognized dimensions in a world root.</summary>
            /// <param name="rootPath">Candidate world root containing <c>level.dat</c>.</param>
            /// <param name="limits">NBT and occupied-chunk safety limits.</param>
            /// <param name="cancellationToken">Token that cancels metadata and chunk inspection.</param>
            /// <returns>Inspected world metadata with exact bounds for each recognized dimension.</returns>
            /// <exception cref="InputValidationException">The root lacks valid level metadata or recognized chunk storage.</exception>
            /// <exception cref="InputSecurityException">A dimension escapes the world root or chunk inspection fails a security check.</exception>
            /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public static async Task<WorldInfo> InspectAsync(
        string rootPath,
        IngestLimits limits,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(rootPath);
        var levelDat = Path.Combine(root, "level.dat");
        LevelDatSummary summary;
        if (File.Exists(levelDat))
        {
            try
            {
                summary = await NbtSummaryReader.ReadLevelDatAsync(levelDat, limits.MaxLevelDatBytes, cancellationToken);
            }
            catch (InputValidationException)
            {
                // level.dat is corrupt: fall back to Minecraft's level.dat_old backup, then to empty metadata.
                // Metadata is cosmetic (name, version, last-played); region data still renders and the operator
                // supplies the name and date at upload time.
                summary = await TryReadLevelDatBackupAsync(root, limits, cancellationToken);
            }
        }
        else
        {
            summary = await TryReadLevelDatBackupAsync(root, limits, cancellationToken);
        }
        var dimensions = new List<DimensionInfo>();
        foreach (var (key, rendererId, relativePaths) in Dimensions)
        {
            var candidates = new List<DimensionInfo>();
            foreach (var relativePath in relativePaths)
            {
                var dimension = await InspectDimensionAsync(root, key, rendererId, relativePath, limits, cancellationToken);
                if (dimension is not null)
                    candidates.Add(dimension);
            }
            if (candidates.Count > 1)
                throw new InputValidationException(
                    $"World contains more than one recognized storage root for {key}: " +
                    string.Join(", ", candidates.Select(value => value.RelativePath)) + ". Select or normalize one source before ingestion.");
            if (candidates.Count == 1)
                dimensions.Add(candidates[0]);
        }

        if (dimensions.Count == 0)
            throw new InputValidationException("World has no recognized Java chunk storage (.mca, .mcr, or legacy chunks).");

        ArchiveWdlEvidence evidence;
        try
        {
            evidence = ArchiveWdlEvidenceReader.ReadWorldRoot(root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            evidence = new ArchiveWdlEvidence();
            evidence.Warnings.Add("Downloader metadata could not be read; terrain inspection continued.");
        }
        evidence.PlayerDimension = summary.PlayerDimension ?? evidence.PlayerDimension;
        evidence.PlayerX = summary.PlayerX ?? evidence.PlayerX;
        evidence.PlayerY = summary.PlayerY ?? evidence.PlayerY;
        evidence.PlayerZ = summary.PlayerZ ?? evidence.PlayerZ;
        if (!string.IsNullOrWhiteSpace(summary.LevelName))
            evidence.NameCandidates.Add(summary.LevelName);
        evidence.RawDimensionIds.AddRange(FindRawDimensionIds(root));
        if (!string.IsNullOrWhiteSpace(evidence.PlayerDimension))
            evidence.RawDimensionIds.Add(evidence.PlayerDimension);
        evidence.RawDimensionIds = ArchiveWdlEvidence.DistinctBounded(evidence.RawDimensionIds, 128);
        evidence.NameCandidates = ArchiveWdlEvidence.DistinctBounded(evidence.NameCandidates, 24);

        var storageTypes = dimensions.Select(dimension => dimension.StorageEra).Distinct().ToArray();
        return new WorldInfo(
            root,
            summary.LevelName,
            summary.DataVersion,
            summary.VersionName,
            storageTypes.Length == 1 ? storageTypes[0] : "mixed",
            dimensions,
            summary.LastPlayedUnixMilliseconds,
            HasArchiveEvidence(evidence) ? evidence : null);
    }

    private static async Task<LevelDatSummary> TryReadLevelDatBackupAsync(
        string root,
        IngestLimits limits,
        CancellationToken cancellationToken)
    {
        var backup = Path.Combine(root, "level.dat_old");
        if (File.Exists(backup))
        {
            try
            {
                return await NbtSummaryReader.ReadLevelDatAsync(backup, limits.MaxLevelDatBytes, cancellationToken);
            }
            catch (InputValidationException)
            {
                // The backup is also unreadable; fall through to empty metadata.
            }
        }
        return new LevelDatSummary(null, null, null, null);
    }

    private static async Task<DimensionInfo?> InspectDimensionAsync(
        string root,
        string key,
        int rendererId,
        string relativePath,
        IngestLimits limits,
        CancellationToken cancellationToken)
    {
        var dimensionRoot = Path.GetFullPath(Path.Combine(root, relativePath));
        EnsureContained(root, dimensionRoot);
        var regionRoot = Path.Combine(dimensionRoot, "region");
        var anvil = Directory.Exists(regionRoot)
            ? Directory.EnumerateFiles(regionRoot, "r.*.*.mca", SearchOption.TopDirectoryOnly).Count()
            : 0;
        var mcRegion = Directory.Exists(regionRoot)
            ? Directory.EnumerateFiles(regionRoot, "r.*.*.mcr", SearchOption.TopDirectoryOnly).Count()
            : 0;
        var legacy = anvil + mcRegion == 0 ? CountLegacyChunks(dimensionRoot) : 0;
        var total = anvil + mcRegion + legacy;
        if (total == 0)
            return null;

        var storage = anvil > 0 ? "anvil" : mcRegion > 0 ? "mcregion" : "legacy-alpha";
        WorldBounds bounds;
        try
        {
            bounds = await ChunkBoundsInspector.InspectAsync(dimensionRoot, limits, cancellationToken);
        }
        catch (InputValidationException)
        {
            // Region files exist but none held a readable occupied chunk (all empty or corrupt). Skip this
            // dimension rather than failing the world; another dimension may still be renderable.
            return null;
        }
        return new DimensionInfo(key, rendererId, relativePath.Replace('\\', '/'), total, storage, bounds);
    }

    private static int CountLegacyChunks(string dimensionRoot)
    {
        if (!Directory.Exists(dimensionRoot))
            return 0;
        var count = 0;
        foreach (var first in Directory.EnumerateDirectories(dimensionRoot))
        {
            if (Path.GetFileName(first).Length > 2)
                continue;
            foreach (var second in Directory.EnumerateDirectories(first))
            {
                if (Path.GetFileName(second).Length > 2)
                    continue;
                count += Directory.EnumerateFiles(second, "c.*.dat", SearchOption.TopDirectoryOnly).Count();
            }
        }
        return count;
    }

    private static void EnsureContained(string root, string candidate)
    {
        var rootPrefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!candidate.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            !candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InputSecurityException("Dimension path escapes the world root.");
        }
    }

    private static IEnumerable<string> FindRawDimensionIds(string root)
    {
        var dimensionsRoot = Path.Combine(root, "dimensions");
        if (!Directory.Exists(dimensionsRoot)) yield break;
        var found = 0;
        foreach (var regionRoot in Directory.EnumerateDirectories(dimensionsRoot, "region", SearchOption.AllDirectories))
        {
            if (++found > 128) yield break;
            var relative = Path.GetRelativePath(dimensionsRoot, Path.GetDirectoryName(regionRoot)!)
                .Replace('\\', '/');
            var separator = relative.IndexOf('/');
            if (separator <= 0 || separator == relative.Length - 1) continue;
            yield return relative[..separator] + ":" + relative[(separator + 1)..];
        }
    }

    private static bool HasArchiveEvidence(ArchiveWdlEvidence evidence) =>
        evidence.DownloaderKind != "unknown" || evidence.NameCandidates.Count > 0 ||
        evidence.RawDimensionIds.Count > 0 || evidence.Warnings.Count > 0 ||
        evidence.PlayerX is not null || evidence.PlayerDimension is not null;
}
