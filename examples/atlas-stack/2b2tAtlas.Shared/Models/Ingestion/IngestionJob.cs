using System.Text.RegularExpressions;

namespace Atlas;

/// <summary>Describes a validated world-download archive to queue for local ingestion.</summary>
public class IngestionJobRequest
{
    /// <summary>Gets or sets the portable ASCII <c>.zip</c> basename expected in the worker intake directory.</summary>
    public string IntakeFileName { get; set; } = string.Empty;

    /// <summary>Gets or sets the unique lowercase path segment used for the job and published tile directory.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Gets or sets the location and render name shown by the Atlas.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the non-future world-download date in <c>yyyy-MM-dd</c> format.</summary>
    public string WorldDownloadDate { get; set; } = string.Empty;

    /// <summary>Gets or sets the human-readable source or provenance attribution.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Gets or sets the display scale tag, such as <c>5k</c>, <c>256k</c>, or <c>1m</c>.</summary>
    public string Scale { get; set; } = string.Empty;

    /// <summary>Gets or sets whether separate day and night tiles are produced; worker jobs always enable this.</summary>
    public bool DayNight { get; set; } = true;

    /// <summary>Gets or sets the location to receive the render, or <see langword="null"/> to create a location at the render center.</summary>
    public int? ExistingLocationId { get; set; }

    /// <summary>Gets or sets whether a valid <c>level.dat</c> LastPlayed value may replace <see cref="WorldDownloadDate"/>; <see langword="null"/> defaults to enabled.</summary>
    public bool? UseArchiveLastPlayed { get; set; }

    /// <summary>Gets or sets the target render dimension: <c>overworld</c>, <c>nether</c>, or <c>end</c>.</summary>
    public string Dimension { get; set; } = "overworld";

    /// <summary>Gets or sets an optional archive-relative world root when a ZIP contains multiple worlds.</summary>
    public string? WorldRoot { get; set; }

    /// <summary>
    /// Gets or sets an optional operator-confirmed Archive command name. Normally the server derives this
    /// from a recognized downloader report or an Archive-attributed filename.
    /// </summary>
    public string? ArchiveWarpName { get; set; }

    /// <summary>Gets or sets the live Archive landing X coordinate captured immediately after the warp.</summary>
    public double? ArchiveWarpX { get; set; }

    /// <summary>Gets or sets the live Archive landing Y coordinate captured immediately after the warp.</summary>
    public double? ArchiveWarpY { get; set; }

    /// <summary>Gets or sets the live Archive landing Z coordinate captured immediately after the warp.</summary>
    public double? ArchiveWarpZ { get; set; }
}

/// <summary>Starts a resumable, proxy-safe WDL upload session.</summary>
public sealed class ChunkedUploadStartRequest
{
    /// <summary>Gets or sets JSON-encoded <see cref="IngestionJobRequest"/> metadata.</summary>
    public string Metadata { get; set; } = string.Empty;

    /// <summary>Gets or sets the original ZIP filename.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Gets or sets the total ZIP length in bytes.</summary>
    public long TotalBytes { get; set; }
}

/// <summary>Identifies a resumable WDL upload and its bounded chunk size.</summary>
public sealed class ChunkedUploadSession
{
    /// <summary>Gets or sets the opaque upload identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the maximum bytes per chunk.</summary>
    public int ChunkSizeBytes { get; set; }
}

/// <summary>Queues a completed ZIP already present in the server's configured local intake directory.</summary>
public sealed class LocalIntakeQueueRequest
{
    /// <summary>Gets or sets the normal bounded ingestion metadata.</summary>
    public IngestionJobRequest Metadata { get; set; } = new();

    /// <summary>Gets or sets the acquisition-side filename retained for Archive warp and provenance inference.</summary>
    public string OriginalFileName { get; set; } = string.Empty;
}

/// <summary>Represents a queued ingestion job and its server-managed execution state.</summary>
public sealed class IngestionJobDto : IngestionJobRequest
{
    /// <summary>Gets or sets the job's public, opaque identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the original uploader-visible ZIP filename.</summary>
    public string? OriginalFileName { get; set; }

