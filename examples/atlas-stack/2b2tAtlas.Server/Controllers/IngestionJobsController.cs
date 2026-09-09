using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Atlas;
using Atlas.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using _2b2tAtlas.Server.Models;
using _2b2tAtlas.Server.Services;
using ServerLocation = _2b2tAtlas.Server.Models.Location;
using ServerLocationRender = _2b2tAtlas.Server.Models.Render;
using ServerWarp = _2b2tAtlas.Server.Models.Warp;

namespace _2b2tAtlas.Server.Controllers;

/// <summary>
/// Coordinates the durable Atlas world-download ingestion queue between authorized operators
/// and the separately trusted local rendering worker.
/// </summary>
/// <remarks>
/// Operator routes require <c>renders.manage</c>. Worker routes are anonymous to ASP.NET
/// authentication but require the configured worker API key; status updates additionally require
/// a short-lived per-claim token. Successful completion atomically creates or links a location
/// render and writes an audit record.
/// </remarks>
[ApiController]
[Route("api/ingestion-jobs")]
public sealed class IngestionJobsController : ControllerBase
{
    private const string WorkerKeyHeader = "X-Atlas-Worker-Key";
    private const int UploadChunkBytes = 32 * 1024 * 1024;
    private const long MaxBrowserUploadBytes = 32L * 1024 * 1024 * 1024;
    private const int MaxAttempts = 3;
    private static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(15);
    private readonly AtlasContext _context;
    private readonly AuditService _audit;
    private readonly _2b2tAtlas.Server.Services.AiEnrichment.EnrichmentQueue _enrichmentQueue;
    private readonly IngestionMatchAiService? _matchAi;
    private readonly byte[]? _workerKeyHash;
    private readonly string[] _allowedUrlPrefixes;
    private readonly string? _intakeRoot;
    private readonly string? _archiveRoot;
    private static readonly JsonSerializerOptions IntakeMetadataJson = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Initializes the ingestion queue and loads its worker and render-registration trust policy.</summary>
    /// <param name="context">The Atlas context used for queue leases, locations, and render registration.</param>
    /// <param name="audit">The append-only recorder for queue and terminal-state events.</param>
    /// <param name="enrichmentQueue">The background queue that auto-enriches a location once its render attaches.</param>
    /// <param name="configuration">Configuration containing the worker-key digest and allowed tile URL prefixes.</param>
    /// <param name="matchAi">Optional local-model second opinion used only for bounded ambiguous candidates.</param>
    public IngestionJobsController(
        AtlasContext context,
        AuditService audit,
        _2b2tAtlas.Server.Services.AiEnrichment.EnrichmentQueue enrichmentQueue,
        IConfiguration configuration,
        IngestionMatchAiService? matchAi = null)
    {
        _context = context;
        _audit = audit;
        _enrichmentQueue = enrichmentQueue;
        _matchAi = matchAi;
        _allowedUrlPrefixes = configuration.GetSection("MapRenders:AllowedUrlPrefixes").Get<string[]>() ?? [];
        _intakeRoot = configuration["IngestionWorker:IntakeRoot"];
        _archiveRoot = configuration["WdlArchive:Root"];
        var configuredHash = configuration["IngestionWorker:ApiKeySha256"];
        if (configuredHash?.Length == 64)
        {
            try
            {
                _workerKeyHash = Convert.FromHexString(configuredHash);
            }
            catch (FormatException)
            {
                _workerKeyHash = null;
            }
        }
    }

    /// <summary>Returns the 100 most recent ingestion jobs for authorized Atlas operators.</summary>
    /// <returns>Queue state with public job identifiers; claim tokens are never included.</returns>
    [HttpGet]
    [Authorize(Policy = Permissions.RendersManage)]
    public async Task<ActionResult<IReadOnlyList<IngestionJobDto>>> GetAll()
    {
        var rows = await _context.IngestionJobs
            .OrderByDescending(job => job.Id)
            .Take(100)
            .ToListAsync();
        var renderIds = rows.Where(row => row.ExistingLocationId is null && row.RenderId is not null)
            .Select(row => row.RenderId!.Value)
            .ToList();
        var renderLocations = await _context.Renders
            .Where(render => renderIds.Contains(render.Id))
            .ToDictionaryAsync(render => render.Id, render => render.LocationRowid);
        return Ok(rows.Select(row => MapToDto(
            row,
            linkedLocationId: row.RenderId is int renderId && renderLocations.TryGetValue(renderId, out var locationId)
                ? locationId
                : null)).ToList());
    }

    /// <summary>Validates and queues a world download on behalf of the authenticated operator.</summary>
    /// <param name="request">The bounded intake and render-registration metadata.</param>
    /// <returns>The newly persisted queued job, or a validation/conflict response.</returns>
    [HttpPost]
    [Authorize(Policy = Permissions.RendersManage)]
    public async Task<ActionResult<IngestionJobDto>> Create([FromBody] IngestionJobRequest request)
        => await CreateCore(request, CurrentUserId(), User.FindFirstValue(ClaimTypes.Name));

    /// <summary>Queues a local intake request after authenticating the dedicated ingestion worker key.</summary>
    /// <param name="request">The bounded intake and render-registration metadata.</param>
    /// <returns>The newly persisted queued job, or <c>401</c> when the worker key is invalid.</returns>
    [HttpPost("local")]
    [AtlasWorkerKey]
    [AllowAnonymous]
    public async Task<ActionResult<IngestionJobDto>> CreateLocal([FromBody] IngestionJobRequest request)
    {
        if (!HasValidWorkerKey()) return Unauthorized();
        return await CreateCore(request, null, "local-intake");
    }

    /// <summary>
    /// Archives, inspects, and queues a completed ZIP already placed directly in the configured local intake directory.
    /// This is the zero-copy handoff used by a example host-side Archive acquisition client.
    /// </summary>
    /// <remarks>
    /// The supplied name must be a plain ZIP basename, never a path. Callers must finish downloads under a temporary
    /// extension and atomically rename to this name before invoking the route, so the ingestion worker cannot observe
    /// partially written archives.
    /// </remarks>
    [HttpPost("local-intake")]
    [AtlasWorkerKey]
    [AllowAnonymous]
    public async Task<ActionResult<IngestionJobDto>> QueueLocalIntake(
        [FromBody] LocalIntakeQueueRequest request,
        CancellationToken cancellationToken)
    {
        if (!HasValidWorkerKey()) return Unauthorized();
        if (string.IsNullOrWhiteSpace(_intakeRoot) || !Directory.Exists(_intakeRoot) ||
            string.IsNullOrWhiteSpace(_archiveRoot) || !Directory.Exists(_archiveRoot))
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { message = "Ingestion intake or WDL archive storage is unavailable." });

