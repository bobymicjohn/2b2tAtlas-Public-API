namespace Atlas.Ingestor.Models;

/// <summary>Describes the immutable result of validating a ZIP archive before extraction.</summary>
/// <param name="ArchivePath">Absolute path of the inspected archive.</param>
/// <param name="Sha256">Lowercase SHA-256 digest of the archive bytes.</param>
/// <param name="ArchiveBytes">Archive file length in bytes.</param>
/// <param name="ExpandedBytes">Sum of the declared uncompressed file lengths.</param>
/// <param name="EntryCount">Total central-directory entry count.</param>
/// <param name="FileCount">Number of regular-file entries.</param>
/// <param name="DirectoryCount">Number of directory entries.</param>
/// <param name="LevelDatCandidates">Normalized paths of candidate Minecraft <c>level.dat</c> files.</param>
public sealed record ArchiveReport(
    string ArchivePath,
    string Sha256,
    long ArchiveBytes,
    long ExpandedBytes,
    int EntryCount,
    int FileCount,
    int DirectoryCount,
    IReadOnlyList<string> LevelDatCandidates);

/// <summary>Describes recognized storage and verified occupied bounds for one Minecraft dimension.</summary>
/// <param name="Key">Canonical dimension key: <c>overworld</c>, <c>nether</c>, or <c>end</c>.</param>
/// <param name="RendererId">Dimension identifier expected by the renderer and Atlas API.</param>
/// <param name="RelativePath">Dimension path relative to its world root.</param>
/// <param name="RegionFileCount">Number of region files, or legacy chunk files, used to identify the dimension.</param>
/// <param name="StorageEra">Detected Java chunk-storage format.</param>
/// <param name="Bounds">Authoritative bounds derived from verified occupied chunk slots, when inspected.</param>
public sealed record DimensionInfo(
    string Key,
    int RendererId,
    string RelativePath,
    int RegionFileCount,
    string StorageEra,
    WorldBounds? Bounds = null);

/// <summary>Identifies a 256-by-256-block native renderer tile.</summary>
/// <param name="X">Signed native tile X coordinate, computed with floor division from chunk X.</param>
/// <param name="Z">Signed native tile Z coordinate, computed with floor division from chunk Z.</param>
public sealed record NativeTileCoordinate(int X, int Z);

/// <summary>Captures exact occupied chunk and native-tile bounds for a dimension.</summary>
/// <param name="MinChunkX">Minimum occupied chunk X coordinate, inclusive.</param>
/// <param name="MinChunkZ">Minimum occupied chunk Z coordinate, inclusive.</param>
/// <param name="MaxChunkX">Maximum occupied chunk X coordinate, inclusive.</param>
/// <param name="MaxChunkZ">Maximum occupied chunk Z coordinate, inclusive.</param>
/// <param name="ChunkCount">Number of distinct occupied chunks.</param>
/// <param name="NativeTileCount">Number of distinct 16-by-16-chunk native tiles.</param>
/// <param name="NativeTileInventorySha256">SHA-256 of canonical <c>x,z\n</c> native-tile coordinates ordered by Z then X.</param>
/// <param name="NativeTiles">Canonical occupied native-tile coordinates.</param>
/// <param name="SkippedRegions">Region files skipped as corrupt or truncated during inspection.</param>
/// <param name="SkippedChunks">Individual chunks skipped as corrupt or unreadable during inspection.</param>
/// <remarks>
/// Bounds are derived only after storage-slot coordinates agree with the chunk NBT <c>xPos</c>/<c>zPos</c> values.
/// Maximum block coordinates are exclusive so they can be passed directly to Atlas render metadata.
/// </remarks>
public sealed record WorldBounds(
    int MinChunkX,
    int MinChunkZ,
    int MaxChunkX,
    int MaxChunkZ,
    int ChunkCount,
    int NativeTileCount,
    string NativeTileInventorySha256,
    IReadOnlyList<NativeTileCoordinate> NativeTiles,
    int SkippedRegions = 0,
    int SkippedChunks = 0)
{
    /// <value>Minimum occupied block X coordinate, inclusive.</value>
    public int MinBlockX => checked(MinChunkX * 16);
    /// <value>Minimum occupied block Z coordinate, inclusive.</value>
    public int MinBlockZ => checked(MinChunkZ * 16);
    /// <value>Maximum occupied block X boundary, exclusive.</value>
    public int MaxBlockXExclusive => checked((MaxChunkX + 1) * 16);
    /// <value>Maximum occupied block Z boundary, exclusive.</value>
    public int MaxBlockZExclusive => checked((MaxChunkZ + 1) * 16);
    /// <value>Integer midpoint of the exclusive X bounds.</value>
    public int CenterBlockX => checked((int)(((long)MinBlockX + MaxBlockXExclusive) / 2));
    /// <value>Integer midpoint of the exclusive Z bounds.</value>
    public int CenterBlockZ => checked((int)(((long)MinBlockZ + MaxBlockZExclusive) / 2));
}