    /// <summary>Gets or sets the queue state, such as <c>queued</c>, <c>needs-match</c>, <c>claimed</c>, <c>running</c>, <c>completed</c>, <c>failed</c>, or <c>cancelled</c>.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets the worker pipeline stage currently associated with the job.</summary>
    public string? Stage { get; set; }

    /// <summary>Gets or sets the latest operator-safe progress or failure message.</summary>
    public string? Message { get; set; }

    /// <summary>Gets or sets overall progress from 0 through 100.</summary>
    public int ProgressPercent { get; set; }

    /// <summary>Gets or sets the estimated remaining seconds, or <see langword="null"/> when unavailable or terminal.</summary>
    public int? EtaSeconds { get; set; }

    /// <summary>Gets or sets the lowercase SHA-256 digest of the immutable intake archive after inspection.</summary>
    public string? ArchiveSha256 { get; set; }

    /// <summary>Gets or sets the one-lease worker secret returned only by a successful claim response.</summary>
    public string? ClaimToken { get; set; }

    /// <summary>Gets or sets the number of times the job has been claimed, including retries after expired leases.</summary>
    public int AttemptCount { get; set; }

    /// <summary>Gets or sets whether this completed job is being rebuilt from its archived source.</summary>
    public bool RerenderRequested { get; set; }

    /// <summary>Gets or sets an optional inclusive top-Y render cutoff selected from reviewed historical evidence.</summary>
    public int? RenderTopY { get; set; }

    /// <summary>Gets or sets the UTC time at which the job was queued.</summary>
    public DateTime RequestedUtc { get; set; }

    /// <summary>Gets or sets the UTC time of the most recent successful claim.</summary>
    public DateTime? ClaimedUtc { get; set; }

    /// <summary>Gets or sets the UTC expiry of the current worker lease.</summary>
    public DateTime? LeaseExpiresUtc { get; set; }

    /// <summary>Gets or sets the UTC time of the most recent status update.</summary>
    public DateTime? UpdatedUtc { get; set; }

    /// <summary>Gets or sets the UTC time at which the job entered a terminal state.</summary>
    public DateTime? CompletedUtc { get; set; }

    /// <summary>Gets or sets the worker's bounded inspection summary for the Minecraft world.</summary>
    public IngestionWorldInspection? Inspection { get; set; }

    /// <summary>Gets or sets untrusted downloader/provenance evidence captured before or during preparation.</summary>
    public ArchiveWdlEvidence? ArchiveEvidence { get; set; }

    /// <summary>Gets or sets ranked existing-location suggestions when the job is awaiting a manual match.</summary>
    public IReadOnlyList<LocationMatchSuggestion>? MatchSuggestions { get; set; }

    /// <summary>Gets or sets the evidence source used to infer <see cref="IngestionJobRequest.ArchiveWarpName"/>.</summary>
    public string? ArchiveWarpSource { get; set; }

    /// <summary>Gets or sets the warp row reused or created for the immutable WDL.</summary>
    public int? WarpId { get; set; }

    /// <summary>Gets or sets the persisted automatic/manual location decision.</summary>
    public string? MatchDecision { get; set; }

    /// <summary>Gets or sets the confidence of the persisted match decision.</summary>
    public double? MatchConfidence { get; set; }

    /// <summary>Gets or sets the concise evidence supporting the persisted match decision.</summary>
    public string? MatchReason { get; set; }
}

/// <summary>Resolves a job awaiting a manual location match.</summary>
public sealed class ResolveMatchRequest
{
    /// <summary>Gets or sets the existing location to attach, or <see langword="null"/> to create a new location at the render centroid.</summary>
    public int? LocationId { get; set; }

    /// <summary>Gets or sets the reviewed Archive warp, or an empty value when this is not an Archive WDL.</summary>
    public string? WarpName { get; set; }
}

/// <summary>Reports authenticated progress or completion data from the worker holding a job lease.</summary>
public sealed class IngestionJobUpdate
{
    /// <summary>Gets or sets the 43-character claim secret proving ownership of the active lease.</summary>
    public string ClaimToken { get; set; } = string.Empty;

