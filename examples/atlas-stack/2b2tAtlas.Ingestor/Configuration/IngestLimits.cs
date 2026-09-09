namespace Atlas.Ingestor.Configuration;

/// <summary>Defines resource ceilings applied while inspecting, extracting, rendering, and publishing an ingestion job.</summary>
/// <remarks>
/// These limits reduce denial-of-service and storage-exhaustion risk but do not establish that an archive is trusted
/// or that its world data originated from 2b2t. Call <see cref="Validate"/> before using a custom instance.
/// </remarks>
public sealed record IngestLimits
{
    /// <summary>Number of bytes in a gibibyte.</summary>
    public const long GiB = 1024L * 1024 * 1024;
    /// <summary>Number of bytes in a mebibyte.</summary>
    public const long MiB = 1024L * 1024;

    /// <value>Maximum accepted ZIP file length, in bytes.</value>
    public long MaxArchiveBytes { get; init; } = 32 * GiB;
    /// <value>Maximum declared and extracted byte total for an archive.</value>
    public long MaxExpandedBytes { get; init; } = 256 * GiB;
    /// <value>Maximum total bytes in a verified rendered tile set.</value>
    public long MaxOutputBytes { get; init; } = 512 * GiB;
    /// <value>Maximum number of files and directories traversed in renderer or adapted output.</value>
    public int MaxOutputEntries { get; init; } = 5_000_000;
    /// <value>Free-space reserve that must remain in addition to the archive's declared expanded size.</value>
    public long MinimumFreeBytes { get; init; } = 20 * GiB;
    /// <value>Maximum number of ZIP central-directory entries.</value>
    public int MaxEntries { get; init; } = 500_000;
    /// <value>Maximum declared uncompressed length of one ZIP entry.</value>
    public long MaxSingleEntryBytes { get; init; } = 8 * GiB;
    /// <value>Maximum accepted uncompressed-to-compressed ratio for ZIP entries at least one mebibyte long.</value>
    public double MaxCompressionRatio { get; init; } = 250;
    /// <value>Maximum normalized archive entry path length.</value>
    public int MaxPathLength { get; init; } = 240;
    /// <value>Maximum number of components in a normalized archive entry path.</value>
    public int MaxPathDepth { get; init; } = 32;
    /// <value>Maximum number of candidate Minecraft world roots discovered in one archive.</value>
    public int MaxWorlds { get; init; } = 16;
    /// <value>Maximum expanded bytes read from a <c>level.dat</c> payload.</value>
    public long MaxLevelDatBytes { get; init; } = 16 * MiB;
    /// <value>Maximum expanded bytes read from one chunk NBT payload.</value>
    public long MaxChunkNbtBytes { get; init; } = 64 * MiB;
    /// <value>Maximum number of occupied chunks accepted in one dimension.</value>
    public int MaxChunkCount { get; init; } = 4_000_000;
    /// <value>Maximum wall-clock duration allowed for one renderer process.</value>
    public TimeSpan RenderTimeout { get; init; } = TimeSpan.FromHours(24);
    /// <value>Maximum length, in bytes, of each renderer standard-output or standard-error log.</value>
    public long MaxLogBytes { get; init; } = 64 * MiB;

    /// <summary>Verifies that all ceilings are positive and mutually consistent.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A numeric ceiling or <see cref="RenderTimeout"/> is not positive.</exception>
    /// <exception cref="ArgumentException"><see cref="MaxExpandedBytes"/> is smaller than <see cref="MaxArchiveBytes"/>.</exception>
    public void Validate()
    {
        var numeric = new Dictionary<string, double>
        {
            [nameof(MaxArchiveBytes)] = MaxArchiveBytes,
            [nameof(MaxExpandedBytes)] = MaxExpandedBytes,
            [nameof(MaxOutputBytes)] = MaxOutputBytes,
            [nameof(MaxOutputEntries)] = MaxOutputEntries,
            [nameof(MinimumFreeBytes)] = MinimumFreeBytes,
            [nameof(MaxEntries)] = MaxEntries,
            [nameof(MaxSingleEntryBytes)] = MaxSingleEntryBytes,
            [nameof(MaxCompressionRatio)] = MaxCompressionRatio,
            [nameof(MaxPathLength)] = MaxPathLength,
            [nameof(MaxPathDepth)] = MaxPathDepth,
            [nameof(MaxWorlds)] = MaxWorlds,
            [nameof(MaxLevelDatBytes)] = MaxLevelDatBytes,
            [nameof(MaxChunkNbtBytes)] = MaxChunkNbtBytes,
            [nameof(MaxChunkCount)] = MaxChunkCount,
            [nameof(MaxLogBytes)] = MaxLogBytes,
        };

        foreach (var pair in numeric)
        {
            if (pair.Value <= 0)
                throw new ArgumentOutOfRangeException(pair.Key, "Ingest limits must be greater than zero.");
        }

        if (MaxExpandedBytes < MaxArchiveBytes)
            throw new ArgumentException("Expanded size limit cannot be smaller than archive size limit.");
        if (RenderTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(RenderTimeout));
    }
}