/// <summary>Describes one inspected Minecraft Java world and its recognized dimensions.</summary>
/// <param name="RootPath">Absolute extracted world-root path.</param>
/// <param name="LevelName">Optional display name read from <c>level.dat</c>.</param>
/// <param name="DataVersion">Optional Java data version read from <c>level.dat</c>.</param>
/// <param name="VersionName">Optional Java version name read from <c>level.dat</c>.</param>
/// <param name="StorageEra">Common storage era, or <c>mixed</c> when dimensions differ.</param>
/// <param name="Dimensions">Recognized dimensions with verified storage metadata.</param>
/// <param name="LastPlayedUnixMilliseconds">Optional <c>LastPlayed</c> timestamp from <c>level.dat</c>, in Unix milliseconds.</param>
/// <param name="ArchiveEvidence">Optional bounded downloader and dimension evidence.</param>
public sealed record WorldInfo(
    string RootPath,
    string? LevelName,
    int? DataVersion,
    string? VersionName,
    string StorageEra,
    IReadOnlyList<DimensionInfo> Dimensions,
    long? LastPlayedUnixMilliseconds = null,
    ArchiveWdlEvidence? ArchiveEvidence = null);

/// <summary>Binds public render metadata to one inspected world and an explicit set of dimensions.</summary>
/// <param name="Slug">Public render slug.</param>
/// <param name="Name">Public display name.</param>
/// <param name="WorldDownloadDate">Attributed world-download date.</param>
/// <param name="Source">Source attribution.</param>
/// <param name="World">Selected inspected world.</param>
/// <param name="Dimensions">Dimensions selected for rendering.</param>
/// <param name="DayNight">Whether the eventual render advertises day/night support.</param>
/// <param name="Scale">Atlas scale label.</param>
/// <param name="Publish">Whether publication was requested by the manifest.</param>
/// <remarks>The plan is hashed and included in renderer, adaptation, and publication receipts.</remarks>
public sealed record RenderPlan(
    string Slug,
    string Name,
    DateOnly WorldDownloadDate,
    string Source,
    WorldInfo World,
    IReadOnlyList<DimensionInfo> Dimensions,
    bool DayNight,
    string Scale,
    bool Publish);

/// <summary>Names the private artifacts belonging to a content-addressed ingestion job.</summary>
/// <param name="Root">Job root directory.</param>
/// <param name="Snapshot">Immutable input ZIP snapshot path.</param>
/// <param name="Extracted">Safely extracted world root.</param>
/// <param name="Render">Renderer staging root.</param>
/// <param name="State">Persisted job-state JSON path.</param>
/// <param name="ArchiveReport">Persisted archive-inspection report path.</param>
/// <param name="RenderPlan">Persisted render-plan JSON path.</param>
public sealed record JobPaths(
    string Root,
    string Snapshot,
    string Extracted,
    string Render,
    string State,
    string ArchiveReport,
    string RenderPlan)
{
    /// <summary>Gets the publication-receipt path for a dimension.</summary>
    /// <param name="dimension">Canonical dimension key.</param>
    /// <returns>The receipt path under the job root.</returns>
    public string PublicationReceipt(string dimension) =>
        Path.Combine(Root, $"publication-{dimension}.json");

    /// <summary>Gets the renderer-provenance path for a dimension.</summary>
    /// <param name="dimension">Canonical dimension key.</param>
    /// <returns>The provenance path under the render root.</returns>
    public string RenderProvenance(string dimension) =>
        Path.Combine(Render, $".atlas-render-provenance-{dimension}.json");

    /// <summary>Gets the adaptation-receipt path for a dimension.</summary>
    /// <param name="dimension">Canonical dimension key.</param>
    /// <returns>The receipt path under the job root.</returns>
    public string AdaptationReceipt(string dimension) =>
        Path.Combine(Root, $"adaptation-{dimension}.json");
}