    /// <summary>Gets or sets the worker-reported state: <c>running</c>, <c>completed</c>, or <c>failed</c>.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets the current pipeline stage, limited to 40 characters without control characters.</summary>
    public string? Stage { get; set; }

    /// <summary>Gets or sets an operator-safe status message, limited to 1,000 characters without control characters.</summary>
    public string? Message { get; set; }

    /// <summary>Gets or sets overall progress from 0 through 100.</summary>
    public int ProgressPercent { get; set; }

    /// <summary>Gets or sets estimated remaining seconds from zero through seven days.</summary>
    public int? EtaSeconds { get; set; }

    /// <summary>Gets or sets the lowercase SHA-256 archive digest; completion reports require it.</summary>
    public string? ArchiveSha256 { get; set; }

    /// <summary>Gets or sets the published render metadata; completion reports require it.</summary>
    public LocationRenderCompletion? LocationRender { get; set; }

    /// <summary>Gets or sets a replacement bounded world-inspection summary to persist with the job.</summary>
    public IngestionWorldInspection? Inspection { get; set; }
}

/// <summary>Summarizes trusted structural metadata extracted from a prepared Minecraft Java world.</summary>
public sealed class IngestionWorldInspection
{
    /// <summary>Gets or sets the optional world name read from <c>level.dat</c>.</summary>
    public string? LevelName { get; set; }

    /// <summary>Gets or sets the optional numeric Minecraft data version read from <c>level.dat</c>.</summary>
    public int? DataVersion { get; set; }

    /// <summary>Gets or sets the optional human-readable Minecraft version name.</summary>
    public string? VersionName { get; set; }

    /// <summary>Gets or sets a plausible UTC LastPlayed time read from <c>level.dat</c>.</summary>
    public DateTime? LastPlayedUtc { get; set; }

    /// <summary>Gets or sets the aggregate storage era: <c>anvil</c>, <c>mcregion</c>, <c>legacy-alpha</c>, or <c>mixed</c>.</summary>
    public string StorageEra { get; set; } = string.Empty;

    /// <summary>Gets or sets one to three unique Overworld, Nether, or End inspection summaries.</summary>
    public List<IngestionDimensionInspection> Dimensions { get; set; } = [];

    /// <summary>Gets or sets the provenance assessment: <c>unverified</c>, <c>source-attributed</c>, or <c>overlap-verified</c>.</summary>
    public string ProvenanceStatus { get; set; } = "unverified";

    /// <summary>Gets or sets the human-readable evidence statement supporting <see cref="ProvenanceStatus"/>.</summary>
    public string ProvenanceMessage { get; set; } = string.Empty;

    /// <summary>Gets or sets bounded Archive downloader, raw-dimension, and player-position evidence.</summary>
    public ArchiveWdlEvidence? ArchiveEvidence { get; set; }
}

/// <summary>Summarizes storage files, chunks, native tiles, and block bounds for one Minecraft dimension.</summary>
public sealed class IngestionDimensionInspection
{
    /// <summary>Gets or sets <c>overworld</c>, <c>nether</c>, or <c>end</c>.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets <c>anvil</c>, <c>mcregion</c>, or <c>legacy-alpha</c> for this dimension.</summary>
    public string StorageEra { get; set; } = string.Empty;

    /// <summary>Gets or sets the number of validated region or chunk-storage files.</summary>
    public int StorageFileCount { get; set; }

    /// <summary>Gets or sets the number of present chunks found in validated storage.</summary>
    public int ChunkCount { get; set; }

    /// <summary>Gets or sets the native tiles implied by the authoritative chunk bounds.</summary>
    public int NativeTileCount { get; set; }

    /// <summary>Gets or sets the inclusive minimum block X coordinate.</summary>
    public int MinX { get; set; }

    /// <summary>Gets or sets the inclusive minimum block Z coordinate.</summary>
    public int MinZ { get; set; }

