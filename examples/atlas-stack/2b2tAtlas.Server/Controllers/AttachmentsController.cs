using Atlas.Locations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using _2b2tAtlas.Server.Models;
using _2b2tAtlas.Server.Services;

namespace _2b2tAtlas.Server.Controllers;

/// <summary>Read-only public catalog of sourced media and reference links attached to Atlas locations.</summary>
[ApiController]
[Route("api/[controller]")]
[AllowAnonymous]
public sealed class AttachmentsController : ControllerBase
{
    private readonly AtlasContext _context;

    /// <summary>Initializes the public attachment catalog.</summary>
    public AttachmentsController(AtlasContext context) => _context = context;

    /// <summary>Returns public attachment metadata, optionally filtered by location or media type.</summary>
    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<IEnumerable<AttachmentRecord>>> GetAttachments(
        [FromQuery] int? locationId = null,
        [FromQuery] string? mediaType = null,
        [FromQuery] int limit = 500,
        [FromQuery] int offset = 0)
    {
        limit = Math.Clamp(limit, 1, 1000);
        offset = Math.Max(0, offset);
        var query = _context.Attachments.AsNoTracking().Include(item => item.LocationRow).AsQueryable();
        if (locationId.HasValue) query = query.Where(item => item.LocationRowid == locationId.Value);
        if (!string.IsNullOrWhiteSpace(mediaType)) query = query.Where(item => item.MediaType == mediaType.Trim());
        var rows = await query.OrderBy(item => item.Id).Skip(offset).Take(limit).ToListAsync();
        return Ok(rows.Select(Map));
    }

    /// <summary>Returns one public attachment metadata record.</summary>
    [HttpGet("{id:int}")]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any)]
    public async Task<ActionResult<AttachmentRecord>> GetAttachment(int id)
    {
        var row = await _context.Attachments.AsNoTracking().Include(item => item.LocationRow)
            .FirstOrDefaultAsync(item => item.Id == id);
        return row is null ? NotFound() : Ok(Map(row));
    }

    private static AttachmentRecord Map(_2b2tAtlas.Server.Models.Attachment item) => new()
    {
        Id = item.Id,
        ApiUrl = PublicAtlasUrls.AttachmentApi(item.Id),
        LocationRowid = item.LocationRowid,
        LocationName = item.LocationRow?.Name ?? string.Empty,
        Dimension = (Atlas.Dimension)(item.LocationRow?.Dimension ?? 0),
        LocationUrl = PublicAtlasUrls.Location(item.LocationRowid),
        LocationApiUrl = PublicAtlasUrls.LocationApi(item.LocationRowid),
        FileName = item.FileName,
        Path = item.FilePath,
        MediaType = item.MediaType,
        ThumbnailPath = item.ThumbnailPath,
        SourceUrl = item.SourceUrl,
        Caption = item.Caption,
        Attribution = item.Attribution,
        DateAddedUtc = item.DateAddedUtc,
    };
}
