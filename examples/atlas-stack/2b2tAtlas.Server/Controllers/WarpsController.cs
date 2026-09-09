using Atlas.Locations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using _2b2tAtlas.Server.Models;
using _2b2tAtlas.Server.Services;

namespace _2b2tAtlas.Server.Controllers;

/// <summary>Read-only public records for Archive warps linked to Atlas locations and WDL renders.</summary>
[ApiController]
[Route("api/[controller]")]
[AllowAnonymous]
public sealed class WarpsController : ControllerBase
{
    private readonly AtlasContext _context;

    /// <summary>Initializes the public Archive warp catalog.</summary>
    public WarpsController(AtlasContext context) => _context = context;

    /// <summary>Returns Archive warp records, optionally filtered by owning location.</summary>
    [HttpGet]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any, VaryByHeader = "Origin")]
    public async Task<ActionResult<IEnumerable<WarpRecord>>> GetWarps(
        [FromQuery] int? locationId = null,
        [FromQuery] int limit = 500,
        [FromQuery] int offset = 0,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 1000);
        offset = Math.Max(0, offset);
        var query = _context.Warps.AsNoTracking().Include(warp => warp.LocationRow).AsQueryable();
        if (locationId.HasValue) query = query.Where(warp => warp.LocationRowid == locationId.Value);
        var rows = await query.OrderBy(warp => warp.Id).Skip(offset).Take(limit).ToListAsync(cancellationToken);
        return Ok(rows.Where(warp => warp.LocationRow is not null).Select(Map).ToList());
    }

    /// <summary>Returns one Archive warp with owning-location context.</summary>
    [HttpGet("{id:int}")]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any, VaryByHeader = "Origin")]
    public async Task<ActionResult<WarpRecord>> GetWarp(int id, CancellationToken cancellationToken)
    {
        var row = await _context.Warps.AsNoTracking().Include(warp => warp.LocationRow)
            .FirstOrDefaultAsync(warp => warp.Id == id, cancellationToken);
        return row?.LocationRow is null ? NotFound() : Ok(Map(row));
    }

    private static WarpRecord Map(_2b2tAtlas.Server.Models.Warp warp) => new()
    {
        Id = warp.Id,
        ApiUrl = PublicAtlasUrls.WarpApi(warp.Id),
        WarpUuid = warp.WarpUuid,
        LocationUuidFk = warp.LocationUuidFk,
        LocationRowid = warp.LocationRowid,
        LocationName = warp.LocationRow.Name ?? string.Empty,
        Dimension = (Atlas.Dimension)warp.LocationRow.Dimension,
        LocationUrl = PublicAtlasUrls.Location(warp.LocationRow.Rowid),
        LocationApiUrl = PublicAtlasUrls.LocationApi(warp.LocationRow.Rowid),
        Name = warp.Name,
        TimeAdded = DateTime.TryParse(warp.TimeAdded, out var added) ? added : DateTime.MinValue,
        ArchiveSha256 = warp.ArchiveSha256,
        WorldDownloadUrl = HasPublicWorldDownload(warp) ? PublicAtlasUrls.WorldDownload(
            warp.Id,
            Atlas.ArchiveWarpResolver.IsSinglePlayerConcept(warp.Name)
                ? $"{warp.LocationRow.Name ?? "2b2t-location"} singleplayer concept"
                : warp.LocationRow.Name) : null,
        WorldDownloadMetadataUrl = HasPublicWorldDownload(warp) ? PublicAtlasUrls.WorldDownloadMetadata(warp.Id) : null,
        WorldDownloadScope = HasPublicWorldDownload(warp) ? "bounded-footprint" : null,
        WorldDownloadDate = warp.WorldDownloadDate,
        Source = warp.Source,
        ArchiveX = warp.ArchiveX,
        ArchiveY = warp.ArchiveY,
        ArchiveZ = warp.ArchiveZ,
    };

    private static bool HasPublicWorldDownload(_2b2tAtlas.Server.Models.Warp warp) =>
        warp.ArchiveSha256 is { Length: 64 } sha && sha.All(Uri.IsHexDigit) &&
        warp.Source?.StartsWith("The Archive automated sync", StringComparison.OrdinalIgnoreCase) == true;
}