    /// <summary>Gets or sets the exclusive maximum block X coordinate.</summary>
    public int MaxXExclusive { get; set; }

    /// <summary>Gets or sets the exclusive maximum block Z coordinate.</summary>
    public int MaxZExclusive { get; set; }

    /// <summary>Gets or sets the number of region files skipped as corrupt or truncated during inspection.</summary>
    public int SkippedRegionCount { get; set; }

    /// <summary>Gets or sets the number of individual chunks skipped as corrupt or unreadable during inspection.</summary>
    public int SkippedChunkCount { get; set; }
}

/// <summary>Describes a verified immutable tile pyramid that the server can register as a location render.</summary>
public sealed class LocationRenderCompletion
{
    /// <summary>Gets or sets the approved HTTPS XYZ tile template ending in <c>{z}/{y}/{x}.png</c>.</summary>
    public string TilesPath { get; set; } = string.Empty;

    /// <summary>Gets or sets whether <see cref="TilesPath"/> contains a <c>{dn}</c> day/night token.</summary>
    public bool HasDayNight { get; set; }

    /// <summary>Gets or sets the map dimension number: 0 Overworld, 1 Nether, or 2 End.</summary>
    public int Dimension { get; set; }

    /// <summary>Gets or sets the inclusive minimum rendered block X coordinate.</summary>
    public int MinX { get; set; }

    /// <summary>Gets or sets the inclusive minimum rendered block Z coordinate.</summary>
    public int MinZ { get; set; }

    /// <summary>Gets or sets the exclusive maximum rendered block X coordinate.</summary>
    public int MaxXExclusive { get; set; }

    /// <summary>Gets or sets the exclusive maximum rendered block Z coordinate.</summary>
    public int MaxZExclusive { get; set; }

    /// <summary>Gets or sets the deepest zoom level backed by native tile files, from 0 through 30.</summary>
    public int MaxNativeZoom { get; set; }

    /// <summary>Gets or sets the signed tile-coordinate contract; registration currently requires <c>atlas-sparse-v1</c>.</summary>
    public string CoordinateScheme { get; set; } = string.Empty;
}

