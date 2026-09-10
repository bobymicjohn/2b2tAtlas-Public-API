using System.Text.Json;
using Atlas;
using Atlas.Locations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using _2b2tAtlas.Server.Models;
using _2b2tAtlas.Server.Services;
using ServerRender = _2b2tAtlas.Server.Models.Render;
using ServerWarp = _2b2tAtlas.Server.Models.Warp;

namespace _2b2tAtlas.Server.Controllers;

/// <summary>Public, read-only access to immutable bounded WDLs produced by the Archive collector.</summary>
[ApiController]
[AllowAnonymous]
public sealed class WorldDownloadsController : ControllerBase
{
    private const string Scope = "bounded-footprint";
    private const string PreservedRenderScope = "preserved-render-source";
    private const string Warning = "This is a bounded historical snapshot, not a complete 2b2t world. " +
        "Chunks outside the retained footprint are absent and Minecraft may generate new terrain if opened normally. " +
        "Use a copy and prevent chunk generation when historical fidelity matters.";
    private const string ConceptWarning = "This is a preserved single-player concept build, not a snapshot of the live 2b2t server. " +
        "It is also a bounded partial world: chunks outside the retained footprint are absent and Minecraft may generate new terrain. " +
        "Use a copy and prevent chunk generation when historical fidelity matters.";
    private const string PreservedRenderWarning = "This is the preserved source save used to produce an Atlas render, " +
        "not a complete copy of 2b2t. Its retained area depends on the original community or archival WDL and chunks " +
        "outside that save may generate as new terrain if opened normally. Use a copy and prevent chunk generation " +
        "when historical fidelity matters.";
    private readonly AtlasContext _context;
    private readonly WdlArchiveOptions _options;
    private static readonly JsonSerializerOptions InspectionJson = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Initializes the public bounded-world-download controller.</summary>
    public WorldDownloadsController(AtlasContext context, IOptions<WdlArchiveOptions> options)
    {
        _context = context;
        _options = options.Value;
    }

    /// <summary>Returns metadata for one downloadable collector WDL without exposing its host path.</summary>
    [HttpGet("api/warps/{id:int}/world-download")]
    [ResponseCache(Duration = 300, Location = ResponseCacheLocation.Any, VaryByHeader = "Origin")]
    public async Task<ActionResult<WorldDownloadRecord>> GetMetadata(int id, CancellationToken cancellationToken)
    {
        var resolved = await ResolveAsync(id, cancellationToken);
        if (resolved.Status is not null) return StatusCode(resolved.Status.Value, new { message = resolved.Message });
        return Ok(await CreateRecordAsync(resolved.Warp!, resolved.Path!, cancellationToken));
    }

    /// <summary>
    /// Streams the immutable exact-footprint Minecraft Java save associated with an Archive warp.
    /// Supports byte ranges and a digest ETag so clients can resume and caches can revalidate safely.
    /// </summary>
    [HttpGet("api/warps/{id:int}/world-download.zip")]
    [EnableRateLimiting("wdl-download")]
    public async Task<IActionResult> Download(int id, CancellationToken cancellationToken)
    {
        var resolved = await ResolveAsync(id, cancellationToken);
        if (resolved.Status is not null) return StatusCode(resolved.Status.Value, new { message = resolved.Message });

        var sha = resolved.Warp!.ArchiveSha256!.ToLowerInvariant();
        if (ValidateDownloadVersion(sha) is { } versionError) return versionError;
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["X-Atlas-World-Scope"] = Scope;
        Response.Headers["X-Atlas-Complete-World"] = "false";
        Response.Headers["X-Atlas-Singleplayer-Concept"] =
            ArchiveWarpResolver.IsSinglePlayerConcept(resolved.Warp.Name) ? "true" : "false";

        return new PhysicalFileResult(resolved.Path!, "application/zip")
        {
            FileDownloadName = DownloadFileNames.WorldDownload(
                ConceptDownloadName(resolved.Warp.LocationRow?.Name, resolved.Warp.Name), id),
            EnableRangeProcessing = true,
            EntityTag = new EntityTagHeaderValue($"\"{sha}\""),
        };
    }

