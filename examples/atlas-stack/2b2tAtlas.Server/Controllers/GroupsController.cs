using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Atlas;
using Atlas.Auth;
using _2b2tAtlas.Server.Models;
using _2b2tAtlas.Server.Services;
using ServerGroup = _2b2tAtlas.Server.Models.Group;

namespace _2b2tAtlas.Server.Controllers;

/// <summary>
/// CRUD for 2b2t groups used to attribute highways/bases (GAMEPLAN §15).
/// Reads are public; writes require <c>groups.manage</c>.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class GroupsController : ControllerBase
{
    private readonly AtlasContext _context;
    private readonly AuditService _audit;

    /// <summary>Initializes the public group directory and permission-gated group editor.</summary>
    /// <param name="context">The Atlas context containing group attribution records.</param>
    /// <param name="audit">The append-only recorder for group mutations.</param>
    public GroupsController(AtlasContext context, AuditService audit)
    {
        _context = context;
        _audit = audit;
    }

    /// <summary>Returns the public directory of 2b2t build and infrastructure groups, ordered by name.</summary>
    /// <returns>A cacheable projection of all persisted groups.</returns>
    [HttpGet]
    [AllowAnonymous]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any, VaryByHeader = "Origin")]
    public async Task<ActionResult<IEnumerable<Atlas.Group>>> GetGroups()
    {
        var rows = await _context.Groups.AsNoTracking().OrderBy(g => g.Name).ToListAsync();
        var locationCounts = await _context.LocationGroups.AsNoTracking()
            .GroupBy(link => link.GroupId)
            .Select(group => new { GroupId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.GroupId, item => item.Count);
        var highwayCounts = await _context.HighwayGroups.AsNoTracking()
            .Where(link => link.Highway.Visibility == "Public" && link.Highway.ReviewStatus == "Approved")
            .GroupBy(link => link.GroupId)
            .Select(group => new { GroupId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.GroupId, item => item.Count);
        return Ok(rows.Select(row => MapToDto(
            row,
            locationCounts.GetValueOrDefault(row.Id),
            highwayCounts.GetValueOrDefault(row.Id))).ToList());
    }

    /// <summary>Returns one group used for Atlas attribution.</summary>
    /// <param name="id">The database identifier of the group.</param>
    /// <returns>The group projection, or <c>404</c> when it does not exist.</returns>
    [HttpGet("{id:int}")]
    [AllowAnonymous]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any, VaryByHeader = "Origin")]
    public async Task<ActionResult<Atlas.Group>> GetGroup(int id)
    {
        var row = await _context.Groups.AsNoTracking().FirstOrDefaultAsync(group => group.Id == id);
        if (row == null) return NotFound();
        var locations = await _context.LocationGroups.AsNoTracking()
            .Where(link => link.GroupId == id)
            .Join(_context.Locations.AsNoTracking(), link => link.LocationRowid, location => location.Rowid,
                (link, location) => new
                {
                    link.Role,
                    Location = location,
                })
            .OrderBy(item => item.Location.Name)
            .ToListAsync();
        var locationIds = locations.Select(item => item.Location.Rowid).ToList();
        var renderCounts = await _context.Renders.AsNoTracking()
            .Where(render => locationIds.Contains(render.LocationRowid) && render.IsPublic == 1)
            .GroupBy(render => render.LocationRowid)
            .Select(group => new { LocationId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.LocationId, item => item.Count);
        var highways = await _context.HighwayGroups.AsNoTracking()
            .Where(link => link.GroupId == id && link.Highway.Visibility == "Public" && link.Highway.ReviewStatus == "Approved")
            .Select(link => new { link.Role, Highway = link.Highway })
            .OrderBy(item => item.Highway.Name)
            .ToListAsync();
        var dto = MapToDto(row, locations.Count, highways.Count);
        dto.Locations = locations.Select(item => new GroupLocationSummary
        {
            LocationId = item.Location.Rowid,
            LocationUrl = PublicAtlasUrls.Location(item.Location.Rowid),
            LocationInteractiveUrl = PublicAtlasUrls.LocationInteractive(item.Location.Rowid),
            LocationApiUrl = PublicAtlasUrls.LocationApi(item.Location.Rowid),
            Name = item.Location.Name,
            Role = item.Role,
            Dimension = item.Location.Dimension,
            X = item.Location.X,
            Z = item.Location.Z,
            RenderCount = renderCounts.GetValueOrDefault(item.Location.Rowid),
        }).ToList();
        dto.Highways = highways.Select(item => new GroupHighwaySummary
        {
            HighwayId = item.Highway.Id,
            HighwayApiUrl = PublicAtlasUrls.HighwayApi(item.Highway.Id),
            MapUrl = PublicAtlasUrls.HighwayMap(item.Highway.Dimension),
            Name = item.Highway.Name,
            Dimension = item.Highway.Dimension,
            Role = item.Role,
        }).ToList();
        return Ok(dto);
    }

    /// <summary>Creates a group attribution record and audits the authenticated manager.</summary>
    /// <param name="dto">The group metadata; the server supplies its identifier and creation timestamp.</param>
    /// <returns>The persisted group with a location header for its public endpoint.</returns>
    [HttpPost]
    [Authorize(Policy = Permissions.GroupsManage)]
    public async Task<ActionResult<Atlas.Group>> CreateGroup([FromBody] Atlas.Group dto)
    {
        if (dto == null || string.IsNullOrWhiteSpace(dto.Name))
            return BadRequest("Name is required");
        if (ValidateUrls(dto) is { } urlError) return BadRequest(urlError);

        var row = new ServerGroup();
        ApplyDto(dto, row);
        row.DateAddedUtc = DateTime.UtcNow.ToString("o");
        row.ModifiedUtc = row.DateAddedUtc;
        _context.Groups.Add(row);
        await _context.SaveChangesAsync();
        await _audit.LogAsync("group.create", "Group", row.Id, CurrentUserId(), CurrentUsername(), $"Created group '{row.Name}'");
        return CreatedAtAction(nameof(GetGroup), new { id = row.Id }, MapToDto(row));
    }

    /// <summary>Replaces the editable metadata for an existing group and records the mutation.</summary>
    /// <param name="id">The group identifier whose metadata will be changed.</param>
    /// <param name="dto">The new name, classification, description, color, and wiki attribution.</param>
    /// <returns>The updated group, or <c>404</c> when the identifier is unknown.</returns>
    [HttpPut("{id:int}")]
    [Authorize(Policy = Permissions.GroupsManage)]
    public async Task<ActionResult<Atlas.Group>> UpdateGroup(int id, [FromBody] Atlas.Group dto)
    {
        if (dto == null) return BadRequest("Request body is required");
        if (string.IsNullOrWhiteSpace(dto.Name)) return BadRequest("Name is required");
        if (ValidateUrls(dto) is { } urlError) return BadRequest(urlError);
        var row = await _context.Groups.FindAsync(id);
        if (row == null) return NotFound();
        ApplyDto(dto, row);
        row.ModifiedUtc = DateTime.UtcNow.ToString("o");
        await _context.SaveChangesAsync();
        await _audit.LogAsync("group.update", "Group", id, CurrentUserId(), CurrentUsername(), $"Updated group '{row.Name}'");
        return Ok(MapToDto(row));
    }

    /// <summary>Deletes a group attribution record and writes an audit event.</summary>
    /// <param name="id">The identifier of the group to remove.</param>
    /// <returns><c>204</c> after deletion, or <c>404</c> when the group does not exist.</returns>
    [HttpDelete("{id:int}")]
    [Authorize(Policy = Permissions.GroupsManage)]
    public async Task<IActionResult> DeleteGroup(int id)
    {
        var row = await _context.Groups.FindAsync(id);
        if (row == null) return NotFound();
        var name = row.Name;
        var locationLinks = await _context.LocationGroups.Where(link => link.GroupId == id).ToListAsync();
        var highwayLinks = await _context.HighwayGroups.Where(link => link.GroupId == id).ToListAsync();
        _context.LocationGroups.RemoveRange(locationLinks);
        _context.HighwayGroups.RemoveRange(highwayLinks);
        _context.Groups.Remove(row);
        await _context.SaveChangesAsync();
        await _audit.LogAsync("group.delete", "Group", id, CurrentUserId(), CurrentUsername(), $"Deleted group '{name}'");
        return NoContent();
    }

    // ---- helpers ----

    private int? CurrentUserId() =>
        int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var uid) ? uid : null;

    private string? CurrentUsername() => User.FindFirstValue(ClaimTypes.Name);

    private static void ApplyDto(Atlas.Group dto, ServerGroup row)
    {
        row.Name = dto.Name.Trim();
        row.Type = dto.Type.ToString();
        row.Description = Clean(dto.Description);
        row.Color = Clean(dto.Color);
        row.WikiUrl = Clean(dto.WikiUrl);
        row.WebsiteUrl = Clean(dto.WebsiteUrl);
        row.DiscordUrl = Clean(dto.DiscordUrl);
        row.LogoUrl = Clean(dto.LogoUrl);
        row.LogoSourceUrl = Clean(dto.LogoSourceUrl);
        row.Founded = Clean(dto.Founded);
        row.Status = Clean(dto.Status);
    }

    private static Atlas.Group MapToDto(ServerGroup row, int locationCount = 0, int highwayCount = 0) => new()
    {
        Id = row.Id,
        CanonicalUrl = PublicAtlasUrls.Group(row.Id),
        InteractiveUrl = PublicAtlasUrls.GroupInteractive(row.Id),
        ApiUrl = PublicAtlasUrls.GroupApi(row.Id),
        Name = row.Name,
        Aliases = GroupAliases.For(row.Name).ToList(),
        Type = Enum.TryParse<GroupType>(row.Type, true, out var t) ? t : GroupType.Other,
        Description = row.Description,
        Color = row.Color,
        WikiUrl = row.WikiUrl,
        WebsiteUrl = row.WebsiteUrl,
        DiscordUrl = row.DiscordUrl,
        LogoUrl = row.LogoUrl,
        LogoSourceUrl = row.LogoSourceUrl,
        Founded = row.Founded,
        Status = row.Status,
        LocationCount = locationCount,
        HighwayCount = highwayCount,
        DateAddedUtc = DateTime.TryParse(row.DateAddedUtc, out var d) ? d : DateTime.MinValue,
        ModifiedUtc = DateTime.TryParse(row.ModifiedUtc, out var modified) ? modified :
            DateTime.TryParse(row.DateAddedUtc, out var fallback) ? fallback : DateTime.MinValue,
    };

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ValidateUrls(Atlas.Group dto)
    {
        foreach (var (label, value) in new[]
        {
            ("Wiki URL", dto.WikiUrl),
            ("Website URL", dto.WebsiteUrl),
            ("Discord URL", dto.DiscordUrl),
            ("Logo URL", dto.LogoUrl),
            ("Logo source URL", dto.LogoSourceUrl),
        })
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                return $"{label} must be an absolute HTTP or HTTPS URL.";
        }
        return null;
    }
}