/// <summary>Validates untrusted ingestion queue requests and lease-holder status reports.</summary>
public static partial class IngestionJobValidator
{
    private static readonly Regex SlugPattern = SlugRegex();
    private static readonly Regex ScalePattern = ScaleRegex();
    private static readonly Regex Sha256Pattern = Sha256Regex();
    private static readonly Regex IntakeFilePattern = IntakeFileRegex();
    private static readonly Regex ClaimTokenPattern = ClaimTokenRegex();
    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul", "clock$",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    /// <summary>Validates portable filenames, public metadata, supported rendering options, and dates for a queued job.</summary>
    /// <param name="request">The untrusted ingestion request.</param>
    /// <returns>A list of validation errors; an empty list indicates a valid request.</returns>
    public static IReadOnlyList<string> ValidateRequest(IngestionJobRequest request)
    {
        var errors = new List<string>();
        var rawFileName = request.IntakeFileName ?? string.Empty;
        var fileName = rawFileName.Trim();
        if (fileName.Length is < 5 or > 180 ||
            fileName != rawFileName ||
            !IntakeFilePattern.IsMatch(fileName) ||
            ReservedWindowsNames.Contains(Path.GetFileNameWithoutExtension(fileName).Split('.')[0]))
        {
            errors.Add("Intake file must be a portable ASCII .zip filename between 5 and 180 characters.");
        }

        var slug = request.Slug?.Trim() ?? string.Empty;
        if (!SlugPattern.IsMatch(slug))
            errors.Add("Slug must be 1-54 lowercase letters, numbers, or hyphens.");

        var rawName = request.Name ?? string.Empty;
        var name = rawName.Trim();
        if (name.Length is < 1 or > 90 || name != rawName || HasControlCharacters(rawName))
            errors.Add("Name must be between 1 and 90 characters.");

        var rawSource = request.Source ?? string.Empty;
        var source = rawSource.Trim();
        if (source.Length is < 1 or > 200 || source != rawSource || HasControlCharacters(rawSource))
            errors.Add("Source must be between 1 and 200 characters.");

        var scale = request.Scale?.Trim() ?? string.Empty;
        if (scale.Length > 12 || !ScalePattern.IsMatch(scale))
            errors.Add("Scale must look like 5k, 256k, or 1m.");

        var dimension = request.Dimension?.Trim().ToLowerInvariant() ?? string.Empty;
        if (dimension is not ("overworld" or "nether" or "end" or "auto"))
            errors.Add("Render dimension must be Overworld, Nether, End, or Auto-detect.");
        if (!string.IsNullOrWhiteSpace(request.WorldRoot))
        {
            var worldRoot = request.WorldRoot.Replace('\\', '/').Trim('/');
            if (worldRoot.Length is < 1 or > 240 || Path.IsPathRooted(worldRoot) ||
                worldRoot.Split('/').Any(part => part is "" or "." or "..") || HasControlCharacters(worldRoot))
                errors.Add("World root must be a safe archive-relative path up to 240 characters.");
        }
        if (!string.IsNullOrWhiteSpace(request.ArchiveWarpName) &&
            (request.ArchiveWarpName.Length > 240 || request.ArchiveWarpName.Any(char.IsControl)))
            errors.Add("Archive warp must be at most 240 characters without control characters.");
        var warpCoordinates = new[] { request.ArchiveWarpX, request.ArchiveWarpY, request.ArchiveWarpZ };
        if (warpCoordinates.Any(value => value.HasValue) && warpCoordinates.Any(value => !value.HasValue))
            errors.Add("Archive warp coordinates must provide X, Y, and Z together.");
        foreach (var coordinate in warpCoordinates)
            if (coordinate is double value && (!double.IsFinite(value) || Math.Abs(value) > 100_000_000))
                errors.Add("Archive warp coordinates are outside the safety limit.");
        if (request.ExistingLocationId is <= 0)
            errors.Add("Existing location ID must be a positive integer.");

        if (!DateOnly.TryParseExact(request.WorldDownloadDate, "yyyy-MM-dd", out var date) ||
            date > DateOnly.FromDateTime(DateTime.UtcNow))
        {
            errors.Add("World download date must be YYYY-MM-DD and cannot be in the future.");
        }

        return errors;
    }

    /// <summary>Validates a worker status report, including its claim token shape, progress limits, digest, and inspection data.</summary>
    /// <param name="update">The untrusted status report.</param>
    /// <returns>A list of validation errors; an empty list indicates a structurally valid report.</returns>
    public static IReadOnlyList<string> ValidateUpdate(IngestionJobUpdate update)
    {
        var errors = new List<string>();
        if (!ClaimTokenPattern.IsMatch(update.ClaimToken ?? string.Empty))
            errors.Add("A valid claim token is required.");
        if (update.Status is not ("running" or "completed" or "failed"))
            errors.Add("Worker status must be running, completed, or failed.");
        if (update.Stage?.Length > 40 || update.Stage is not null && HasControlCharacters(update.Stage))
            errors.Add("Stage must be at most 40 characters without control characters.");
        if (update.Message?.Length > 1000 || update.Message is not null && HasControlCharacters(update.Message))
            errors.Add("Message must be at most 1000 characters without control characters.");
        if (update.ProgressPercent is < 0 or > 100)
            errors.Add("Progress percent must be between 0 and 100.");
        if (update.EtaSeconds is < 0 or > 7 * 24 * 60 * 60)
            errors.Add("ETA must be between zero and seven days.");
        if (update.ArchiveSha256 is not null && !Sha256Pattern.IsMatch(update.ArchiveSha256))
            errors.Add("Archive SHA-256 must be 64 lowercase hexadecimal characters.");
        if (update.Status == "completed" && update.ArchiveSha256 is null)
            errors.Add("Completed jobs require an archive SHA-256.");
        if (update.Status == "completed" && update.LocationRender is null)
            errors.Add("Completed jobs require location render metadata.");
        if (update.Inspection is not null)
            ValidateInspection(update.Inspection, errors);
        return errors;
    }