        var metadata = request.Metadata ?? new IngestionJobRequest();
        var fileName = metadata.IntakeFileName?.Trim() ?? string.Empty;
        if (fileName.Length == 0 || fileName != Path.GetFileName(fileName) ||
            !fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "IntakeFileName must be a plain .zip basename." });

        var intakePath = Path.Combine(_intakeRoot, fileName);
        if (!System.IO.File.Exists(intakePath))
            return NotFound(new { message = "The completed ZIP is not present in the local intake directory." });

        return await QueueStoredArchiveAsync(
            metadata, intakePath, fileName, SafeOriginalFileName(request.OriginalFileName), cancellationToken);
    }

    /// <summary>Previews which existing locations a dropped WDL's dimensions would match, without queuing anything.</summary>
    /// <param name="request">The render name and per-dimension centroids computed client-side from region file names.</param>
    /// <returns>Per-dimension match previews (a confident location or ranked suggestions).</returns>
    [HttpPost("match-preview")]
    [Authorize(Policy = Permissions.RendersManage)]
    public async Task<ActionResult<IReadOnlyList<MatchPreviewResult>>> MatchPreview([FromBody] MatchPreviewRequest? request)
    {
        var candidatesIn = request?.Candidates;
        if (candidatesIn is null || candidatesIn.Count == 0)
            return Ok(Array.Empty<MatchPreviewResult>());

        var name = request!.Name ?? string.Empty;
        var results = new List<MatchPreviewResult>();
        foreach (var candidate in candidatesIn)
        {
            var dimensionIndex = DimensionIndex(candidate.Dimension?.Trim().ToLowerInvariant() ?? string.Empty);
            if (dimensionIndex is null) continue;
            var locations = await LoadMatchCandidatesAsync(dimensionIndex.Value);
            var match = LocationMatcher.Match(dimensionIndex.Value, candidate.CenterX, candidate.CenterZ, name, locations);
            var attachName = match.AutoAttachLocationId is int id
                ? locations.FirstOrDefault(location => location.LocationId == id)?.Name
                : null;
            results.Add(new MatchPreviewResult(
                candidate.Dimension!.Trim().ToLowerInvariant(),
                candidate.CenterX,
                candidate.CenterZ,
                match.AutoAttachLocationId,
                attachName,
                match.Suggestions));
        }
        return Ok(results);
    }

    /// <summary>Accepts an authorized operator's world-download archive over HTTP, stores it in the shared intake directory, and queues it.</summary>
    /// <param name="metadata">JSON-encoded <see cref="IngestionJobRequest"/> describing the render.</param>
    /// <param name="archive">The uploaded world-download ZIP.</param>
    /// <param name="cancellationToken">Token that cancels streaming and persistence.</param>
    /// <returns>The newly queued job, or a validation/conflict response.</returns>
    /// <remarks>Enables remote operators to queue ingestions without any local worker. Deep archive inspection remains deferred to the worker's prepare stage.</remarks>
    [HttpPost("upload")]
    [Authorize(Policy = Permissions.RendersManage)]
    [RequestSizeLimit(1_073_741_824)]
    [RequestFormLimits(MultipartBodyLengthLimit = 1_073_741_824)]
    public async Task<ActionResult<IngestionJobDto>> Upload(
        [FromForm] string metadata,
        IFormFile? archive,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_intakeRoot) || !Directory.Exists(_intakeRoot))
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "Ingestion intake storage is unavailable." });
        if (string.IsNullOrWhiteSpace(_archiveRoot) || !Directory.Exists(_archiveRoot))
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "The WDL archive is unavailable; upload was not accepted." });
        if (archive is null || archive.Length == 0)
            return BadRequest(new { message = "A world download ZIP is required." });
        if (!archive.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "The upload must be a .zip archive." });

        IngestionJobRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<IngestionJobRequest>(metadata, IntakeMetadataJson);
        }
        catch (JsonException)
        {
            return BadRequest(new { message = "Invalid ingestion metadata." });
        }
        if (request is null) return BadRequest(new { message = "Invalid ingestion metadata." });

        request.IntakeFileName = "pending.zip";
        var errors = IngestionJobValidator.ValidateRequest(request);
        if (errors.Count > 0) return BadRequest(new { errors });

        var intakeName = $"{request.Slug.Trim().ToLowerInvariant()}-{Guid.NewGuid():N}.zip";
        var partialPath = Path.Combine(_intakeRoot, $".{Guid.NewGuid():N}.partial.zip");
        var finalPath = Path.Combine(_intakeRoot, intakeName);
        try
        {
            await using (var target = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await archive.CopyToAsync(target, cancellationToken);
            }
            await using (var check = new FileStream(partialPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var signature = new byte[4];
                var read = await check.ReadAsync(signature.AsMemory(0, 4), cancellationToken);
                if (read < 4 || signature[0] != 0x50 || signature[1] != 0x4B || signature[2] != 0x03 || signature[3] != 0x04)
                    throw new InvalidDataException("The upload is not a ZIP archive.");
            }
            System.IO.File.Move(partialPath, finalPath);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            TryDeleteIntake(partialPath);
            return BadRequest(new { message = "The uploaded file could not be stored as a valid ZIP archive." });
        }

        try
        {
            return await QueueStoredArchiveAsync(request, finalPath, intakeName, archive.FileName, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            TryDeleteIntake(finalPath);
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { message = "The upload could not be verified in the WDL archive; no job was queued." });
        }
    }

    /// <summary>Starts a resumable upload whose chunks remain below reverse-proxy body limits.</summary>
    [HttpPost("upload-sessions")]
    [Authorize(Policy = Permissions.RendersManage)]
    public async Task<ActionResult<ChunkedUploadSession>> StartUploadSession(
        [FromBody] ChunkedUploadStartRequest start,
        CancellationToken cancellationToken)
    {
        if (!StorageAvailable(out var unavailable)) return unavailable!;
        if (start.TotalBytes is <= 0 or > MaxBrowserUploadBytes)
            return BadRequest(new { message = "Upload size must be between 1 byte and 32 GiB." });
        if (string.IsNullOrWhiteSpace(start.FileName) ||
            !start.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "The upload must be a .zip archive." });

        var request = DeserializeRequest(start.Metadata);
        if (request is null) return BadRequest(new { message = "Invalid ingestion metadata." });
        var userId = CurrentUserId();
        if (userId is null) return Forbid();
        request.IntakeFileName = "pending.zip";
        var errors = IngestionJobValidator.ValidateRequest(request);
        if (errors.Count > 0) return BadRequest(new { errors });

        var id = Guid.NewGuid().ToString("N");
        var root = UploadSessionRoot();
        Directory.CreateDirectory(root);
        CleanupExpiredUploadSessions(root);
        var state = new UploadSessionState(
            id, userId, start.TotalBytes, start.FileName, request, DateTime.UtcNow);
        await System.IO.File.WriteAllTextAsync(
            UploadSessionMetadata(root, id), JsonSerializer.Serialize(state, IntakeMetadataJson), cancellationToken);
        await using (System.IO.File.Create(UploadSessionData(root, id))) { }
        return Ok(new ChunkedUploadSession { Id = id, ChunkSizeBytes = UploadChunkBytes });
    }

    /// <summary>Appends one exactly-positioned binary chunk to a resumable upload.</summary>
    [HttpPut("upload-sessions/{id}/chunks")]
    [Authorize(Policy = Permissions.RendersManage)]
    [RequestSizeLimit(UploadChunkBytes + 1024)]
    public async Task<IActionResult> UploadChunk(
        string id,
        [FromQuery] long offset,
        CancellationToken cancellationToken)
    {
        var state = await ReadUploadSessionAsync(id, cancellationToken);
        if (state is null) return NotFound();
        if (state.UserId != CurrentUserId()) return Forbid();
        var data = UploadSessionData(UploadSessionRoot(), id);
        FileStream output;
        try { output = new FileStream(data, FileMode.Open, FileAccess.Write, FileShare.None, 1_048_576, true); }
        catch (IOException) { return Conflict(new { message = "Another chunk is being written; retry this offset." }); }
        await using (output)
        {
        if (offset != output.Length) return Conflict(new { message = $"Expected upload offset {output.Length}." });
        if (Request.ContentLength is null or <= 0 or > UploadChunkBytes || output.Length + Request.ContentLength > state.TotalBytes)
            return BadRequest(new { message = "Chunk length is invalid." });
        output.Position = output.Length;
        await Request.Body.CopyToAsync(output, cancellationToken);
        if (output.Length > state.TotalBytes) return BadRequest(new { message = "Upload exceeds its declared size." });
        }
        return NoContent();
    }

    /// <summary>Verifies and queues a fully uploaded resumable WDL.</summary>
    [HttpPost("upload-sessions/{id}/complete")]
    [Authorize(Policy = Permissions.RendersManage)]
    public async Task<ActionResult<IngestionJobDto>> CompleteUploadSession(
        string id,
        CancellationToken cancellationToken)
    {
        var state = await ReadUploadSessionAsync(id, cancellationToken);
        if (state is null) return NotFound();
        if (state.UserId != CurrentUserId()) return Forbid();
        var root = UploadSessionRoot();
        var data = UploadSessionData(root, id);
        if (!System.IO.File.Exists(data) || new FileInfo(data).Length != state.TotalBytes)
            return Conflict(new { message = "Upload is incomplete." });

        await using (var check = new FileStream(data, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var signature = new byte[4];
            if (await check.ReadAsync(signature, cancellationToken) != 4 ||
                signature[0] != 0x50 || signature[1] != 0x4B || signature[2] != 0x03 || signature[3] != 0x04)
                return BadRequest(new { message = "The upload is not a ZIP archive." });
        }

        var intakeName = $"{state.Request.Slug.Trim().ToLowerInvariant()}-{Guid.NewGuid():N}.zip";
        var finalPath = Path.Combine(_intakeRoot!, intakeName);
        System.IO.File.Move(data, finalPath);
        TryDeleteIntake(UploadSessionMetadata(root, id));
        try
        {
            return await QueueStoredArchiveAsync(state.Request, finalPath, intakeName, state.FileName, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            TryDeleteIntake(finalPath);
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { message = "The upload could not be verified in the WDL archive; no job was queued." });
        }
    }

    private async Task<ActionResult<IngestionJobDto>> QueueStoredArchiveAsync(
        IngestionJobRequest request,
        string finalPath,
        string intakeName,
        string originalFileName,
        CancellationToken cancellationToken)
    {
        originalFileName = SafeOriginalFileName(originalFileName);
        var archived = await WdlArchiveStore.ArchiveAsync(
            finalPath, _archiveRoot!, expectedSha256: null, cancellationToken);

        request.IntakeFileName = intakeName;
        var requestedDimension = string.IsNullOrWhiteSpace(request.Dimension)
            ? "auto"
            : request.Dimension.Trim().ToLowerInvariant();

        IReadOnlyDictionary<string, (int CenterX, int CenterZ)> archiveCentroids;
        IReadOnlyDictionary<string, int> archiveRegionCounts;
        IReadOnlyList<string> customDimensionIds;
        ArchiveWdlEvidence archiveEvidence;
        try
        {
            archiveCentroids = DetectArchiveDimensionCentroids(finalPath, out customDimensionIds, out archiveRegionCounts);
            archiveEvidence = ArchiveWdlEvidenceReader.ReadArchive(finalPath, originalFileName, request.WorldRoot);
        }
        catch (InvalidDataException)
        {
            TryDeleteIntake(finalPath);
            return BadRequest(new { message = "The archive contents could not be read to detect dimensions." });
        }
        var archiveWarp = ArchiveWarpResolver.Resolve(
            archiveEvidence, originalFileName, request.Source, request.ArchiveWarpName);
        if (archiveWarp is null)
        {
            // A byte-identical WDL may arrive under a renamed ZIP or without its old sidecar metadata.
            // Its immutable digest is stronger identity evidence than either the filename or coordinates,
            // so recover the one already-reviewed Archive warp before dimension ownership is assigned.
            var knownWarp = await _context.Warps.AsNoTracking().SingleOrDefaultAsync(
                warp => warp.ArchiveSha256 == archived.Sha256, cancellationToken);
            if (knownWarp is not null)
                archiveWarp = new ArchiveWarpCandidate(knownWarp.Name, "existing-archive-sha", 1, true);
        }

        IReadOnlyList<string> dimensions;
        if (requestedDimension == "auto")
        {
            if (customDimensionIds.Count > 0)
            {
                TryDeleteIntake(finalPath);
                return BadRequest(new
                {
                    message = "Custom dimension storage was detected (" + string.Join(", ", customDimensionIds.Take(8)) +
                        "). Choose its original 2b2t dimension explicitly; Atlas will not guess.",
                });
            }
            dimensions = SelectAutoDimensions(OrderDimensions(archiveCentroids.Keys));
            if (dimensions.Count == 0)
            {
                TryDeleteIntake(finalPath);
                return BadRequest(new { message = "No recognized Minecraft dimensions (region data) were found in the archive." });
            }
        }
        else
        {
            dimensions = [requestedDimension];
        }

        var single = dimensions.Count == 1;
        var primaryWarpDimension = SelectPrimaryWarpDimension(archiveEvidence, dimensions, archiveRegionCounts);
        var created = new List<IngestionJobDto>();
        ActionResult<IngestionJobDto>? lastFailure = null;
        foreach (var dimension in dimensions)
        {
            var ownsWarp = primaryWarpDimension == "end" ? dimension == "end" : dimension != "end";
            var perDimension = CloneRequestForDimension(request, dimension, single, ownsWarp);
            var perDimensionWarp = ownsWarp ? archiveWarp : null;
            (int CenterX, int CenterZ)? uploadCenter =
                archiveCentroids.TryGetValue(dimension, out var center) ? center : null;
            var result = await CreateCore(perDimension, CurrentUserId(), User.FindFirstValue(ClaimTypes.Name),
                uploadCenter, archiveEvidence, archived.Sha256, perDimensionWarp, originalFileName);
            if (result.Result is CreatedAtActionResult { Value: IngestionJobDto dto })
                created.Add(dto);
            else
                lastFailure = result;
        }
        if (created.Count == 0)
        {
            TryDeleteIntake(finalPath);
            return lastFailure ?? BadRequest(new { message = "No ingestion jobs could be queued from the archive." });
        }
        return CreatedAtAction(nameof(GetAll), created[0]);
    }

    private bool StorageAvailable(out ActionResult<ChunkedUploadSession>? unavailable)
    {
        unavailable = null;
        if (string.IsNullOrWhiteSpace(_intakeRoot) || !Directory.Exists(_intakeRoot) ||
            string.IsNullOrWhiteSpace(_archiveRoot) || !Directory.Exists(_archiveRoot))
        {
            unavailable = StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { message = "Ingestion intake or WDL archive storage is unavailable." });
            return false;
        }
        return true;
    }

    private IngestionJobRequest? DeserializeRequest(string metadata)
    {
        try { return JsonSerializer.Deserialize<IngestionJobRequest>(metadata, IntakeMetadataJson); }
        catch (JsonException) { return null; }
    }

    private string UploadSessionRoot() => Path.Combine(_intakeRoot!, ".uploads");
    private static string UploadSessionMetadata(string root, string id) => Path.Combine(root, id + ".json");
    private static string UploadSessionData(string root, string id) => Path.Combine(root, id + ".partial.zip");

    private async Task<UploadSessionState?> ReadUploadSessionAsync(string id, CancellationToken cancellationToken)
    {
        if (id.Length != 32 || id.Any(character => !Uri.IsHexDigit(character))) return null;
        var path = UploadSessionMetadata(UploadSessionRoot(), id);
        if (!System.IO.File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<UploadSessionState>(
                await System.IO.File.ReadAllTextAsync(path, cancellationToken), IntakeMetadataJson);
        }
        catch (JsonException) { return null; }
    }

    private static void CleanupExpiredUploadSessions(string root)
    {
        foreach (var metadata in Directory.EnumerateFiles(root, "*.json"))
        {
            if (System.IO.File.GetLastWriteTimeUtc(metadata) >= DateTime.UtcNow.AddHours(-24)) continue;
            var id = Path.GetFileNameWithoutExtension(metadata);
            TryDeleteIntake(metadata);
            TryDeleteIntake(UploadSessionData(root, id));
        }
    }

    private sealed record UploadSessionState(
        string Id,
        int? UserId,
        long TotalBytes,
        string FileName,
        IngestionJobRequest Request,
        DateTime CreatedUtc);

    /// <summary>Copies an upload request for one detected dimension, sharing the same intake ZIP and base slug.</summary>
    private static IngestionJobRequest CloneRequestForDimension(
        IngestionJobRequest source,
        string dimension,
        bool single,
        bool ownsArchiveWarp) => new()
    {
        IntakeFileName = source.IntakeFileName,
        Slug = source.Slug,
        Name = source.Name,
        WorldDownloadDate = source.WorldDownloadDate,
        UseArchiveLastPlayed = source.UseArchiveLastPlayed,
        Source = source.Source,
        Scale = source.Scale,
        DayNight = true,
        Dimension = dimension,
        WorldRoot = source.WorldRoot,
        // Overworld and nether both attach to the overworld location the operator confirmed; End has its own.
        ExistingLocationId = single || dimension != "end" ? source.ExistingLocationId : null,
        ArchiveWarpName = ownsArchiveWarp ? source.ArchiveWarpName : null,
        ArchiveWarpX = ownsArchiveWarp ? source.ArchiveWarpX : null,
        ArchiveWarpY = ownsArchiveWarp ? source.ArchiveWarpY : null,
        ArchiveWarpZ = ownsArchiveWarp ? source.ArchiveWarpZ : null,
    };

    /// <summary>
    /// Selects the one logical capture dimension that owns the Archive WDL/warp. Overworld and Nether
    /// project to the same Atlas location; End does not. Recognized report/player metadata outranks the
    /// amount of stored region data, with region count used as a conservative fallback for old exports.
    /// </summary>
    private static string SelectPrimaryWarpDimension(
        ArchiveWdlEvidence evidence,
        IReadOnlyList<string> dimensions,
        IReadOnlyDictionary<string, int> regionCounts)
    {
        if (dimensions.Count == 1) return dimensions[0];
        foreach (var raw in new[] { evidence.ReportedDimension, evidence.PlayerDimension })
        {
            var normalized = NormalizeEvidenceDimension(raw);
            if (normalized is not null && dimensions.Contains(normalized)) return normalized;
        }
        return dimensions
            .OrderByDescending(dimension => regionCounts.GetValueOrDefault(dimension))
            .ThenBy(dimension => Array.IndexOf(new[] { "overworld", "nether", "end" }, dimension))
            .First();
    }

    private static string? NormalizeEvidenceDimension(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized is "-1" or "minecraft:the_nether" or "minecraft:nether") return "nether";
        if (normalized is "1" or "minecraft:the_end" or "minecraft:end") return "end";
        if (normalized is "0" or "minecraft:overworld") return "overworld";
        return InferDimensionFromCustomId(normalized);
    }

    /// <summary>
    /// Resolves the detected dimensions into the set actually queued for an auto-detect upload.
    /// Every recognized dimension is retained. Archive and museum exports can bundle a real End
    /// capture with small Overworld or Nether fragments, so silently dropping a present dimension
    /// would lose data and could misidentify the intended render.
    /// </summary>
    private static IReadOnlyList<string> SelectAutoDimensions(IReadOnlyList<string> present)
    {
        return present;
    }

    /// <summary>Orders the detected dimension keys as overworld, nether, then end.</summary>
    private static IReadOnlyList<string> OrderDimensions(IEnumerable<string> present)
    {
        var set = present as ICollection<string> ?? present.ToList();
        var ordered = new List<string>();
        foreach (var key in new[] { "overworld", "nether", "end" })
            if (set.Contains(key)) ordered.Add(key);
        return ordered;
    }

    /// <summary>
    /// Computes each present dimension's density-weighted block centroid from the region file names
    /// (<c>r.X.Z.mca</c> encode 512-block regions), reading only the ZIP central directory. The densest
    /// region cluster is used so a small spawn portion and a long highway trail don't skew the centroid
    /// away from the actual base. Used for the immediate upload-time / drop-time location match.
    /// </summary>
    private static IReadOnlyDictionary<string, (int CenterX, int CenterZ)> DetectArchiveDimensionCentroids(
        string zipPath,
        out IReadOnlyList<string> customDimensionIds,
        out IReadOnlyDictionary<string, int> dimensionRegionCounts)
    {
        var regions = new Dictionary<string, List<(int RX, int RZ)>>(StringComparer.Ordinal);
        var customRegions = new Dictionary<string, List<(int RX, int RZ)>>(StringComparer.OrdinalIgnoreCase);
        using (var zip = System.IO.Compression.ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/').ToLowerInvariant();
                var segments = name.Split('/');
                var customId = CustomArchiveDimensionId(segments);
                var dimension = ClassifyArchiveDimension(segments);
                var parts = segments[^1].Split('.');
                int rx, rz;
                if ((name.EndsWith(".mca", StringComparison.Ordinal) || name.EndsWith(".mcr", StringComparison.Ordinal)) &&
                    segments.Contains("region") && parts.Length == 4 && parts[0] == "r" &&
                    int.TryParse(parts[1], out rx) && int.TryParse(parts[2], out rz))
                {
                    // Region coordinates are already in 32-by-32 chunk units.
                }
                else if (name.EndsWith(".dat", StringComparison.Ordinal) && parts.Length == 4 && parts[0] == "c" &&
                         segments.Length >= 3 && segments[^2].Length <= 2 && segments[^3].Length <= 2 &&
                         TryParseBase36(parts[1], out var chunkX) && TryParseBase36(parts[2], out var chunkZ))
                {
                    rx = FloorDiv(chunkX, 32);
                    rz = FloorDiv(chunkZ, 32);
                }
                else
                {
                    continue;
                }
                if (customId is not null)
                {
                    if (!customRegions.TryGetValue(customId, out var customList) && customRegions.Count < 128)
                        customRegions[customId] = customList = [];
                    customList?.Add((rx, rz));
                    continue;
                }
                if (dimension is null) continue;
                if (!regions.TryGetValue(dimension, out var list))
                    regions[dimension] = list = [];
                list.Add((rx, rz));
            }
        }

        // A single custom storage root can be normalized safely when its identifier itself is explicit
        // (for example archive:the_nether). Arbitrary museum IDs remain unresolved, and multiple custom
        // roots always fail closed because their contents may have been interleaved for unrelated worlds.
        var unresolvedCustom = new List<string>();
        if (customRegions.Count == 1)
        {
            var (customId, list) = customRegions.Single();
            var inferredDimension = InferDimensionFromCustomId(customId);
            if (inferredDimension is null)
                unresolvedCustom.Add(customId);
            else if (regions.TryGetValue(inferredDimension, out var canonicalRegions) && canonicalRegions.Count > 0)
            {
                // Two occupied roots claiming the same logical dimension may be unrelated museum worlds.
                // Do not merge their centroids or let the worker silently render only the canonical one.
                unresolvedCustom.Add(customId);
            }
            else
            {
                if (!regions.TryGetValue(inferredDimension, out var inferredRegions))
                    regions[inferredDimension] = inferredRegions = [];
                inferredRegions.AddRange(list);
            }
        }
        else
        {
            unresolvedCustom.AddRange(customRegions.Keys);
        }

        var result = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        foreach (var (dimension, list) in regions)
            result[dimension] = DensityCentroid(list);
        customDimensionIds = unresolvedCustom.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        dimensionRegionCounts = regions.ToDictionary(pair => pair.Key, pair => pair.Value.Count, StringComparer.Ordinal);
        return result;
    }

    /// <summary>
    /// Infers only dimension identifiers with an explicit, token-level vanilla meaning. This deliberately
    /// rejects fuzzy substrings and generic names such as <c>world</c>, which are common in museum layouts.
    /// </summary>
    private static string? InferDimensionFromCustomId(string customId)
    {
        var tokens = customId.ToLowerInvariant().Split([':', '/', '.', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Contains("nether") || tokens.Contains("hell")) return "nether";
        if (tokens.Contains("end")) return "end";
        if (tokens.Contains("overworld") || tokens.Contains("surface")) return "overworld";
        return null;
    }

    private static string? CustomArchiveDimensionId(string[] segments)
    {
        var dimensionsIndex = Array.IndexOf(segments, "dimensions");
        var regionIndex = Array.LastIndexOf(segments, "region");
        if (dimensionsIndex < 0 || regionIndex <= dimensionsIndex + 2) return null;
        var dimensionPath = string.Join('/', segments[(dimensionsIndex + 2)..regionIndex]);
        if (segments[dimensionsIndex + 1] == "minecraft" &&
            dimensionPath is "overworld" or "the_nether" or "the_end") return null;
        return segments[dimensionsIndex + 1] + ":" + dimensionPath;
    }

    /// <summary>Classifies only canonical vanilla dimension layouts; unknown custom namespaces are not guessed.</summary>
    private static string? ClassifyArchiveDimension(string[] segments)
    {
        if (segments.Contains("dim-1")) return "nether";
        if (segments.Contains("dim1")) return "end";
        var dimensionsIndex = Array.IndexOf(segments, "dimensions");
        if (dimensionsIndex >= 0 && dimensionsIndex + 2 < segments.Length)
        {
            if (segments[dimensionsIndex + 1] != "minecraft") return null;
            return segments[dimensionsIndex + 2] switch
            {
                "overworld" => "overworld",
                "the_nether" => "nether",
                "the_end" => "end",
                _ => null,
            };
        }
        return "overworld";
    }

    private static int FloorDiv(int value, int divisor)
    {
        var quotient = value / divisor;
        var remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }

    private static bool TryParseBase36(string value, out int result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var negative = value[0] == '-';
        var start = negative ? 1 : 0;
        if (start == value.Length) return false;
        long parsed = 0;
        for (var index = start; index < value.Length; index++)
        {
            var digit = value[index] switch
            {
                >= '0' and <= '9' => value[index] - '0',
                >= 'a' and <= 'z' => value[index] - 'a' + 10,
                _ => -1,
            };
            if (digit < 0 || digit >= 36) return false;
            parsed = parsed * 36 + digit;
            if (parsed > (negative ? -(long)int.MinValue : int.MaxValue)) return false;
        }
        result = checked((int)(negative ? -parsed : parsed));
        return true;
    }

    /// <summary>Returns the block centroid of the densest region cluster (D=2 neighborhood, C=8 cluster window).</summary>
    internal static (int CenterX, int CenterZ) DensityCentroid(List<(int RX, int RZ)> regions)
    {
        const int RegionBlocks = 512, DensityRadius = 2, ClusterRadius = 8;
        var present = new HashSet<(int, int)>(regions);
        regions.Sort((a, b) => a.RX != b.RX ? a.RX.CompareTo(b.RX) : a.RZ.CompareTo(b.RZ));

        var anchor = regions[0];
        var best = -1;
        foreach (var (rx, rz) in regions)
        {
            var count = 0;
            for (var dx = -DensityRadius; dx <= DensityRadius; dx++)
                for (var dz = -DensityRadius; dz <= DensityRadius; dz++)
                    if (present.Contains((rx + dx, rz + dz))) count++;
            if (count > best) { best = count; anchor = (rx, rz); }
        }

        long sumX = 0, sumZ = 0;
        var n = 0;
        foreach (var (rx, rz) in regions)
        {
            if (Math.Abs(rx - anchor.RX) > ClusterRadius || Math.Abs(rz - anchor.RZ) > ClusterRadius) continue;
            sumX += (long)rx * RegionBlocks + 256;
            sumZ += (long)rz * RegionBlocks + 256;
            n++;
        }
        return ((int)(sumX / n), (int)(sumZ / n));
    }

    private static void TryDeleteIntake(string path)
    {
        try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private async Task<ActionResult<IngestionJobDto>> CreateCore(
        IngestionJobRequest request,
        int? requestedByUserId,
        string? requestedByUsername,
        (int CenterX, int CenterZ)? uploadCenter = null,
        ArchiveWdlEvidence? archiveEvidence = null,
        string? archiveSha256 = null,
        ArchiveWarpCandidate? archiveWarp = null,
        string? originalFileName = null)
    {
        var errors = IngestionJobValidator.ValidateRequest(request);
        if (errors.Count > 0) return BadRequest(new { errors });
        var dimension = string.IsNullOrWhiteSpace(request.Dimension) ? "overworld" : request.Dimension.Trim().ToLowerInvariant();
        if (dimension == "auto")
            return BadRequest(new { message = "The render dimension must be resolved before a job is queued." });
        // Cancelled/failed jobs release the slug reservation so it can be re-uploaded.
        var reservedJob = await _context.IngestionJobs.FirstOrDefaultAsync(job =>
            job.Slug == request.Slug && job.Dimension == dimension &&
            job.Status != "cancelled" && job.Status != "failed");
        if (reservedJob is not null)
        {
            // A local intake caller may lose the HTTP response after the job is durably committed. Treat an
            // exact immutable-archive retry as success so its client can reconcile state without creating a
            // duplicate render. Ordinary slug collisions remain conflicts.
            if (!string.IsNullOrWhiteSpace(archiveSha256) &&
                string.Equals(reservedJob.ArchiveSha256, archiveSha256, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(reservedJob.IntakeFileName, request.IntakeFileName, StringComparison.OrdinalIgnoreCase))
                return CreatedAtAction(nameof(GetAll), MapToDto(reservedJob));
            return Conflict(new { message = "That ingestion slug is already reserved for this dimension." });
        }
        if (request.ExistingLocationId is int locationId &&
            !await _context.Locations.AnyAsync(location => location.Rowid == locationId))
            return BadRequest(new { message = "The selected location does not exist." });

        var row = new IngestionJob
        {
            PublicId = Guid.NewGuid().ToString("N"),
            IntakeFileName = request.IntakeFileName.Trim(),
            OriginalFileName = originalFileName,
            Slug = request.Slug.Trim(),
            Name = request.Name.Trim(),
            WorldDownloadDate = request.WorldDownloadDate,
            UseArchiveLastPlayed = request.UseArchiveLastPlayed == false ? 0 : 1,
            Source = request.Source.Trim(),
            Scale = request.Scale.Trim().ToLowerInvariant(),
            Dimension = dimension,
            WorldRoot = request.WorldRoot,
            DayNight = 1,
            ExistingLocationId = request.ExistingLocationId,
            MatchResolved = request.ExistingLocationId is null ? 0 : 1,
            MatchDecision = request.ExistingLocationId is null ? null : "manual-existing",
            MatchConfidence = request.ExistingLocationId is null ? null : 1,
            MatchReason = request.ExistingLocationId is null ? null : "Location selected by the authenticated uploader.",
            RequestedByUserId = requestedByUserId,
            RequestedByUsername = requestedByUsername,
            ArchiveEvidenceJson = archiveEvidence is null ? null : JsonSerializer.Serialize(archiveEvidence),
            ArchiveSha256 = archiveSha256,
            ArchiveWarpName = archiveWarp?.Name,
            ArchiveWarpX = request.ArchiveWarpX,
            ArchiveWarpY = request.ArchiveWarpY,
            ArchiveWarpZ = request.ArchiveWarpZ,
            ArchiveWarpSource = archiveWarp?.Source,
            RenderTopY = ArchiveWarpResolver.RecommendedRenderTopY(archiveWarp),
            // Do not expose the row to the worker until the upload-time matcher has
            // finished. A fast worker used to claim and even complete the job between
            // this method's first and second SaveChanges calls, allowing the stale
            // preliminary match result to race the authoritative prepare-time result.
            Status = "matching",
        };
        _context.IngestionJobs.Add(row);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            return Conflict(new { message = "That ingestion slug is already reserved." });
        }

        // Upload-time match on the density-weighted region-name centroid, so the operator sees the
        // suggested location (or a needs-match to confirm) immediately, before any worker render.
        if (uploadCenter is { } center && row.ExistingLocationId is null && row.MatchResolved == 0)
        {
            await ApplyMatchAsync(row, center.CenterX, center.CenterZ, archiveEvidence, archiveWarp,
                incomingFootprint: null, parkIfUnresolved: false);
        }
        if (row.Status == "matching") row.Status = "queued";
        await _context.SaveChangesAsync();

        await _audit.LogAsync("ingestion.queue", "IngestionJob", row.Id, row.RequestedByUserId,
            row.RequestedByUsername, $"Queued WDL ingestion '{row.Name}' ({row.PublicId})");
        return CreatedAtAction(nameof(GetAll), MapToDto(row));
    }

    /// <summary>Cancels an unclaimed queued job and records the administrator action.</summary>
    /// <param name="publicId">The opaque job identifier exposed outside the database.</param>
    /// <returns><c>204</c> on cancellation, <c>404</c> if absent, or <c>409</c> once work has started.</returns>
    [HttpDelete("{publicId}")]
    [Authorize(Policy = Permissions.RendersManage)]
    public async Task<IActionResult> Cancel(string publicId)
    {
        var row = await _context.IngestionJobs.AsNoTracking().SingleOrDefaultAsync(job => job.PublicId == publicId);
        if (row is null) return NotFound();
        var now = DateTime.UtcNow.ToString("o");
        var changed = await _context.IngestionJobs
            .Where(job => job.PublicId == publicId && (job.Status == "queued" || job.Status == "needs-match"))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, "cancelled")
                .SetProperty(job => job.Message, "Cancelled by an administrator.")
                .SetProperty(job => job.UpdatedUtc, now)
                .SetProperty(job => job.CompletedUtc, now));
        if (changed != 1) return Conflict(new { message = "Only queued or awaiting-match jobs can be cancelled." });
        await _audit.LogAsync("ingestion.cancel", "IngestionJob", row.Id, CurrentUserId(),
            User.FindFirstValue(ClaimTypes.Name), $"Cancelled WDL ingestion '{row.Name}' ({row.PublicId})");
        return NoContent();
    }

    /// <summary>Atomically leases the oldest eligible job to the authenticated local worker.</summary>
    /// <returns>
    /// The claimed job and its one-time plaintext claim token, <c>204</c> when the queue is empty,
    /// or <c>401</c> when the worker key is invalid.
    /// </returns>
    /// <remarks>Expired leases may be reclaimed up to the configured attempt limit.</remarks>
    [HttpPost("claim")]
    [AtlasWorkerKey]
    [AllowAnonymous]
    public async Task<ActionResult<IngestionJobDto>> Claim()
    {
        if (!HasValidWorkerKey()) return Unauthorized();

        var nowUtc = DateTimeOffset.UtcNow;
        var now = nowUtc.ToString("o");
        var interruptedPreflightCutoff = nowUtc.Subtract(TimeSpan.FromMinutes(5)).ToString("o");
        await _context.IngestionJobs
            .Where(job => job.Status == "matching" && job.RequestedUtc.CompareTo(interruptedPreflightCutoff) < 0)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, "queued")
                .SetProperty(job => job.Message,
                    "Recovered an interrupted upload-time match; the worker will perform authoritative inspection.")
                .SetProperty(job => job.UpdatedUtc, now));
        await _context.IngestionJobs
            .Where(job => job.AttemptCount >= MaxAttempts &&
                job.LeaseExpiresUtc != null && job.LeaseExpiresUtc.CompareTo(now) < 0 &&
                (job.Status == "claimed" || job.Status == "running"))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, "failed")
                .SetProperty(job => job.Stage, "lease")
                .SetProperty(job => job.Message, "Worker lease expired after the maximum attempt count.")
                .SetProperty(job => job.UpdatedUtc, now)
                .SetProperty(job => job.CompletedUtc, now));

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var publicId = await _context.IngestionJobs
                .Where(job => job.AttemptCount < MaxAttempts &&
                    (job.Status == "queued" ||
                     ((job.Status == "claimed" || job.Status == "running") &&
                      job.LeaseExpiresUtc != null && job.LeaseExpiresUtc.CompareTo(now) < 0)))
                .OrderBy(job => job.Id)
                .Select(job => job.PublicId)
                .FirstOrDefaultAsync();
            if (publicId is null) return NoContent();

            var claimToken = CreateClaimToken();
            var tokenHash = HashClaimToken(claimToken);
            var leaseExpires = nowUtc.Add(ClaimLease).ToString("o");
            var changed = await _context.IngestionJobs
                .Where(job => job.PublicId == publicId && job.AttemptCount < MaxAttempts &&
                    (job.Status == "queued" ||
                     ((job.Status == "claimed" || job.Status == "running") &&
                      job.LeaseExpiresUtc != null && job.LeaseExpiresUtc.CompareTo(now) < 0)))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.Status, "claimed")
                    .SetProperty(job => job.Stage, "claimed")
                    .SetProperty(job => job.Message, "Claimed by the local ingestion worker.")
                    .SetProperty(job => job.ClaimTokenSha256, tokenHash)
                    .SetProperty(job => job.AttemptCount, job => job.AttemptCount + 1)
                    .SetProperty(job => job.ClaimedUtc, now)
                    .SetProperty(job => job.LeaseExpiresUtc, leaseExpires)
                    .SetProperty(job => job.UpdatedUtc, now));
            if (changed == 1)
            {
                var row = await _context.IngestionJobs.SingleAsync(job => job.PublicId == publicId);
                return Ok(MapToDto(row, claimToken));
            }
        }

        return Conflict(new { message = "The queue changed while claiming a job; retry." });
    }

    /// <summary>Accepts a leased worker's progress or terminal update and renews its claim lease.</summary>
    /// <param name="publicId">The opaque identifier of the claimed job.</param>
    /// <param name="update">Worker status carrying the per-claim token and bounded completion metadata.</param>
    /// <returns>The persisted job state, or an authorization, validation, or lease-conflict response.</returns>
    /// <remarks>
    /// A completed location render is registered in the same database transaction as the terminal
    /// job state. Allowed URL prefixes and coordinate bounds are revalidated at this trust boundary.
    /// </remarks>
    [HttpPut("{publicId}/status")]
    [AtlasWorkerKey]
    [AllowAnonymous]
    public async Task<ActionResult<IngestionJobDto>> UpdateStatus(string publicId, [FromBody] IngestionJobUpdate update)
    {
        if (!HasValidWorkerKey()) return Unauthorized();
        var errors = IngestionJobValidator.ValidateUpdate(update);
        if (errors.Count > 0) return BadRequest(new { errors });

        var row = await _context.IngestionJobs.SingleOrDefaultAsync(job => job.PublicId == publicId);
        if (row is null) return NotFound();
        if (!HasValidClaimToken(row, update.ClaimToken)) return Unauthorized();
        if (row.Status == update.Status && row.Status is "completed" or "failed")
            return Ok(MapToDto(row));
        if (row.Status is "completed" or "failed" or "cancelled")
            return Conflict(new { message = "Terminal jobs cannot be updated." });
        if (row.Status is not ("claimed" or "running"))
            return Conflict(new { message = "The job has not been claimed." });
        if (update.Status == "completed" && update.LocationRender is not null &&
            row.RerenderRequested != 1 && (row.MatchResolved != 1 || row.MatchDecision == "review"))
            return Conflict(new
            {
                message = "The location match is unresolved; completion cannot register a render, warp, or location."
            });
        var nowUtc = DateTimeOffset.UtcNow;
        if (!DateTimeOffset.TryParse(row.LeaseExpiresUtc, out var leaseExpiresUtc) || leaseExpiresUtc <= nowUtc)
            return Conflict(new { message = "The worker claim lease has expired." });

        await using var completionTransaction = update.Status == "completed"
            ? await _context.Database.BeginTransactionAsync()
            : null;
        ServerLocation? targetLocation = null;
        ServerLocationRender? existingRender = null;
        if (update.Status == "completed" && update.LocationRender is not null)
        {
            errors = IngestionCompletionValidator.Validate(MapToDto(row), update.LocationRender, _allowedUrlPrefixes).ToList();
            if (errors.Count > 0) return BadRequest(new { errors });
            var reserved = await _context.IngestionJobs
                .Where(job => job.Id == row.Id && (job.Status == "claimed" || job.Status == "running"))
                .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.Status, "completing"));
            if (reserved != 1)
                return Conflict(new { message = "Another request is completing this job." });
            if (row.RerenderRequested == 1 && row.RenderId is int existingRenderId)
            {
                existingRender = await _context.Renders.SingleOrDefaultAsync(render => render.Id == existingRenderId);
                if (existingRender is null)
                    return Conflict(new { message = "The existing render selected for replacement no longer exists." });
                if (row.ExistingLocationId is null)
                    targetLocation = await _context.Locations.SingleOrDefaultAsync(
                        location => location.Rowid == existingRender.LocationRowid);
            }
            if (await _context.Renders.AnyAsync(render =>
                    render.TilesPath == update.LocationRender.TilesPath &&
                    (existingRender == null || render.Id != existingRender.Id)))
                return Conflict(new { message = "That location render is already registered." });
            if (row.ExistingLocationId is int locationId)
            {
                targetLocation = await _context.Locations.SingleOrDefaultAsync(location => location.Rowid == locationId);
                if (targetLocation is null)
                    return Conflict(new { message = "The selected location no longer exists." });
                if (targetLocation.Dimension != LocationDimensionFor(update.LocationRender.Dimension))
                    return BadRequest(new { message = "The selected location is in a different dimension." });
            }
        }

        row.Status = update.Status;
        row.Stage = update.Stage?.Trim();
        row.Message = update.Message?.Trim();
        row.ProgressPercent = update.Status == "completed" ? 100 : update.ProgressPercent;
        row.EtaSeconds = update.Status is "completed" or "failed" ? null : update.EtaSeconds;
        if (update.Inspection is not null)
        {
            update.Inspection.ArchiveEvidence = ArchiveWdlEvidence.Merge(
                ParseArchiveEvidence(row.ArchiveEvidenceJson), update.Inspection.ArchiveEvidence);
            row.InspectionJson = JsonSerializer.Serialize(update.Inspection);
            row.ArchiveEvidenceJson = update.Inspection.ArchiveEvidence is null
                ? row.ArchiveEvidenceJson
                : JsonSerializer.Serialize(update.Inspection.ArchiveEvidence);
            // A museum capture's level.dat is touched when the exhibit is visited, so LastPlayed
            // describes our capture session rather than the historical WDL. Preserve the Archive
            // catalog/warp date whenever recognized Archive evidence is present.
            if (row.UseArchiveLastPlayed == 1 &&
                update.Inspection.ArchiveEvidence?.IsArchiveSource != true &&
                update.Inspection.LastPlayedUtc is DateTime lastPlayed)
            {
                var utc = lastPlayed.Kind == DateTimeKind.Utc ? lastPlayed : lastPlayed.ToUniversalTime();
                if (utc >= new DateTime(2010, 1, 1, 0, 0, 0, DateTimeKind.Utc) &&
                    utc <= DateTime.UtcNow.AddDays(1))
                    row.WorldDownloadDate = utc.ToString("yyyy-MM-dd");
            }
        }
        row.ArchiveSha256 = update.ArchiveSha256 ?? row.ArchiveSha256;
        row.UpdatedUtc = nowUtc.ToString("o");
        row.LeaseExpiresUtc = nowUtc.Add(ClaimLease).ToString("o");
        if (update.Status is "completed" or "failed") row.CompletedUtc = row.UpdatedUtc;

        // Prepare-time auto-match: link a confidently matched location, or park for manual matching.
        if (update.Status == "running" && update.Stage == "prepare" &&
            update.Inspection is not null && row.ExistingLocationId is null && row.MatchResolved == 0)
        {
            await TryAutoMatchAsync(row, update.Inspection);
        }

        ServerLocationRender? render = null;
        ServerWarp? archiveWarp = null;
        if (update.Status == "completed")
        {
            if (targetLocation is null && existingRender is null)
            {
                var lateIdentityMatch = await FindLateIdentityMatchAsync(row, update.LocationRender!.Dimension);
                if (lateIdentityMatch.Error is not null)
                    return Conflict(new { message = lateIdentityMatch.Error });
                targetLocation = lateIdentityMatch.Location;
                existingRender = lateIdentityMatch.ExistingRender;
            }
            targetLocation ??= CreateLocation(row, update.LocationRender!);
            var hasEstablishedRender = targetLocation.Rowid > 0 &&
                await _context.Renders.AsNoTracking().AnyAsync(candidate =>
                    candidate.LocationRowid == targetLocation.Rowid);
            render = existingRender ?? CreateRender(row, update.LocationRender!, targetLocation);
            if (existingRender is not null)
                ApplyRender(existingRender, row, update.LocationRender!, targetLocation);
            var warpResult = await EnsureArchiveWarpAsync(row, targetLocation);
            if (warpResult.Error is not null)
                return Conflict(new { message = warpResult.Error });
            archiveWarp = warpResult.Warp;
            render.ArchiveWarp = archiveWarp;
            render.ArchiveWarpId = archiveWarp is { Id: > 0 } ? archiveWarp.Id : null;
            ApplyArchiveWarpCoordinates(
                row, update.LocationRender!, targetLocation, archiveWarp, hasEstablishedRender);
            if (existingRender is null)
            targetLocation.ModifiedUtc = nowUtc.ToString("o");
            row.RerenderRequested = 0;
        }
        if (update.Status is "completed" or "failed")
        {
            await _audit.LogAsync(
                update.Status == "completed" && existingRender is not null
                    ? "ingestion.rerender.complete"
                    : update.Status == "completed" ? "ingestion.complete" : "ingestion.failed",
                "IngestionJob",
                row.Id,
                null,
                "ingestion-worker",
                update.Status == "completed"
                    ? $"Completed WDL ingestion and linked location render '{render!.Name}'"
                    : $"WDL ingestion failed in stage '{row.Stage}'",
                save: false);
        }
        if (render is not null)
        {
            await _context.SaveChangesAsync();
            row.RenderId = render.Id;
            row.ExistingLocationId = targetLocation?.Rowid;
            row.WarpId = archiveWarp?.Id;
            // Resolve explicit group provenance in the publication transaction, before optional AI.
            // Include rerenders/new warps attached to an existing location, not only new locations.
            if (targetLocation is not null)
                await new ArchiveGroupAttributionService(_context).StageAsync(targetLocation.Rowid);
            await _context.SaveChangesAsync();
            await completionTransaction!.CommitAsync();
            // A base now has a render attached: enrich it in the background (wiki match + description).
            if (targetLocation is not null && existingRender is null)
                _enrichmentQueue.Enqueue(targetLocation.Rowid);
        }
        else
            await _context.SaveChangesAsync();
        return Ok(MapToDto(row));
    }

    /// <summary>Resolves a job awaiting a manual match by attaching a location or requesting a new one, then re-queues it.</summary>
    /// <param name="publicId">The opaque identifier of the parked job.</param>
    /// <param name="request">The chosen location, or a null location to create one at the render centroid.</param>
    /// <returns>The re-queued job, or a not-found, conflict, or validation response.</returns>
    [HttpPost("{publicId}/match")]
    [Authorize(Policy = Permissions.RendersManage)]
    public async Task<ActionResult<IngestionJobDto>> ResolveMatch(string publicId, [FromBody] ResolveMatchRequest? request)
    {
        request ??= new ResolveMatchRequest();
        var row = await _context.IngestionJobs.SingleOrDefaultAsync(job => job.PublicId == publicId);
        if (row is null) return NotFound();
        if (row.Status != "needs-match")
            return Conflict(new { message = "Only jobs awaiting a manual match can be resolved." });

        var reviewedWarp = ArchiveWarpResolver.Resolve(
            ParseArchiveEvidence(row.ArchiveEvidenceJson), row.OriginalFileName ?? row.IntakeFileName, row.Source,
            request.WarpName ?? row.ArchiveWarpName);
        var resolvedLocationId = request.LocationId;
        if (reviewedWarp is not null)
        {
            var normalizedWarp = ArchiveWarpResolver.Normalize(reviewedWarp.Name);
            var existingWarpLocations = (await _context.Warps.ToListAsync())
                .Where(warp => ArchiveWarpResolver.Normalize(warp.Name) == normalizedWarp)
                .Select(warp => warp.LocationRowid).Where(id => id.HasValue).Select(id => id!.Value)
                .Distinct().ToList();
            if (existingWarpLocations.Count > 1)
                return Conflict(new { message = "That Archive warp currently points to multiple Atlas locations and must be repaired first." });
            if (existingWarpLocations.Count == 1)
            {
                if (resolvedLocationId is int requestedId && requestedId != existingWarpLocations[0])
                    return Conflict(new { message = "That Archive warp already belongs to a different Atlas location." });
                resolvedLocationId = existingWarpLocations[0];
            }
        }

        if (resolvedLocationId is int locationId)
        {
            var location = await _context.Locations.SingleOrDefaultAsync(candidate => candidate.Rowid == locationId);
            if (location is null)
                return BadRequest(new { message = "The selected location does not exist." });
            if (DimensionIndex(row.Dimension) is int renderDimension && location.Dimension != LocationDimensionFor(renderDimension))
                return BadRequest(new { message = "The selected location is in a different dimension." });
            row.ExistingLocationId = locationId;
        }
        else
        {
            row.ExistingLocationId = null;
        }

        row.ArchiveWarpName = reviewedWarp?.Name;
        row.ArchiveWarpSource = reviewedWarp?.Source;
        row.RenderTopY = ArchiveWarpResolver.RecommendedRenderTopY(reviewedWarp);
        row.MatchResolved = 1;
        row.MatchDecision = resolvedLocationId is null ? "manual-new" : "manual-existing";
        row.MatchConfidence = 1;
        row.MatchReason = "Resolved by an authenticated Atlas operator.";
        row.MatchSuggestionsJson = null;
        row.Status = "queued";
        row.Stage = "matched";
        row.Message = resolvedLocationId is not null
            ? "Matched to a location; re-queued for rendering."
            : "Will create a new location at the render centroid; re-queued for rendering.";
        row.AttemptCount = 0;
        row.ClaimTokenSha256 = null;
        row.ClaimedUtc = null;
        row.LeaseExpiresUtc = null;
        row.UpdatedUtc = DateTime.UtcNow.ToString("o");
        await _context.SaveChangesAsync();
        await _audit.LogAsync("ingestion.match", "IngestionJob", row.Id, CurrentUserId(), User.FindFirstValue(ClaimTypes.Name),
            resolvedLocationId is int matchedId
                ? $"Matched WDL job '{row.Name}' to location {matchedId}"
                : $"Set WDL job '{row.Name}' to create a new location at its centroid");
        return Ok(MapToDto(row));
    }

    private ServerLocation CreateLocation(IngestionJob job, LocationRenderCompletion dto)
    {
        var centerX = (int)(((long)dto.MinX + dto.MaxXExclusive) / 2);
        var centerZ = (int)(((long)dto.MinZ + dto.MaxZExclusive) / 2);
        // Nether renders have no separate nether location: create the overworld location at the *8 position.
        var isNether = dto.Dimension == 1;
        var archivePosition = TrustedArchiveLocationPosition(job, dto.Dimension);
        var archiveIdentity = ArchiveDisplayName(job);
        var now = DateTime.UtcNow.ToString("o");
        var location = new ServerLocation
        {
            LocationUuid = Guid.NewGuid().ToString(),
            Name = archiveIdentity.Length > 0 ? archiveIdentity : job.Name,
            Description = null,
            Dimension = isNether ? 0 : dto.Dimension,
            X = archivePosition?.X ?? (isNether ? centerX * 8 : centerX),
            Y = archivePosition?.Y ?? 64,
            Z = archivePosition?.Z ?? (isNether ? centerZ * 8 : centerZ),
            DateAddedUtc = now,
            ModifiedUtc = now,
        };
        _context.Locations.Add(location);
        return location;
    }

    /// <summary>
    /// Persists the immutable WDL's live Archive landing coordinate on its one-warp record. A location
    /// without a render is aligned to that authoritative position. Once a location has an established
    /// render, only a nearby landing may refine its canonical coordinate; a distant rebuild, concept,
    /// relocation, or bad manual match must not move the existing Atlas entity across the map.
    /// The controller receives this coordinate only from explicitly reviewed acquisition metadata; the
    /// downloader's final player position is not used because adaptive traversal moves the client away
    /// from the original warp landing point.
    /// </summary>
    private static void ApplyArchiveWarpCoordinates(
        IngestionJob job,
        LocationRenderCompletion render,
        ServerLocation location,
        ServerWarp? warp,
        bool hasEstablishedRender)
    {
        if (warp is null || job.ArchiveWarpX is not double archiveX ||
            job.ArchiveWarpY is not double archiveY || job.ArchiveWarpZ is not double archiveZ)
            return;

        warp.ArchiveX = archiveX;
        warp.ArchiveY = archiveY;
        warp.ArchiveZ = archiveZ;
        var position = TrustedArchiveLocationPosition(job, render.Dimension);
        if (position is null) return;
        if (hasEstablishedRender)
        {
            var dx = (long)position.Value.X - location.X;
            var dz = (long)position.Value.Z - location.Z;
            var distance = Math.Sqrt(dx * (double)dx + dz * (double)dz);
            if (distance > LocationMatcher.CandidateRadius(render.Dimension)) return;
        }
        location.X = position.Value.X;
        location.Y = position.Value.Y;
        location.Z = position.Value.Z;
    }

    private static (int X, int Y, int Z)? TrustedArchiveLocationPosition(IngestionJob job, int renderDimension)
    {
        if (job.ArchiveWarpSource is not ("existing-archive-sha" or "operator" or
            "archive-download-report" or "archive-filename") ||
            job.ArchiveWarpX is not double rawX || job.ArchiveWarpY is not double rawY ||
            job.ArchiveWarpZ is not double rawZ ||
            !double.IsFinite(rawX) || !double.IsFinite(rawY) || !double.IsFinite(rawZ))
            return null;

        var scale = renderDimension == 1 ? 8d : 1d;
        var x = rawX * scale;
        var z = rawZ * scale;
        if (Math.Abs(x) > 30_000_256 || Math.Abs(z) > 30_000_256 || Math.Abs(rawY) > int.MaxValue)
            return null;
        return (
            (int)Math.Round(x, MidpointRounding.AwayFromZero),
            (int)Math.Round(rawY, MidpointRounding.AwayFromZero),
            (int)Math.Round(z, MidpointRounding.AwayFromZero));
    }

    private static string SafeOriginalFileName(string? value)
    {
        var leaf = Path.GetFileName((value ?? string.Empty).Replace('/', Path.DirectorySeparatorChar));
        return leaf.Length is >= 5 and <= 240 && !leaf.Any(char.IsControl) &&
               leaf.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? leaf
            : "uploaded-wdl.zip";
    }

    /// <summary>
    /// Reuses an existing Archive warp or creates exactly one for the immutable WDL. Warp names are
    /// globally identifying on the Archive; a collision pointing at another location fails closed.
    /// </summary>
    private async Task<(ServerWarp? Warp, string? Error)> EnsureArchiveWarpAsync(
        IngestionJob job,
        ServerLocation targetLocation)
    {
        if (string.IsNullOrWhiteSpace(job.ArchiveWarpName)) return (null, null);
        var normalized = ArchiveWarpResolver.Normalize(job.ArchiveWarpName);
        if (normalized.Length == 0) return (null, "The reviewed Archive warp name is invalid.");

        if (!string.IsNullOrWhiteSpace(job.ArchiveSha256))
        {
            var sameArchive = await _context.Warps.SingleOrDefaultAsync(
                warp => warp.ArchiveSha256 == job.ArchiveSha256);
            if (sameArchive is not null)
            {
                if ((targetLocation.Rowid == 0 || sameArchive.LocationRowid != targetLocation.Rowid) &&
                    !await TryRehomeConflictingWarpAsync(job, sameArchive, targetLocation))
                    return (null, "This immutable WDL is already tied to an Archive warp on another Atlas location.");
                return (sameArchive, null);
            }
        }

        var sameName = (await _context.Warps.ToListAsync())
            .Where(warp => ArchiveWarpResolver.Normalize(warp.Name) == normalized)
            .OrderBy(warp => warp.Id)
            .ToList();
        if (sameName.Count > 0)
        {
            var locationIds = sameName.Select(warp => warp.LocationRowid).Distinct().ToList();
            var existing = sameName[0];
            if (targetLocation.Rowid == 0 || locationIds.Any(id => id != targetLocation.Rowid))
            {
                if (sameName.Count != 1 || !await TryRehomeConflictingWarpAsync(job, existing, targetLocation))
                    return (null, "That Archive /warp already belongs to another Atlas location; manual review is required.");
            }
            if (string.IsNullOrWhiteSpace(existing.ArchiveSha256) || IsArchiveCollector(job))
            {
                existing.ArchiveSha256 = job.ArchiveSha256;
                existing.WorldDownloadDate = job.WorldDownloadDate;
                existing.Source = job.Source;
            }
            return (existing, null);
        }

        var created = new ServerWarp
        {
            WarpUuid = Guid.NewGuid().ToString(),
            LocationUuidFk = targetLocation.LocationUuid,
            LocationRow = targetLocation,
            Name = job.ArchiveWarpName,
            TimeAdded = DateTime.UtcNow.ToString("o"),
            ArchiveSha256 = job.ArchiveSha256,
            WorldDownloadDate = job.WorldDownloadDate,
            Source = job.Source,
        };
        _context.Warps.Add(created);
        return (created, null);
    }

    /// <summary>
    /// Repairs one legacy warp association only when deterministic numbered-iteration evidence proves that
    /// the owning Atlas location is a different base. The warp record remains unique and keeps its history.
    /// </summary>
    private async Task<bool> TryRehomeConflictingWarpAsync(
        IngestionJob job,
        ServerWarp warp,
        ServerLocation targetLocation)
    {
        if (job.MatchDecision != "new-rehome" || warp.LocationRowid is not int ownerId ||
            string.IsNullOrWhiteSpace(job.ArchiveWarpName))
            return false;
        var owner = await _context.Locations.SingleOrDefaultAsync(location => location.Rowid == ownerId);
        if (owner is null || !ArchiveWarpResolver.HasIterationConflict(job.ArchiveWarpName, owner.Name))
            return false;
        warp.LocationRow = targetLocation;
        warp.LocationRowid = targetLocation.Rowid == 0 ? null : targetLocation.Rowid;
        warp.LocationUuidFk = targetLocation.LocationUuid;
        return true;
    }

    private ServerLocationRender CreateRender(
        IngestionJob job,
        LocationRenderCompletion dto,
        ServerLocation location)
    {
        var render = new ServerLocationRender
        {
            LocationRow = location,
            Name = RenderDisplayName(job, location),
            Description = RenderDescription(job),
            Source = RenderSource(job),
            Dimension = dto.Dimension,
            Scale = job.Scale,
            TilesPath = dto.TilesPath.Trim(),
            HasDayNight = dto.HasDayNight ? 1 : 0,
            WorldDownloadDate = job.WorldDownloadDate,
            MinX = dto.MinX,
            MinZ = dto.MinZ,
            MaxXExclusive = dto.MaxXExclusive,
            MaxZExclusive = dto.MaxZExclusive,
            MaxNativeZoom = dto.MaxNativeZoom,
            CoordinateScheme = dto.CoordinateScheme,
            IsPublic = 1,
            DateAddedUtc = DateTime.UtcNow.ToString("o"),
        };
        _context.Renders.Add(render);
        return render;
    }

    private static void ApplyRender(
        ServerLocationRender render,
        IngestionJob job,
        LocationRenderCompletion dto,
        ServerLocation location)
    {
        render.LocationRow = location;
        render.Name = RenderDisplayName(job, location);
        render.Description = RenderDescription(job);
        render.Source = RenderSource(job);
        render.Dimension = dto.Dimension;
        render.Scale = job.Scale;
        render.TilesPath = dto.TilesPath.Trim();
        render.HasDayNight = dto.HasDayNight ? 1 : 0;
        render.WorldDownloadDate = job.WorldDownloadDate;
        render.MinX = dto.MinX;
        render.MinZ = dto.MinZ;
        render.MaxXExclusive = dto.MaxXExclusive;
        render.MaxZExclusive = dto.MaxZExclusive;
        render.MaxNativeZoom = dto.MaxNativeZoom;
        render.CoordinateScheme = dto.CoordinateScheme;
    }

    private static int? DimensionIndex(string dimension) => dimension switch
    {
        "overworld" => 0,
        "nether" => 1,
        "end" => 2,
        _ => null,
    };

    private static string ArchiveDisplayName(IngestionJob job)
    {
        if (job.ArchiveWarpSource is not ("existing-archive-sha" or "operator" or
            "archive-download-report" or "archive-filename"))
            return string.Empty;
        return ArchiveWarpResolver.DisplayIdentity(job.ArchiveWarpName);
    }

    private static string RenderDisplayName(IngestionJob job, ServerLocation location)
    {
        var name = !string.IsNullOrWhiteSpace(location.Name) ? location.Name :
            ArchiveDisplayName(job) is { Length: > 0 } archiveName ? archiveName : job.Name;
        return ArchiveWarpResolver.IsSinglePlayerConcept(job.ArchiveWarpName) &&
            !ArchiveWarpResolver.IsSinglePlayerConcept(name)
                ? $"{name} — Singleplayer Concept"
                : name;
    }

    private static string? RenderDescription(IngestionJob job) =>
        ArchiveWarpResolver.IsSinglePlayerConcept(job.ArchiveWarpName)
            ? "Preserved single-player concept build. This is an offline creative concept, not a snapshot of the live 2b2t server."
            : null;

    private static bool IsArchiveCollector(IngestionJob job) =>
        job.Source.StartsWith("The Archive automated sync", StringComparison.OrdinalIgnoreCase);

    private static string RenderSource(IngestionJob job) =>
        IsArchiveCollector(job)
            ? "archive-collector"
            : job.RequestedByUserId.HasValue ? "manual-upload" : "wdl-ingestion";

    /// <summary>Auto-links a prepared render to a confident location match, or parks it for manual matching.</summary>
    private async Task TryAutoMatchAsync(IngestionJob row, IngestionWorldInspection inspection)
    {
        var dimensionIndex = DimensionIndex(row.Dimension);
        var bounds = inspection.Dimensions.FirstOrDefault(dimension => dimension.Key == row.Dimension);
        if (dimensionIndex is null || bounds is null)
        {
            row.ExistingLocationId = null;
            row.MatchResolved = 0;
            row.MatchDecision = "review";
            row.Status = "needs-match";
            row.Stage = "match";
            row.Message = "Could not auto-match; please match this render to a location manually.";
            row.MatchSuggestionsJson = null;
            return;
        }

        var (centerX, centerZ) = LocationMatcher.Centroid(bounds.MinX, bounds.MinZ, bounds.MaxXExclusive, bounds.MaxZExclusive);
        var footprint = new LocationRenderFootprint(bounds.MinX, bounds.MinZ, bounds.MaxXExclusive, bounds.MaxZExclusive);
        await ApplyMatchAsync(row, centerX, centerZ, inspection.ArchiveEvidence, WarpCandidate(row), footprint, true);
    }

    /// <summary>Runs the location matcher on a render centroid, auto-attaching a confident match or parking for manual review.</summary>
    private async Task ApplyMatchAsync(
        IngestionJob row,
        int centerX,
        int centerZ,
        ArchiveWdlEvidence? archiveEvidence = null,
        ArchiveWarpCandidate? archiveWarp = null,
        LocationRenderFootprint? incomingFootprint = null,
        bool parkIfUnresolved = true)
    {
        var dimensionIndex = DimensionIndex(row.Dimension);
        if (dimensionIndex is null)
        {
            row.ExistingLocationId = null;
            row.MatchResolved = 0;
            row.MatchDecision = "review";
            row.Status = "needs-match";
            row.Stage = "match";
            row.Message = "Could not auto-match; please match this render to a location manually.";
            row.MatchSuggestionsJson = null;
            return;
        }

        // The Archive command is globally identifying and outranks coordinates. Resolve it directly from
        // the complete warp table so a moved WDL still attaches, while a dimension/ownership contradiction
        // parks before any GPU work rather than disappearing from the same-dimension candidate query.
        if (archiveWarp?.IsTrusted == true)
        {
            var normalizedWarp = ArchiveWarpResolver.Normalize(archiveWarp.Name);
            var exactWarps = (await _context.Warps.ToListAsync())
                .Where(warp => ArchiveWarpResolver.Normalize(warp.Name) == normalizedWarp)
                .ToList();
            var exactWarpLocationIds = exactWarps
                .Select(warp => warp.LocationRowid).Where(id => id.HasValue).Select(id => id!.Value)
                .Distinct().ToList();
            if (exactWarpLocationIds.Count == 1)
            {
                var exactLocation = await _context.Locations.SingleOrDefaultAsync(
                    location => location.Rowid == exactWarpLocationIds[0]);
                var iterationConflict = exactLocation is not null &&
                    ArchiveWarpResolver.HasIterationConflict(archiveWarp.Name, exactLocation.Name);
                var coordinateIdentityConflict = exactLocation is not null &&
                    TrustedWarpCoordinatesContradictOwner(row, dimensionIndex.Value, archiveWarp, exactLocation);
                if (exactLocation?.Dimension == LocationDimensionFor(dimensionIndex.Value) &&
                    !iterationConflict && !coordinateIdentityConflict)
                {
                    row.ExistingLocationId = exactLocation.Rowid;
                    row.MatchResolved = 1;
                    row.MatchDecision = "existing";
                    row.MatchConfidence = 1;
                    row.MatchReason = $"Exact existing Archive /warp '{archiveWarp.Name}'.";
                    row.Message = $"Auto-matched to {exactLocation.Name} by exact existing Archive warp.";
                    row.MatchSuggestionsJson = null;
                    var exactWarpIds = exactWarps.Select(warp => warp.Id).ToList();
                    var linkedRender = await _context.Renders
                        .Where(render => render.ArchiveWarpId.HasValue && exactWarpIds.Contains(render.ArchiveWarpId.Value))
                        .OrderByDescending(render => render.Id)
                        .FirstOrDefaultAsync();
                    if (linkedRender is not null)
                    {
                        // One Archive warp identifies one WDL/render. A fuller recapture of that same warp
                        // replaces the previous pyramid instead of creating a duplicate historical card.
                        row.RenderId = linkedRender.Id;
                        row.RerenderRequested = 1;
                    }
                    return;
                }

                if (exactLocation is not null && (iterationConflict || coordinateIdentityConflict))
                {
                    // Historical imports sometimes attached a numbered warp iteration or a differently named
                    // Archive site to an unrelated location. A trusted landing coordinate thousands of blocks
                    // away plus a different canonical identity is stronger evidence than that legacy link.
                    row.ExistingLocationId = null;
                    row.MatchResolved = 1;
                    row.MatchDecision = "new-rehome";
                    row.MatchConfidence = 1;
                    row.MatchReason = iterationConflict
                        ? $"Exact Archive warp iteration conflicts with legacy owner '{exactLocation.Name}'."
                        : $"Exact Archive warp identity and trusted landing coordinate contradict legacy owner '{exactLocation.Name}'.";
                    row.Message = "Confident separate Archive location; rendering will create it and re-home the unique warp.";
                    row.MatchSuggestionsJson = null;
                    return;
                }

                row.Status = "needs-match";
                row.Stage = "match";
                row.ExistingLocationId = null;
                row.MatchResolved = 0;
                row.MatchDecision = "review";
                row.MatchConfidence = 1;
                row.MatchReason = "The exact Archive warp exists, but its location dimension conflicts with this render dimension.";
                row.Message = "Exact Archive warp found in another dimension; manual review required before rendering.";
                row.MatchSuggestionsJson = null;
                return;
            }
            if (exactWarpLocationIds.Count > 1)
            {
                row.Status = "needs-match";
                row.Stage = "match";
                row.ExistingLocationId = null;
                row.MatchResolved = 0;
                row.MatchDecision = "review";
                row.MatchConfidence = 1;
                row.MatchReason = "The exact Archive warp currently points to multiple Atlas locations.";
                row.Message = "Duplicate Archive warp ownership must be reviewed before rendering.";
                row.MatchSuggestionsJson = null;
                return;
            }
        }

        var candidates = await LoadMatchCandidatesAsync(dimensionIndex.Value);
        var result = LocationMatcher.Match(
            dimensionIndex.Value, centerX, centerZ, row.Name, candidates, archiveEvidence: archiveEvidence,
            archiveWarp: archiveWarp, incomingFootprint: incomingFootprint);
        IngestionAiMatch? aiMatch = null;
        if (!result.IsConfident && archiveWarp?.IsTrusted == true && _matchAi is not null)
        {
            aiMatch = await _matchAi.EvaluateAsync(row.Name, row.Dimension, archiveWarp, candidates,
                result.Suggestions, HttpContext.RequestAborted);
            if (aiMatch is not null && (aiMatch.Confidence < 0.95 ||
                result.Suggestions.First(value => value.LocationId == aiMatch.LocationId).Confidence < 0.55))
                aiMatch = null;
        }
        var chosenLocationId = result.AutoAttachLocationId ?? aiMatch?.LocationId;
        if (chosenLocationId is int matchedLocationId)
        {
            row.ExistingLocationId = matchedLocationId;
            row.MatchResolved = 1;
            row.MatchDecision = "existing";
            row.MatchConfidence = aiMatch?.Confidence ?? result.Confidence;
            row.MatchReason = aiMatch is null
                ? result.AutoAttachReason ?? "Existing-location evidence agreed."
                : $"Qwen second opinion: {aiMatch.Reason}";
            row.Message = $"Auto-matched to an existing location: {row.MatchReason}.";
            row.MatchSuggestionsJson = null;
        }
        else if (result.CreateNewLocation)
        {
            row.ExistingLocationId = null;
            row.MatchResolved = 1;
            row.MatchDecision = "new";
            row.MatchConfidence = result.Confidence;
            row.MatchReason = "Trusted Archive warp has no plausible existing Atlas location.";
            row.Message = "Confident new location; rendering will create it and register its Archive warp.";
            row.MatchSuggestionsJson = null;
        }
        else
        {
            // An inconclusive second pass must revoke every preliminary/stale
            // destination. Completion is fail-closed and cannot inherit a location
            // selected by an earlier request or a racing upload-time matcher.
            row.ExistingLocationId = null;
            row.MatchResolved = 0;
            row.MatchDecision = "review";
            row.MatchConfidence = result.Confidence;
            row.MatchReason = result.Suggestions.FirstOrDefault()?.Reason ??
                "No trustworthy automatic existing/new decision was available.";
            if (parkIfUnresolved)
            {
                row.Status = "needs-match";
                row.Stage = "match";
                row.Message = "Awaiting manual location match and Archive warp review.";
            }
            else
            {
                row.Message = "Preliminary match inconclusive; queued for authoritative worker inspection.";
            }
            row.MatchSuggestionsJson = JsonSerializer.Serialize(result.Suggestions);
        }
    }

    private static bool TrustedWarpCoordinatesContradictOwner(
        IngestionJob row,
        int dimensionIndex,
        ArchiveWarpCandidate archiveWarp,
        ServerLocation location)
    {
        var position = TrustedArchiveLocationPosition(row, dimensionIndex);
        if (position is null) return false;
        var incomingIdentity = ArchiveWarpResolver.CanonicalLocationIdentity(archiveWarp.Name);
        var ownerIdentity = ArchiveWarpResolver.CanonicalLocationIdentity(location.Name);
        if (incomingIdentity.Length == 0 || ownerIdentity.Length == 0 || incomingIdentity == ownerIdentity)
            return false;
        var dx = (long)position.Value.X - location.X;
        var dz = (long)position.Value.Z - location.Z;
        var distance = Math.Sqrt(dx * (double)dx + dz * (double)dz);
        return distance > LocationMatcher.CandidateRadius(dimensionIndex);
    }

    private static ArchiveWarpCandidate? WarpCandidate(IngestionJob row) =>
        string.IsNullOrWhiteSpace(row.ArchiveWarpName) ? null : new ArchiveWarpCandidate(
            row.ArchiveWarpName,
            row.ArchiveWarpSource ?? "stored",
            row.ArchiveWarpSource is "existing-archive-sha" or "operator" ? 1 :
                row.ArchiveWarpSource is "archive-download-report" ? 0.99 : 0.94,
            row.ArchiveWarpSource is "existing-archive-sha" or "operator" or
                "archive-download-report" or "archive-filename");

    /// <summary>
    /// Revalidates a trusted "new" decision at the final registration boundary. Multiple collector
    /// workers can decide that dated captures of the same previously-unknown Archive warp are new
    /// before the first render commits; this check makes the later completion converge on the newly
    /// created location instead of creating a duplicate.
    /// </summary>
    private async Task<(ServerLocation? Location, ServerLocationRender? ExistingRender, string? Error)> FindLateIdentityMatchAsync(
        IngestionJob row, int renderDimension)
    {
        if (row.MatchDecision is not ("new" or "manual-new" or "new-rehome")) return (null, null, null);
        var archiveWarp = WarpCandidate(row);
        if (archiveWarp?.IsTrusted != true) return (null, null, null);
        var identity = ArchiveWarpResolver.CanonicalLocationIdentity(archiveWarp.Name);
        if (identity.Length == 0) return (null, null, null);

        var candidates = await LoadMatchCandidatesAsync(renderDimension);
        if (row.MatchDecision == "new-rehome")
        {
            // A new-rehome decision means the exact warp was historically owned by the wrong numbered
            // iteration. While this job rendered, a parallel job may have already created the correct
            // location and moved that unique warp. Converge only when the exact warp now has one owner
            // and that owner's name no longer has the iteration conflict. If the old conflict remains,
            // preserve the original create-and-rehome decision.
            var normalizedWarp = ArchiveWarpResolver.Normalize(archiveWarp.Name);
            var exactWarps = (await _context.Warps.ToListAsync())
                .Where(warp => ArchiveWarpResolver.Normalize(warp.Name) == normalizedWarp)
                .ToList();
            var exactOwnerIds = exactWarps
                .Where(warp => warp.LocationRowid.HasValue)
                .Select(warp => warp.LocationRowid!.Value)
                .Distinct()
                .ToList();
            if (exactOwnerIds.Count > 1)
                return (null, null, $"Archive warp '{archiveWarp.Name}' currently belongs to multiple Atlas locations; repair/review is required.");
            if (exactOwnerIds.Count == 0) return (null, null, null);

            var exactOwner = candidates.Single(candidate => candidate.LocationId == exactOwnerIds[0]);
            var exactWarpIds = exactWarps.Select(warp => warp.Id).ToList();
            var establishedRender = await _context.Renders
                .Where(render => render.LocationRowid == exactOwner.LocationId &&
                    render.ArchiveWarpId.HasValue && exactWarpIds.Contains(render.ArchiveWarpId.Value))
                .OrderByDescending(render => render.Id)
                .FirstOrDefaultAsync();
            if (ArchiveWarpResolver.HasIterationConflict(archiveWarp.Name, exactOwner.Name))
            {
                // A render explicitly linked to this exact warp proves that a previous parallel
                // completion already established the intended owner. Without that durable link,
                // this may still be the legacy misownership that new-rehome is meant to repair.
                if (establishedRender is null) return (null, null, null);
            }

            var convergedLocation = await _context.Locations.SingleAsync(
                candidate => candidate.Rowid == exactOwner.LocationId);
            ApplyLateIdentityMatch(row, convergedLocation);
            return (convergedLocation, establishedRender, null);
        }

        var matchingIds = candidates.Where(candidate =>
                ArchiveWarpResolver.CanonicalLocationIdentity(candidate.Name) == identity ||
                candidate.WarpNames?.Any(warp =>
                    ArchiveWarpResolver.CanonicalLocationIdentity(warp) == identity) == true)
            .Select(candidate => candidate.LocationId)
            .Distinct()
            .ToList();
        if (matchingIds.Count > 1)
            return (null, null, $"Archive identity '{ArchiveWarpResolver.DisplayIdentity(archiveWarp.Name)}' already resolves to multiple Atlas locations; repair/review is required.");
        if (matchingIds.Count == 0) return (null, null, null);

        var location = await _context.Locations.SingleAsync(candidate => candidate.Rowid == matchingIds[0]);
        var normalized = ArchiveWarpResolver.Normalize(archiveWarp.Name);
        var exactWarpIdsForLocation = (await _context.Warps
                .Where(warp => warp.LocationRowid == location.Rowid)
                .ToListAsync())
            .Where(warp => ArchiveWarpResolver.Normalize(warp.Name) == normalized)
            .Select(warp => warp.Id)
            .ToList();
        var priorRender = exactWarpIdsForLocation.Count == 0
            ? null
            : await _context.Renders
                .Where(render => render.LocationRowid == location.Rowid && render.ArchiveWarpId.HasValue &&
                    exactWarpIdsForLocation.Contains(render.ArchiveWarpId.Value))
                .OrderByDescending(render => render.Id)
                .FirstOrDefaultAsync();
        ApplyLateIdentityMatch(row, location);
        return (location, priorRender, null);
    }

    private static void ApplyLateIdentityMatch(IngestionJob row, ServerLocation location)
    {
        row.ExistingLocationId = location.Rowid;
        row.MatchResolved = 1;
        row.MatchDecision = "existing-late";
        row.MatchConfidence = 1;
        row.MatchReason = $"Completion-time Archive identity matched existing location '{location.Name}'.";
        row.Message = $"Registered with {location.Name}; a parallel dated capture created the location first.";
        row.MatchSuggestionsJson = null;
    }

    /// <summary>
    /// Loads the location candidates a render of the given dimension should match. There are no separate
    /// nether locations — an overworld location simply appears on the nether map at its /8 position — so a
    /// nether render is matched against overworld locations projected into nether coordinates.
    /// </summary>
    private async Task<List<LocationMatchCandidate>> LoadMatchCandidatesAsync(int renderDimension)
    {
        var locationDimension = LocationDimensionFor(renderDimension);
        var locations = await _context.Locations
            .Where(location => location.Dimension == locationDimension)
            .Select(location => new { location.Rowid, location.Name, location.X, location.Z })
            .ToListAsync();
        var locationIds = locations.Select(location => location.Rowid).ToList();
        var warpsByLocation = (await _context.Warps
                .Where(warp => warp.LocationRowid.HasValue && locationIds.Contains(warp.LocationRowid.Value))
                .Select(warp => new { LocationRowid = warp.LocationRowid!.Value, warp.Name })
                .ToListAsync())
            .GroupBy(warp => warp.LocationRowid)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group.Select(warp => warp.Name).ToList());
        var footprintsByLocation = (await _context.Renders
                .Where(render => locationIds.Contains(render.LocationRowid) && render.Dimension == renderDimension &&
                    render.MinX.HasValue && render.MinZ.HasValue && render.MaxXExclusive.HasValue && render.MaxZExclusive.HasValue)
                .Select(render => new
                {
                    render.LocationRowid, MinX = render.MinX!.Value, MinZ = render.MinZ!.Value,
                    MaxX = render.MaxXExclusive!.Value, MaxZ = render.MaxZExclusive!.Value,
                }).ToListAsync())
            .GroupBy(render => render.LocationRowid)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<LocationRenderFootprint>)group
                .Select(render => new LocationRenderFootprint(render.MinX, render.MinZ, render.MaxX, render.MaxZ)).ToList());
        return renderDimension == 1
            ? locations.Select(location => new LocationMatchCandidate(
                location.Rowid, location.Name, ProjectToNether(location.X), ProjectToNether(location.Z), renderDimension,
                warpsByLocation.GetValueOrDefault(location.Rowid), footprintsByLocation.GetValueOrDefault(location.Rowid))).ToList()
            : locations.Select(location => new LocationMatchCandidate(
                location.Rowid, location.Name, location.X, location.Z, renderDimension,
                warpsByLocation.GetValueOrDefault(location.Rowid), footprintsByLocation.GetValueOrDefault(location.Rowid))).ToList();
    }

    /// <summary>Maps a render dimension to the location dimension it attaches to (nether renders link to overworld locations).</summary>
    private static int LocationDimensionFor(int renderDimension) => renderDimension == 1 ? 0 : renderDimension;

    private static int ProjectToNether(int overworldCoordinate) => (int)Math.Floor(overworldCoordinate / 8.0);

    private bool HasValidWorkerKey()
    {
        if (_workerKeyHash is null || !Request.Headers.TryGetValue(WorkerKeyHeader, out var supplied))
            return false;
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied.ToString()));
        return CryptographicOperations.FixedTimeEquals(_workerKeyHash, suppliedHash);
    }

    private static string CreateClaimToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string HashClaimToken(string claimToken) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(claimToken)));

    private static bool HasValidClaimToken(IngestionJob row, string claimToken)
    {
        if (row.ClaimTokenSha256 is null) return false;
        var supplied = SHA256.HashData(Encoding.UTF8.GetBytes(claimToken));
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(row.ClaimTokenSha256), supplied);
    }

    private int? CurrentUserId() =>
        int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ? userId : null;

    private static IngestionJobDto MapToDto(
        IngestionJob row,
        string? claimToken = null,
        int? linkedLocationId = null) => new()
    {
        Id = row.PublicId,
        OriginalFileName = row.OriginalFileName,
        IntakeFileName = row.IntakeFileName,
        Slug = row.Slug,
        Name = row.Name,
        WorldDownloadDate = row.WorldDownloadDate,
        Source = row.Source,
        Scale = row.Scale,
        Dimension = row.Dimension,
        WorldRoot = row.WorldRoot,
        ArchiveWarpName = row.ArchiveWarpName,
        ArchiveWarpX = row.ArchiveWarpX,
        ArchiveWarpY = row.ArchiveWarpY,
        ArchiveWarpZ = row.ArchiveWarpZ,
        DayNight = row.DayNight == 1,
        ExistingLocationId = row.ExistingLocationId ?? linkedLocationId,
        Status = row.Status,
        Stage = row.Stage,
        Message = row.Message,
        ProgressPercent = row.Status == "completed" ? 100 : row.ProgressPercent,
        EtaSeconds = row.EtaSeconds,
        ArchiveSha256 = row.ArchiveSha256,
        ClaimToken = claimToken,
        AttemptCount = row.AttemptCount,
        RerenderRequested = row.RerenderRequested == 1,
        RenderTopY = row.RenderTopY,
        RequestedUtc = ParseDate(row.RequestedUtc) ?? DateTime.MinValue,
        ClaimedUtc = ParseDate(row.ClaimedUtc),
        LeaseExpiresUtc = ParseDate(row.LeaseExpiresUtc),
        UpdatedUtc = ParseDate(row.UpdatedUtc),
        CompletedUtc = ParseDate(row.CompletedUtc),
        Inspection = ParseInspection(row.InspectionJson),
        ArchiveEvidence = ParseArchiveEvidence(row.ArchiveEvidenceJson),
        MatchSuggestions = ParseSuggestions(row.MatchSuggestionsJson),
        ArchiveWarpSource = row.ArchiveWarpSource,
        WarpId = row.WarpId,
        MatchDecision = row.MatchDecision,
        MatchConfidence = row.MatchConfidence,
        MatchReason = row.MatchReason,
    };

    private static IngestionWorldInspection? ParseInspection(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return JsonSerializer.Deserialize<IngestionWorldInspection>(value); }
        catch (JsonException) { return null; }
    }

    private static ArchiveWdlEvidence? ParseArchiveEvidence(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return JsonSerializer.Deserialize<ArchiveWdlEvidence>(value); }
        catch (JsonException) { return null; }
    }

    private static IReadOnlyList<LocationMatchSuggestion>? ParseSuggestions(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return JsonSerializer.Deserialize<List<LocationMatchSuggestion>>(value); }
        catch (JsonException) { return null; }
    }

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParse(value, out var parsed) ? parsed : null;
}
