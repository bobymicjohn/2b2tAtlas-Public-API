using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Atlas;
using _2b2tAtlas.Server.Models;
using _2b2tAtlas.Server.Services;
using ServerRender = _2b2tAtlas.Server.Models.Render;
using ServerLocation = _2b2tAtlas.Server.Models.Location;

namespace _2b2tAtlas.Server.Controllers;

/// <summary>
/// Per-location base renders (tile pyramids anchored at individual bases, produced by WDL ingestion). These
/// are distinct from the dimension-level primary layers in <see cref="MapRendersController"/>: there are many
/// of them, each tied to a location. All reads are public and cached so external map/tool builders can
/// enumerate every render, fetch one, or list the renders for a given location, with enough tile and
/// coordinate metadata to place and load the tiles directly.
/// </summary>
[ApiController]
public sealed class RendersController : ControllerBase
{
    private readonly AtlasContext _context;
    private readonly BlueMapCatalogService? _blueMap;

    /// <summary>Initializes the per-location render read API.</summary>
    /// <param name="context">The Atlas context containing renders and their owning locations.</param>
    /// <param name="blueMap">Validated interactive 3D derivative catalog, when configured.</param>
    public RendersController(AtlasContext context, BlueMapCatalogService? blueMap = null)
    {
        _context = context;
        _blueMap = blueMap;
    }