    /// <summary>Returns metadata for a verified preserved source WDL used by a pre-Archive render.</summary>
    [HttpGet("api/renders/{id:int}/world-download")]
    [ResponseCache(Duration = 300, Location = ResponseCacheLocation.Any, VaryByHeader = "Origin")]
    public async Task<ActionResult<WorldDownloadRecord>> GetRenderMetadata(int id, CancellationToken cancellationToken)
    {
        var resolved = await ResolveRenderAsync(id, cancellationToken);
        if (resolved.Status is not null) return StatusCode(resolved.Status.Value, new { message = resolved.Message });
        return Ok(CreateRenderRecord(resolved.Render!, resolved.Job!, resolved.Path!));
    }

    /// <summary>Streams the immutable source WDL associated with a verified public pre-Archive render.</summary>
    [HttpGet("api/renders/{id:int}/world-download.zip")]
    [EnableRateLimiting("wdl-download")]
    public async Task<IActionResult> DownloadRenderSource(int id, CancellationToken cancellationToken)
    {
        var resolved = await ResolveRenderAsync(id, cancellationToken);
        if (resolved.Status is not null) return StatusCode(resolved.Status.Value, new { message = resolved.Message });

        var sha = resolved.Job!.ArchiveSha256!.ToLowerInvariant();
        if (ValidateDownloadVersion(sha) is { } versionError) return versionError;
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["X-Atlas-World-Scope"] = PreservedRenderScope;
        Response.Headers["X-Atlas-Complete-World"] = "false";

        return new PhysicalFileResult(resolved.Path!, "application/zip")
        {
            FileDownloadName = DownloadFileNames.RenderWorldDownload(resolved.Render!.LocationRow?.Name, id),
            EnableRangeProcessing = true,
            EntityTag = new EntityTagHeaderValue($"\"{sha}\""),
        };
    }

    private IActionResult? ValidateDownloadVersion(string sha)
    {
        var requested = Request.Query["sha256"].ToString();
        if (!string.IsNullOrEmpty(requested) &&
            !string.Equals(requested, sha, StringComparison.OrdinalIgnoreCase))
        {
            Response.Headers.CacheControl = "no-store";
            return Conflict(new { message = "This world download has been replaced. Refresh its metadata for the current download link." });
        }

        // IDs point to the latest reviewed source; only digest-qualified URLs are immutable.
        Response.Headers.CacheControl = string.IsNullOrEmpty(requested)
            ? "public,max-age=0,must-revalidate"
            : "public,max-age=31536000,immutable";
        return null;
    }

