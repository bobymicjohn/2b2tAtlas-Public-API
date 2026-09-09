using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Atlas;
using Atlas.Auth;
using _2b2tAtlas.Server.Models;
using _2b2tAtlas.Server.Services;
using ServerMapRender = _2b2tAtlas.Server.Models.MapRender;

namespace _2b2tAtlas.Server.Controllers;

/// <summary>
/// Dimension-level world renders (full tile pyramids) that the map picker stacks
/// (GAMEPLAN §6b / Phase 1). Reads are public (published only). The ingest
/// pipeline upserts renders by slug (policy: <c>renders.manage</c>) so new world
/// downloads become map layers with no manual admin step.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class MapRendersController : ControllerBase
{
    private readonly AtlasContext _context;
    private readonly AuditService _audit;
    private readonly string[] _allowedUrlPrefixes;

    /// <summary>Initializes the world-render registry and its URL trust policy.</summary>
    /// <param name="context">The Atlas context containing dimension-level tile pyramid registrations.</param>
    /// <param name="audit">The append-only recorder for render registrations and removals.</param>
    /// <param name="configuration">Configuration containing the allowed public tile URL prefixes.</param>
    public MapRendersController(AtlasContext context, AuditService audit, IConfiguration configuration)
    {
        _context = context;
        _audit = audit;
        _allowedUrlPrefixes = configuration.GetSection("MapRenders:AllowedUrlPrefixes").Get<string[]>() ?? [];
    }

    /// <summary>GET /api/maprenders — published world renders (public map layers).</summary>
    [HttpGet]
    [AllowAnonymous]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any, VaryByHeader = "Origin")]
    public async Task<ActionResult<IEnumerable<MapRenderDto>>> GetPublished()
    {
        var rows = await _context.MapRenders
            .AsNoTracking()
            .Where(r => r.IsPublished == 1)
            .OrderBy(r => r.Dimension).ThenBy(r => r.SortOrder).ThenBy(r => r.Id)
            .ToListAsync();
        return Ok(rows.Select(MapToDto).ToList());
    }

    /// <summary>GET /api/maprenders/all — every render regardless of publish state.</summary>
    [HttpGet("all")]
    [Authorize(Policy = Permissions.RendersManage)]
    public async Task<ActionResult<IEnumerable<MapRenderDto>>> GetAll()
    {
        var rows = await _context.MapRenders
            .OrderBy(r => r.Dimension).ThenBy(r => r.SortOrder).ThenBy(r => r.Id)
            .ToListAsync();
        return Ok(rows.Select(MapToDto).ToList());
    }

    /// <summary>
    /// GET /api/maprenders/catalog — a combined, external read-only view of every render the
    /// atlas exposes: the dimension-level primary layers (full tile pyramids) and every
    /// per-location base render (with its owning location and block bounds).
    /// </summary>
    [HttpGet("catalog")]
    [AllowAnonymous]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any, VaryByHeader = "Origin")]
    public async Task<ActionResult<RenderCatalogResponse>> GetCatalog()
    {
        // Primary layers: the canonical base layers, plus any ingest-registered published world renders.
        var primary = PrimaryMapRenders.All.ToList();
        var registered = await _context.MapRenders.AsNoTracking()
            .Where(r => r.IsPublished == 1)
            .OrderBy(r => r.Dimension).ThenBy(r => r.SortOrder).ThenBy(r => r.Id)
            .ToListAsync();
        primary.AddRange(registered.Select(r => new RenderCatalogEntry
        {
            Kind = "primary",
            Id = r.Slug,
            Name = r.Name,
            Dimension = r.Dimension,
            Scale = r.Scale,
            TileUrlTemplate = r.UrlTemplate,
            HasDayNight = r.HasDayNight == 1,
            MaxNativeZoom = r.MaxNativeZoom,
            CoordinateScheme = "xyz-v1",
            Source = r.Source,
            WorldDownloadDate = r.WorldDownloadDate,
        }));

        var locations = await (
            from render in _context.Renders.AsNoTracking()
            join loc in _context.Locations.AsNoTracking() on render.LocationRowid equals loc.Rowid
            where render.IsPublic == 1
            orderby render.Dimension, render.Id
            select new RenderCatalogEntry
            {
                Kind = "location",
                Id = "render-" + render.Id,
                Name = render.Name,
                Dimension = render.Dimension,
                Scale = render.Scale,
                TileUrlTemplate = render.TilesPath,
                HasDayNight = render.HasDayNight == 1,
                MaxNativeZoom = render.MaxNativeZoom,
                CoordinateScheme = render.CoordinateScheme,
                WorldDownloadDate = render.WorldDownloadDate,
                LocationId = loc.Rowid,
                LocationName = loc.Name,
                LocationX = loc.X,
                LocationZ = loc.Z,
                MinX = render.MinX,
                MinZ = render.MinZ,
                MaxXExclusive = render.MaxXExclusive,
                MaxZExclusive = render.MaxZExclusive,
            }).ToListAsync();

        return Ok(new RenderCatalogResponse { Primary = primary, Locations = locations });
    }

    /// <summary>
    /// PUT /api/maprenders — idempotent upsert by slug. The ingestor calls this on
    /// completion to register (or refresh) a world render as a map layer.
    /// </summary>
    [HttpPut]
    [Authorize(Policy = Permissions.RendersManage)]
    public async Task<ActionResult<MapRenderDto>> Upsert([FromBody] MapRenderDto dto)
    {
        var errors = MapRenderRegistrationValidator.Validate(dto, _allowedUrlPrefixes);
        if (errors.Count > 0) return BadRequest(new { errors });
        var slug = dto.Slug.Trim();

        var row = await _context.MapRenders.FirstOrDefaultAsync(r => r.Slug == slug);
        var created = row == null;
        row ??= new ServerMapRender { Slug = slug, DateAddedUtc = DateTime.UtcNow.ToString("o") };

        Apply(dto, row);

        if (created) _context.MapRenders.Add(row);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException exception) when (created && IsUniqueConstraintViolation(exception))
        {
            _context.Entry(row).State = EntityState.Detached;
            var concurrentRow = await _context.MapRenders.SingleOrDefaultAsync(r => r.Slug == slug);
            if (concurrentRow is null)
                throw;
            row = concurrentRow;
            created = false;
            Apply(dto, row);
            await _context.SaveChangesAsync();
        }

        await _audit.LogAsync(created ? "maprender.create" : "maprender.update", "MapRender", row.Id,
            CurrentUserId(), CurrentUsername(), $"{(created ? "Registered" : "Updated")} render '{row.Name}' ({slug})");

        return Ok(MapToDto(row));
    }

    /// <summary>DELETE /api/maprenders/{id} — remove a world render.</summary>
    [HttpDelete("{id:int}")]
    [Authorize(Policy = Permissions.RendersManage)]
    public async Task<IActionResult> Delete(int id)
    {
        var row = await _context.MapRenders.FindAsync(id);
        if (row == null) return NotFound();
        _context.MapRenders.Remove(row);
        await _context.SaveChangesAsync();
        await _audit.LogAsync("maprender.delete", "MapRender", id, CurrentUserId(), CurrentUsername(),
            $"Deleted render '{row.Name}' ({row.Slug})");
        return NoContent();
    }

    private static MapRenderDto MapToDto(ServerMapRender r) => new()
    {
        Id = r.Id,
        Slug = r.Slug,
        Name = r.Name,
        Dimension = r.Dimension,
        Scale = r.Scale,
        UrlTemplate = r.UrlTemplate,
        HasDayNight = r.HasDayNight == 1,
        MaxNativeZoom = r.MaxNativeZoom,
        WorldDownloadDate = r.WorldDownloadDate,
        Source = r.Source,
        SortOrder = r.SortOrder,
        IsPublished = r.IsPublished == 1,
        DateAddedUtc = DateTime.TryParse(r.DateAddedUtc, out var d) ? d : DateTime.MinValue,
    };

    private static void Apply(MapRenderDto dto, ServerMapRender row)
    {
        row.Name = dto.Name.Trim();
        row.Dimension = dto.Dimension;
        row.Scale = dto.Scale.Trim().ToLowerInvariant();
        row.UrlTemplate = dto.UrlTemplate.Trim();
        row.HasDayNight = dto.HasDayNight ? 1 : 0;
        row.MaxNativeZoom = dto.MaxNativeZoom;
        row.WorldDownloadDate = dto.WorldDownloadDate;
        row.Source = dto.Source?.Trim();
        row.SortOrder = dto.SortOrder;
        row.IsPublished = dto.IsPublished ? 1 : 0;
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException exception) =>
        exception.InnerException is SqliteException { SqliteErrorCode: 19 };

    private int? CurrentUserId() =>
        int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var uid) ? uid : null;

    private string? CurrentUsername() => User.FindFirstValue(ClaimTypes.Name);
}