    private static void ValidateInspection(IngestionWorldInspection inspection, List<string> errors)
    {
        if (inspection.LevelName?.Length > 200 || inspection.LevelName is not null && HasControlCharacters(inspection.LevelName))
            errors.Add("World level name must be at most 200 characters without control characters.");
        if (inspection.VersionName?.Length > 80 || inspection.VersionName is not null && HasControlCharacters(inspection.VersionName))
            errors.Add("World version name must be at most 80 characters without control characters.");
        if (inspection.StorageEra is not ("anvil" or "mcregion" or "legacy-alpha" or "mixed"))
            errors.Add("World storage era is invalid.");
        if (inspection.ProvenanceStatus is not ("unverified" or "source-attributed" or "overlap-verified"))
            errors.Add("World provenance status is invalid.");
        if (inspection.ProvenanceMessage.Length is < 1 or > 500 || HasControlCharacters(inspection.ProvenanceMessage))
            errors.Add("World provenance message must be between 1 and 500 characters without control characters.");
        if (inspection.ArchiveEvidence is { } evidence)
            ValidateArchiveEvidence(evidence, errors);
        if (inspection.Dimensions.Count is < 1 or > 3 ||
            inspection.Dimensions.Select(value => value.Key).Distinct(StringComparer.Ordinal).Count() != inspection.Dimensions.Count)
            errors.Add("World inspection must contain one to three unique dimensions.");
        foreach (var dimension in inspection.Dimensions)
        {
            if (dimension.Key is not ("overworld" or "nether" or "end") ||
                dimension.StorageEra is not ("anvil" or "mcregion" or "legacy-alpha") ||
                dimension.StorageFileCount < 1 || dimension.ChunkCount < 1 || dimension.NativeTileCount < 1 ||
                dimension.MinX >= dimension.MaxXExclusive || dimension.MinZ >= dimension.MaxZExclusive)
                errors.Add("World dimension inspection is invalid.");
        }
    }

    private static void ValidateArchiveEvidence(ArchiveWdlEvidence evidence, List<string> errors)
    {
        static bool Invalid(string? value, int maximum) =>
            value?.Length > maximum || value is not null && value.Any(char.IsControl);
        if (Invalid(evidence.DownloaderKind, 40) || Invalid(evidence.DownloadName, 240) ||
            Invalid(evidence.SourceAddress, 500) || Invalid(evidence.SourceName, 500) ||
            Invalid(evidence.SourceMotd, 500) || Invalid(evidence.ReportedDimension, 240) ||
            Invalid(evidence.PlayerDimension, 240) || Invalid(evidence.CompletionStatus, 40) ||
            Invalid(evidence.SourceKind, 80) || Invalid(evidence.ServerBrand, 120) ||
            Invalid(evidence.MinecraftVersion, 80) || Invalid(evidence.ModVersion, 80) ||
            Invalid(evidence.LoaderName, 80) || Invalid(evidence.LoaderVersion, 80))
            errors.Add("Archive WDL evidence contains an invalid text field.");
        if (evidence.ReportSchemaVersion is < 0 or > 1_000 || evidence.ReportSessionCount is < 0 or > 256 ||
            new[] { evidence.CapturedChunkCount, evidence.SavedChunkCount, evidence.EntityCount, evidence.ContainerCount }
                .Any(value => value is < 0 or > 100_000_000))
            errors.Add("Archive WDL evidence contains an invalid report count.");
        if (evidence.NameCandidates.Count > 24 || evidence.RawDimensionIds.Count > 128 || evidence.Warnings.Count > 24 ||
            evidence.NameCandidates.Concat(evidence.RawDimensionIds).Concat(evidence.Warnings)
                .Any(value => Invalid(value, 240)))
            errors.Add("Archive WDL evidence exceeds its bounded collection limits.");
        foreach (var coordinate in new[] { evidence.PlayerX, evidence.PlayerY, evidence.PlayerZ })
            if (coordinate is double value && (!double.IsFinite(value) || Math.Abs(value) > 100_000_000))
                errors.Add("Archive WDL player position is outside the safety limit.");
    }