    private async Task<(ServerWarp? Warp, string? Path, int? Status, string? Message)> ResolveAsync(
        int id, CancellationToken cancellationToken)
    {
        var warp = await _context.Warps.AsNoTracking()
            .Include(value => value.LocationRow)
            .Include(value => value.Render)
            .SingleOrDefaultAsync(value => value.Id == id, cancellationToken);
        if (warp?.LocationRow is null || !IsPublicCollectorWdl(warp))
            return (null, null, StatusCodes.Status404NotFound, "No public world download is available for this warp.");

        string path;
        try { path = WdlArchiveStore.ObjectPath(_options.Root, warp.ArchiveSha256!); }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IOException)
        {
            return (null, null, StatusCodes.Status404NotFound, "No public world download is available for this warp.");
        }
        if (!System.IO.File.Exists(path))
            return (null, null, StatusCodes.Status503ServiceUnavailable, "The archived world download is temporarily unavailable.");
        return (warp, path, null, null);
    }

    private async Task<(ServerRender? Render, IngestionJob? Job, string? Path, int? Status, string? Message)> ResolveRenderAsync(
        int id, CancellationToken cancellationToken)
    {
        var render = await _context.Renders.AsNoTracking()
            .Include(value => value.LocationRow)
            .SingleOrDefaultAsync(value => value.Id == id && value.IsPublic == 1, cancellationToken);
        if (render?.LocationRow is null || render.ArchiveWarpId is not null)
            return (null, null, null, StatusCodes.Status404NotFound, "No preserved source world download is available for this render.");

        var jobs = await _context.IngestionJobs.AsNoTracking()
            .Where(value => value.RenderId == id && value.Status == "completed" && value.WarpId == null && value.ArchiveSha256 != null)
            .OrderByDescending(value => value.Id)
            .ToListAsync(cancellationToken);
        var job = jobs.FirstOrDefault(IsPublicRenderSourceJob);
        if (job is null)
            return (null, null, null, StatusCodes.Status404NotFound, "No preserved source world download is available for this render.");

        string path;
        try { path = WdlArchiveStore.ObjectPath(_options.Root, job.ArchiveSha256!); }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IOException)
        {
            return (null, null, null, StatusCodes.Status404NotFound, "No preserved source world download is available for this render.");
        }
        if (!System.IO.File.Exists(path))
            return (render, job, null, StatusCodes.Status503ServiceUnavailable, "The archived world download is temporarily unavailable.");
        return (render, job, path, null, null);
    }

    private async Task<WorldDownloadRecord> CreateRecordAsync(
        ServerWarp warp, string path, CancellationToken cancellationToken)
    {
        var job = await _context.IngestionJobs.AsNoTracking()
            .Where(value => value.WarpId == warp.Id && value.ArchiveSha256 == warp.ArchiveSha256)
            .OrderByDescending(value => value.Id)
            .Select(value => new { value.InspectionJson })
            .FirstOrDefaultAsync(cancellationToken);

        var dimensionNumber = warp.Render?.Dimension ?? warp.LocationRow.Dimension;
        var dimension = DimensionName(dimensionNumber);
        IngestionDimensionInspection? inspection = null;
        if (!string.IsNullOrWhiteSpace(job?.InspectionJson))
        {
            try
            {
                inspection = JsonSerializer.Deserialize<IngestionWorldInspection>(job.InspectionJson, InspectionJson)?
                    .Dimensions.FirstOrDefault(value => value.Key.Equals(dimension.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase));
            }
            catch (JsonException) { }
        }

        var bounds = CreateBounds(warp.Render, inspection);
        return new WorldDownloadRecord
        {
            WarpId = warp.Id,
            WarpName = warp.Name,
            RenderId = warp.Render?.Id,
            RenderName = warp.Render?.Name,
            LocationId = warp.LocationRow.Rowid,
            LocationName = warp.LocationRow.Name ?? string.Empty,
            LocationUrl = PublicAtlasUrls.Location(warp.LocationRow.Rowid),
            LocationApiUrl = PublicAtlasUrls.LocationApi(warp.LocationRow.Rowid),
            MetadataUrl = PublicAtlasUrls.WorldDownloadMetadata(warp.Id),
            DownloadUrl = PublicAtlasUrls.WorldDownload(
                warp.Id, ConceptDownloadName(warp.LocationRow.Name, warp.Name), warp.ArchiveSha256),
            FileName = DownloadFileNames.WorldDownload(
                ConceptDownloadName(warp.LocationRow.Name, warp.Name), warp.Id),
            ByteLength = new FileInfo(path).Length,
            Sha256 = warp.ArchiveSha256!.ToLowerInvariant(),
            IsCompleteWorld = false,
            Warning = ArchiveWarpResolver.IsSinglePlayerConcept(warp.Name) ? ConceptWarning : Warning,
            Dimension = dimension,
            ChunkCount = inspection?.ChunkCount,
            Bounds = bounds,
            WorldDownloadDate = warp.WorldDownloadDate,
            Source = warp.Source,
            Attribution = ArchiveWarpResolver.IsSinglePlayerConcept(warp.Name)
                ? "Single-player concept build preserved by The Archive and 2b2t Atlas."
                : "Captured from The Archive museum server by 2b2t Atlas.",
        };
    }

    private WorldDownloadRecord CreateRenderRecord(ServerRender render, IngestionJob job, string path)
    {
        var dimension = DimensionName(render.Dimension);
        IngestionDimensionInspection? inspection = null;
        if (!string.IsNullOrWhiteSpace(job.InspectionJson))
        {
            try
            {
                inspection = JsonSerializer.Deserialize<IngestionWorldInspection>(job.InspectionJson, InspectionJson)?
                    .Dimensions.FirstOrDefault(value => value.Key.Equals(dimension.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase));
            }
            catch (JsonException) { }
        }

        return new WorldDownloadRecord
        {
            RenderId = render.Id,
            RenderName = render.Name,
            LocationId = render.LocationRow.Rowid,
            LocationName = render.LocationRow.Name ?? string.Empty,
            LocationUrl = PublicAtlasUrls.Location(render.LocationRow.Rowid),
            LocationApiUrl = PublicAtlasUrls.LocationApi(render.LocationRow.Rowid),
            MetadataUrl = PublicAtlasUrls.RenderWorldDownloadMetadata(render.Id),
            DownloadUrl = PublicAtlasUrls.RenderWorldDownload(render.Id, render.LocationRow.Name, job.ArchiveSha256),
            FileName = DownloadFileNames.RenderWorldDownload(render.LocationRow.Name, render.Id),
            ByteLength = new FileInfo(path).Length,
            Sha256 = job.ArchiveSha256!.ToLowerInvariant(),
            CaptureType = PreservedRenderScope,
            IsCompleteWorld = false,
            Warning = PreservedRenderWarning,
            Dimension = dimension,
            ChunkCount = inspection?.ChunkCount,
            Bounds = CreateBounds(render, inspection),
            WorldDownloadDate = render.WorldDownloadDate ?? job.WorldDownloadDate,
            Source = job.Source,
            Attribution = "Preserved by 2b2t Atlas from the credited community or archival source.",
        };
    }

    private static WorldDownloadBounds? CreateBounds(ServerRender? render, IngestionDimensionInspection? inspection)
    {
        if (render?.MinX is int minX && render.MinZ is int minZ &&
            render.MaxXExclusive is int maxX && render.MaxZExclusive is int maxZ)
            return new WorldDownloadBounds { MinX = minX, MinZ = minZ, MaxXExclusive = maxX, MaxZExclusive = maxZ };
        return inspection is null ? null : new WorldDownloadBounds
        {
            MinX = inspection.MinX,
            MinZ = inspection.MinZ,
            MaxXExclusive = inspection.MaxXExclusive,
            MaxZExclusive = inspection.MaxZExclusive,
        };
    }

    private static bool IsPublicCollectorWdl(ServerWarp warp) =>
        warp.ArchiveSha256 is { Length: 64 } sha && sha.All(Uri.IsHexDigit) &&
        warp.Source?.StartsWith("The Archive automated sync", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsPublicRenderSourceJob(IngestionJob job) =>
        job.RenderId.HasValue && job.WarpId is null &&
        job.Status.Equals("completed", StringComparison.OrdinalIgnoreCase) &&
        job.ArchiveSha256 is { Length: 64 } sha && sha.All(Uri.IsHexDigit) &&
        !string.IsNullOrWhiteSpace(job.Source);

    private static string? ConceptDownloadName(string? locationName, string? warpName) =>
        ArchiveWarpResolver.IsSinglePlayerConcept(warpName)
            ? $"{locationName ?? "2b2t-location"} singleplayer concept"
            : locationName;

    private static string DimensionName(int dimension) => dimension switch
    {
        1 => "Nether",
        2 => "End",
        _ => "Overworld",
    };
}