    /// <summary>
    /// GET /api/renders — every per-location render with its owning location, optionally filtered.
    /// </summary>
    /// <param name="locationId">When set, only renders for this location.</param>
    /// <param name="dimension">When set, only renders in this dimension (0 Overworld, 1 Nether, 2 End).</param>
    /// <param name="scale">When set, only renders with this scale label (case-insensitive, e.g. "256k").</param>
    /// <param name="limit">Maximum rows to return (default 500, clamped 1..1000).</param>
    /// <param name="offset">Rows to skip for paging (default 0).</param>
    /// <param name="cancellationToken">Token that cancels the request.</param>
    /// <returns>The matching renders, ordered by dimension then id.</returns>
    [HttpGet("api/renders")]
    [AllowAnonymous]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any, VaryByHeader = "Origin")]
    public async Task<ActionResult<IEnumerable<LocationRenderDto>>> GetRenders(
        [FromQuery] int? locationId,
        [FromQuery] int? dimension,
        [FromQuery] string? scale,
        [FromQuery] int? limit,
        [FromQuery] int? offset,
        CancellationToken cancellationToken)
    {
        var query =
            from render in _context.Renders.AsNoTracking()
            join loc in _context.Locations.AsNoTracking() on render.LocationRowid equals loc.Rowid
            where render.IsPublic == 1
            select new { render, loc };

        if (locationId is int lid)
            query = query.Where(x => x.loc.Rowid == lid);
        if (dimension is int dim)
            query = query.Where(x => x.render.Dimension == dim);
        if (!string.IsNullOrWhiteSpace(scale))
        {
            var normalized = scale.Trim().ToLowerInvariant();
            query = query.Where(x => x.render.Scale.ToLower() == normalized);
        }

        var take = Math.Clamp(limit ?? 500, 1, 1000);
        var skip = Math.Max(offset ?? 0, 0);
        var rows = await query
            .OrderBy(x => x.render.Dimension).ThenBy(x => x.render.Id)
            .Skip(skip).Take(take)
            .ToListAsync(cancellationToken);

        var warpIds = rows.Where(x => x.render.ArchiveWarpId.HasValue)
            .Select(x => x.render.ArchiveWarpId!.Value).Distinct().ToList();
        var warps = await _context.Warps.AsNoTracking()
            .Where(warp => warpIds.Contains(warp.Id))
            .ToDictionaryAsync(warp => warp.Id, cancellationToken);
        var legacyJobs = await GetLegacySourceJobsAsync(rows.Select(row => row.render.Id), cancellationToken);

        return Ok(rows.Select(x => MapToDto(
            x.render, x.loc,
            x.render.ArchiveWarpId is int warpId ? warps.GetValueOrDefault(warpId) : null,
            legacyJobs.GetValueOrDefault(x.render.Id))).ToList());
    }

    /// <summary>GET /api/renders/{id} — a single render with its owning location.</summary>
    /// <param name="id">The render id.</param>
    /// <param name="cancellationToken">Token that cancels the request.</param>
    /// <returns>The render, or 404 when it does not exist.</returns>
    [HttpGet("api/renders/{id:int}")]
    [AllowAnonymous]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any, VaryByHeader = "Origin")]
    public async Task<ActionResult<LocationRenderDto>> GetRender(int id, CancellationToken cancellationToken)
    {
        var row = await (
            from render in _context.Renders.AsNoTracking()
            join loc in _context.Locations.AsNoTracking() on render.LocationRowid equals loc.Rowid
            where render.Id == id && render.IsPublic == 1
            select new { render, loc }).SingleOrDefaultAsync(cancellationToken);

        if (row is null) return NotFound();
        var warp = row.render.ArchiveWarpId is int warpId
            ? await _context.Warps.AsNoTracking().SingleOrDefaultAsync(value => value.Id == warpId, cancellationToken)
            : null;
        var legacyJobs = await GetLegacySourceJobsAsync([row.render.Id], cancellationToken);
        return Ok(MapToDto(row.render, row.loc, warp, legacyJobs.GetValueOrDefault(row.render.Id)));
    }

    /// <summary>GET /api/locations/{locationId}/renders — the renders attached to a location.</summary>
    /// <param name="locationId">The owning location's row id.</param>
    /// <param name="cancellationToken">Token that cancels the request.</param>
    /// <returns>The location's renders (possibly empty), or 404 when the location does not exist.</returns>
    [HttpGet("api/locations/{locationId:int}/renders")]
    [AllowAnonymous]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any, VaryByHeader = "Origin")]
    public async Task<ActionResult<IEnumerable<LocationRenderDto>>> GetByLocation(int locationId, CancellationToken cancellationToken)
    {
        var location = await _context.Locations.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Rowid == locationId, cancellationToken);
        if (location is null)
            return NotFound();

        var renders = await _context.Renders.AsNoTracking()
            .Where(r => r.LocationRowid == locationId && r.IsPublic == 1)
            .OrderBy(r => r.Dimension).ThenBy(r => r.Id)
            .ToListAsync(cancellationToken);
        var warpIds = renders.Where(render => render.ArchiveWarpId.HasValue)
            .Select(render => render.ArchiveWarpId!.Value).Distinct().ToList();
        var warps = await _context.Warps.AsNoTracking()
            .Where(warp => warpIds.Contains(warp.Id))
            .ToDictionaryAsync(warp => warp.Id, cancellationToken);
        var legacyJobs = await GetLegacySourceJobsAsync(renders.Select(render => render.Id), cancellationToken);

        return Ok(renders.Select(r => MapToDto(
            r, location, r.ArchiveWarpId is int warpId ? warps.GetValueOrDefault(warpId) : null,
            legacyJobs.GetValueOrDefault(r.Id))).ToList());
    }

    private async Task<Dictionary<int, IngestionJob>> GetLegacySourceJobsAsync(
        IEnumerable<int> renderIds, CancellationToken cancellationToken)
    {
        var ids = renderIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        var jobs = await _context.IngestionJobs.AsNoTracking()
            .Where(job => job.RenderId.HasValue && ids.Contains(job.RenderId.Value) &&
                job.Status == "completed" && job.WarpId == null && job.ArchiveSha256 != null)
            .OrderByDescending(job => job.Id)
            .ToListAsync(cancellationToken);
        return jobs.Where(IsPublicRenderSourceJob)
            .GroupBy(job => job.RenderId!.Value)
            .ToDictionary(group => group.Key, group => group.First());
    }

    private static bool IsPublicRenderSourceJob(IngestionJob job) =>
        job.RenderId.HasValue && job.WarpId is null &&
        job.Status.Equals("completed", StringComparison.OrdinalIgnoreCase) &&
        job.ArchiveSha256 is { Length: 64 } sha && sha.All(Uri.IsHexDigit) &&
        !string.IsNullOrWhiteSpace(job.Source);

    private LocationRenderDto MapToDto(ServerRender r, ServerLocation loc, Warp? warp, IngestionJob? sourceJob)
    {
        var blueMap = _blueMap?.Find(r.Id);
        return new LocationRenderDto
        {
        RenderId = r.Id,
        ApiUrl = PublicAtlasUrls.RenderApi(r.Id),
        LocationId = loc.Rowid,
        LocationUrl = PublicAtlasUrls.Location(loc.Rowid),
        LocationApiUrl = PublicAtlasUrls.LocationApi(loc.Rowid),
        LocationName = loc.Name ?? string.Empty,
        LocationX = loc.X,
        LocationZ = loc.Z,
        Dimension = r.Dimension,
        DimensionName = DimensionName(r.Dimension),
        Name = r.Name,
        Description = r.Description,
        Source = r.Source,
        ArchiveWarpId = r.ArchiveWarpId,
        ArchiveWarpApiUrl = r.ArchiveWarpId is int warpId ? PublicAtlasUrls.WarpApi(warpId) : null,
        ArchiveWarpName = warp?.Name,
        Scale = r.Scale,
        TileUrlTemplate = r.TilesPath,
        HasDayNight = r.HasDayNight == 1,
        MaxNativeZoom = r.MaxNativeZoom,
        CoordinateScheme = r.CoordinateScheme,
        WorldDownloadDate = r.WorldDownloadDate,
        WorldDownloadUrl = sourceJob is null ? null : PublicAtlasUrls.RenderWorldDownload(r.Id, loc.Name),
        WorldDownloadMetadataUrl = sourceJob is null ? null : PublicAtlasUrls.RenderWorldDownloadMetadata(r.Id),
        WorldDownloadScope = sourceJob is null ? null : "preserved-render-source",
        WorldDownloadSha256 = sourceJob?.ArchiveSha256?.ToLowerInvariant(),
        WorldDownloadSource = sourceJob?.Source,
        BlueMapUrl = blueMap?.PublicUrl,
        BlueMapPath = blueMap?.RelativeUrl,
        BlueMapProfileVersion = blueMap?.RendererProfileVersion,
        MinX = r.MinX,
        MinZ = r.MinZ,
        MaxXExclusive = r.MaxXExclusive,
        MaxZExclusive = r.MaxZExclusive,
        DateAddedUtc = r.DateAddedUtc,
        };
    }

    private static string DimensionName(int dimension) => dimension switch
    {
        0 => "Overworld",
        1 => "Nether",
        2 => "End",
        _ => "Unknown",
    };
}