    private static bool HasControlCharacters(string value) => value.Any(char.IsControl);

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,52}[a-z0-9])?$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SlugRegex();

    [GeneratedRegex("^[1-9][0-9]*(?:\\.[0-9]+)?[km]?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex ScaleRegex();

    [GeneratedRegex("^[a-f0-9]{64}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Sha256Regex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._ ()-]{0,175}\\.zip$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex IntakeFileRegex();

    [GeneratedRegex("^[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex ClaimTokenRegex();
}

/// <summary>Validates published location-render metadata before the server links it into Atlas storage.</summary>
public static class IngestionCompletionValidator
{
    private const int RenderCoordinateLimit = 30_000_256;

    /// <summary>Checks dimension support, half-open world bounds, native zoom, coordinate scheme, and approved job-specific tile URL.</summary>
    /// <param name="job">The job whose slug must own the published tile path.</param>
    /// <param name="render">The worker-reported render completion metadata.</param>
    /// <param name="allowedUrlPrefixes">The operator-configured HTTPS tile roots.</param>
    /// <returns>A list of validation errors; an empty list indicates metadata safe to register.</returns>
    public static IReadOnlyList<string> Validate(
        IngestionJobRequest job,
        LocationRenderCompletion render,
        IReadOnlyCollection<string> allowedUrlPrefixes)
    {
        var errors = new List<string>();
        if (render.Dimension is not (0 or 1 or 2))
            errors.Add("The certified base-render schemes support the Overworld, Nether, and End.");
        if (render.CoordinateScheme != "atlas-sparse-v1")
            errors.Add("Location render must use the signed sparse Atlas coordinate scheme.");
        if (render.MinX >= render.MaxXExclusive || render.MinZ >= render.MaxZExclusive)
            errors.Add("Location render bounds are empty or inverted.");
        if (render.MinX < -RenderCoordinateLimit || render.MinZ < -RenderCoordinateLimit ||
            render.MaxXExclusive > RenderCoordinateLimit || render.MaxZExclusive > RenderCoordinateLimit)
            errors.Add("Location render bounds exceed the world border tile margin.");
        if (render.MaxNativeZoom is < 0 or > 30)
            errors.Add("Location render maximum native zoom is invalid.");
        var dimensionPath = render.Dimension switch { 1 => "nether", 2 => "end", _ => "overworld" };
        var pathMatches = render.HasDayNight
            ? Regex.IsMatch(
                render.TilesPath,
                $@"/{Regex.Escape(job.Slug)}/{dimensionPath}/g-[0-9]{{14}}-[a-f0-9]{{8}}" +
                Regex.Escape("/{dn}/{z}/{y}/{x}.png") + "$",
                RegexOptions.CultureInvariant)
            : render.TilesPath.EndsWith(
                $"/{job.Slug}/{dimensionPath}/{{z}}/{{y}}/{{x}}.png", StringComparison.Ordinal);
        if (!pathMatches || !IsAllowedTileUrl(render.TilesPath, allowedUrlPrefixes))
            errors.Add("Location render tile URL is not under the approved root or does not match the job.");
        return errors;
    }

    private static bool IsAllowedTileUrl(string value, IReadOnlyCollection<string> prefixes)
    {
        if (value.Length is < 1 or > 500 || value.Any(char.IsControl) || value.Contains('?') || value.Contains('#') ||
            !Uri.TryCreate(value.Replace("{dn}", "day").Replace("{z}", "10").Replace("{y}", "0").Replace("{x}", "0"), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
            return false;
        return prefixes.Any(prefix => Uri.TryCreate(prefix, UriKind.Absolute, out var allowed) &&
            uri.IdnHost.Equals(allowed.IdnHost, StringComparison.OrdinalIgnoreCase) &&
            uri.Port == allowed.Port && uri.AbsolutePath.StartsWith(
                allowed.AbsolutePath.TrimEnd('/') + '/', StringComparison.Ordinal));
    }
}
