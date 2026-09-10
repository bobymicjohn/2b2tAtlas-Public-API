using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Atlas;
using Atlas.Auth;
using _2b2tAtlas.Server.Models;
using _2b2tAtlas.Server.Services;
using Atlas.Validation;
using Microsoft.AspNetCore.Authorization;

namespace _2b2tAtlas.Server.Controllers;

/// <summary>
/// Serves the public Atlas location catalog and permission-gated location, warp, and attachment mutations.
/// </summary>
/// <remarks>
/// Coordinates are persisted in native Minecraft dimension block units. Writes are transactional where
/// related rows are replaced, require the corresponding content permission, and append audit records.
/// </remarks>
[ApiController]
[Route("api/[controller]")]
public class LocationsController : ControllerBase
{
    private readonly AtlasContext _context;
    private readonly AuditService _audit;
    private readonly BlueMapCatalogService? _blueMap;

    /// <summary>Initializes the location catalog and editor API.</summary>
    /// <param name="context">The Atlas context containing locations and related content.</param>
    /// <param name="audit">The append-only recorder for location mutations.</param>
    /// <param name="blueMap">Validated interactive 3D derivative catalog, when configured.</param>
    public LocationsController(AtlasContext context, AuditService audit, BlueMapCatalogService? blueMap = null)
    {
        _context = context;
        _audit = audit;
        _blueMap = blueMap;
    }

    private int? CurrentUserId() =>
        int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var uid) ? uid : null;

    private string? CurrentUsername() => User.FindFirstValue(ClaimTypes.Name);

    /// <summary>Field snapshot used to compute before→after audit diffs.</summary>
    private static Dictionary<string, object?> SnapshotLocation(_2b2tAtlas.Server.Models.Location loc) => new()
    {
        ["Name"] = loc.Name,
        ["Description"] = loc.Description,
        ["Tags"] = loc.Tags,
        ["Wiki"] = loc.Wiki,
        ["VideoUrl"] = loc.VideoUrl,
        ["X"] = loc.X,
        ["Y"] = loc.Y,
        ["Z"] = loc.Z,
        ["Dimension"] = loc.Dimension,
    };

    /// <summary>
    /// Gets all locations with their warps
    /// </summary>
    /// <returns>List of locations with warps</returns>
    [HttpGet]
    [AllowAnonymous]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any, VaryByHeader = "Origin")]
    public async Task<ActionResult<IEnumerable<Atlas.Location>>> GetLocations()
    {
        try
        {
            var dbLocations = await _context.Locations
                .AsNoTracking()
                .AsSplitQuery()
                .Include(l => l.Attachments)
                .Include(l => l.Renders)
                .ToListAsync();

            // Get all warps for all locations in a separate query for better performance
            var locationIds = dbLocations.Select(l => l.Rowid).ToList();
            var publicRenderIds = dbLocations.SelectMany(location => location.Renders ?? [])
                .Where(render => render.IsPublic == 1)
                .Select(render => render.Id)
                .ToList();
            var legacySourceJobs = await _context.IngestionJobs
                .AsNoTracking()
                .Where(job => job.RenderId.HasValue && publicRenderIds.Contains(job.RenderId.Value) &&
                    job.Status == "completed" && job.WarpId == null && job.ArchiveSha256 != null)
                .OrderByDescending(job => job.Id)
                .ToListAsync();
            var legacySourceJobsByRender = legacySourceJobs
                .Where(IsPublicRenderSourceJob)
                .GroupBy(job => job.RenderId!.Value)
                .ToDictionary(group => group.Key, group => group.First());
            var allWarps = await _context.Warps
                .AsNoTracking()
                .Where(w => w.LocationRowid.HasValue && locationIds.Contains(w.LocationRowid.Value))
                .ToListAsync();
            var warpsByLocation = allWarps
                .GroupBy(w => w.LocationRowid!.Value)
                .ToDictionary(group => group.Key, group => group.ToList());
            var allGroupLinks = await _context.LocationGroups
                .AsNoTracking()
                .Where(link => locationIds.Contains(link.LocationRowid))
                .Include(link => link.Group)
                .ToListAsync();
            var groupsByLocation = allGroupLinks
                .GroupBy(link => link.LocationRowid)
                .ToDictionary(group => group.Key, group => group.Select(MapGroupAttribution).OrderBy(item => item.GroupName).ToList());

            // Convert from EF model to Atlas model
            var atlasLocations = dbLocations.Select(loc =>
            {
                var locationWarps = warpsByLocation.GetValueOrDefault(loc.Rowid, [])
                    .Select(warp => MapWarp(warp, loc.Name)).ToList();
                var locationWarpsById = locationWarps.ToDictionary(warp => warp.Id);

                return new Atlas.Location
                {
                    Rowid = loc.Rowid,
                    CanonicalUrl = PublicAtlasUrls.Location(loc.Rowid),
                    InteractiveUrl = PublicAtlasUrls.LocationInteractive(loc.Rowid),
                    ApiUrl = PublicAtlasUrls.LocationApi(loc.Rowid),
                    LocationUuid = loc.LocationUuid,
                    Name = loc.Name ?? string.Empty,
                    Description = loc.Description,
                    Tags = loc.Tags,
                    Wiki = loc.Wiki,
                    VideoUrl = loc.VideoUrl,
                    X = loc.X,
                    Y = loc.Y,
                    Z = loc.Z,
                    Dimension = loc.Dimension,
                    DateAddedUtc = DateTime.TryParse(loc.DateAddedUtc, out var parsedDateAdded) ? parsedDateAdded : DateTime.UtcNow,
                    ModifiedUtc = DateTime.TryParse(loc.ModifiedUtc, out var parsedModified) ? parsedModified :
                        DateTime.TryParse(loc.DateAddedUtc, out var modifiedFallback) ? modifiedFallback : DateTime.UtcNow,
                    Warps = locationWarps,
                    Groups = groupsByLocation.GetValueOrDefault(loc.Rowid, []),
                    Attachments = loc.Attachments?.Select(MapAttachment).ToList() ?? new List<Atlas.Locations.Attachment>(),
                    Renders = loc.Renders?.Where(r => r.IsPublic == 1).Select(r =>
                    {
                        var sourceJob = r.ArchiveWarpId is null
                            ? legacySourceJobsByRender.GetValueOrDefault(r.Id)
                            : null;
                        var blueMap = _blueMap?.Find(r.Id);
                        return new Atlas.Locations.Render
                        {
                        Id = r.Id,
                        ApiUrl = PublicAtlasUrls.RenderApi(r.Id),
                        LocationRowid = r.LocationRowid,
                        Name = r.Name,
                        Description = r.Description,
                        Source = r.Source,
                        ArchiveWarpId = r.ArchiveWarpId,
                        ArchiveWarp = r.ArchiveWarpId is int warpId
                            ? locationWarpsById.GetValueOrDefault(warpId)
                            : null,
                        ArchiveWarps = r.ArchiveWarpId is int archiveWarpId &&
                            locationWarpsById.TryGetValue(archiveWarpId, out var archiveWarp)
                                ? [archiveWarp]
                                : [],
                        Dimension = r.Dimension,
                        Scale = r.Scale,
                        TilesPath = r.TilesPath,
                        HasDayNight = r.HasDayNight == 1,
                        PreviewImagePath = r.PreviewImagePath,
                        WorldDownloadDate = r.WorldDownloadDate,
                        WorldDownloadUrl = sourceJob is null ? null : PublicAtlasUrls.RenderWorldDownload(r.Id, loc.Name, sourceJob.ArchiveSha256),
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
                        MaxNativeZoom = r.MaxNativeZoom,
                        CoordinateScheme = r.CoordinateScheme,
                        DateAddedUtc = r.DateAddedUtc
                        };
                    }).ToList() ?? new List<Atlas.Locations.Render>()
                };
            }).ToList();

            return Ok(atlasLocations);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets a specific location by ID with its warps
    /// </summary>
    /// <param name="id">Location ID</param>
    /// <returns>Location details with warps</returns>
    [HttpGet("{id}")]
    [AllowAnonymous]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any, VaryByHeader = "Origin")]
    public async Task<ActionResult<Atlas.Location>> GetLocation(int id)
    {
        try
        {
            var dbLocation = await _context.Locations
                .AsSplitQuery()
                .Include(l => l.Attachments)
                .Include(l => l.Renders)
                .FirstOrDefaultAsync(l => l.Rowid == id);

            if (dbLocation == null)
            {
                return NotFound();
            }

            // Get warps for this location
            var warps = await _context.Warps
                .Where(w => w.LocationRowid == id)
                .ToListAsync();
            var publicWarps = warps.Select(warp => MapWarp(warp, dbLocation.Name)).ToList();
            var publicWarpsById = publicWarps.ToDictionary(warp => warp.Id);
            var groupLinks = await _context.LocationGroups
                .AsNoTracking()
                .Where(link => link.LocationRowid == id)
                .Include(link => link.Group)
                .OrderBy(link => link.Group.Name)
                .ToListAsync();
            var publicRenderIds = dbLocation.Renders?.Where(render => render.IsPublic == 1)
                .Select(render => render.Id).ToList() ?? [];
            var legacySourceJobs = await _context.IngestionJobs
                .AsNoTracking()
                .Where(job => job.RenderId.HasValue && publicRenderIds.Contains(job.RenderId.Value) &&
                    job.Status == "completed" && job.WarpId == null && job.ArchiveSha256 != null)
                .OrderByDescending(job => job.Id)
                .ToListAsync();
            var legacySourceJobsByRender = legacySourceJobs
                .Where(IsPublicRenderSourceJob)
                .GroupBy(job => job.RenderId!.Value)
                .ToDictionary(group => group.Key, group => group.First());

            var atlasLocation = new Atlas.Location
            {
                Rowid = dbLocation.Rowid,
                CanonicalUrl = PublicAtlasUrls.Location(dbLocation.Rowid),
                InteractiveUrl = PublicAtlasUrls.LocationInteractive(dbLocation.Rowid),
                ApiUrl = PublicAtlasUrls.LocationApi(dbLocation.Rowid),
                LocationUuid = dbLocation.LocationUuid,
                Name = dbLocation.Name ?? string.Empty,
                Description = dbLocation.Description,
                Tags = dbLocation.Tags,
                Wiki = dbLocation.Wiki,
                VideoUrl = dbLocation.VideoUrl,
                X = dbLocation.X,
                Y = dbLocation.Y,
                Z = dbLocation.Z,
                Dimension = dbLocation.Dimension,
                DateAddedUtc = DateTime.TryParse(dbLocation.DateAddedUtc, out var parsedDate) ? parsedDate : DateTime.UtcNow,
                ModifiedUtc = DateTime.TryParse(dbLocation.ModifiedUtc, out var parsedModified) ? parsedModified :
                    DateTime.TryParse(dbLocation.DateAddedUtc, out var modifiedFallback) ? modifiedFallback : DateTime.UtcNow,
                Warps = publicWarps,
                Groups = groupLinks.Select(MapGroupAttribution).ToList(),
                Attachments = dbLocation.Attachments?.Select(MapAttachment).ToList() ?? new List<Atlas.Locations.Attachment>(),
                Renders = dbLocation.Renders?.Where(r => r.IsPublic == 1).Select(r =>
                {
                    var sourceJob = r.ArchiveWarpId is null
                        ? legacySourceJobsByRender.GetValueOrDefault(r.Id)
                        : null;
                    var blueMap = _blueMap?.Find(r.Id);
                    return new Atlas.Locations.Render
                    {
                    Id = r.Id,
                    ApiUrl = PublicAtlasUrls.RenderApi(r.Id),
                    LocationRowid = r.LocationRowid,
                    Name = r.Name,
                    Description = r.Description,
                    Source = r.Source,
                    ArchiveWarpId = r.ArchiveWarpId,
                    ArchiveWarp = r.ArchiveWarpId is int warpId
                        ? publicWarpsById.GetValueOrDefault(warpId)
                        : null,
                    ArchiveWarps = r.ArchiveWarpId is int archiveWarpId &&
                        publicWarpsById.TryGetValue(archiveWarpId, out var archiveWarp)
                            ? [archiveWarp]
                            : [],
                    Dimension = r.Dimension,
                    Scale = r.Scale,
                    TilesPath = r.TilesPath,
                    HasDayNight = r.HasDayNight == 1,
                    PreviewImagePath = r.PreviewImagePath,
                    WorldDownloadDate = r.WorldDownloadDate,
                    WorldDownloadUrl = sourceJob is null ? null : PublicAtlasUrls.RenderWorldDownload(r.Id, dbLocation.Name, sourceJob.ArchiveSha256),
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
                    MaxNativeZoom = r.MaxNativeZoom,
                    CoordinateScheme = r.CoordinateScheme,
                    DateAddedUtc = r.DateAddedUtc
                    };
                }).ToList() ?? new List<Atlas.Locations.Render>()
            };

            return Ok(atlasLocation);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates a new location with its warps
    /// </summary>
    /// <param name="location">Location to create</param>
    /// <returns>Created location</returns>
    [HttpPost]
    [Authorize(Policy = Permissions.LocationsCreate)]
    public async Task<ActionResult<Atlas.Location>> CreateLocation(Atlas.Location location)
    {
        try
        {
            using var transaction = await _context.Database.BeginTransactionAsync();

            // Create the location
            var now = DateTime.UtcNow.ToString("o");
            var dbLocation = new _2b2tAtlas.Server.Models.Location
            {
                LocationUuid = Guid.NewGuid().ToString(),
                Name = location.Name,
                Description = location.Description,
                Tags = location.Tags,
                Wiki = location.Wiki,
                VideoUrl = location.VideoUrl,
                X = location.X,
                Y = location.Y ?? 64, // Default Y level if null
                Z = location.Z,
                Dimension = location.Dimension,
                DateAddedUtc = now,
                ModifiedUtc = now
            };

            _context.Locations.Add(dbLocation);
            await _context.SaveChangesAsync();

            if (location.Groups is { Count: > 0 })
                await ReplaceLocationGroupsAsync(dbLocation.Rowid, location.Groups, now);

            // Create the warps
            if (location.Warps?.Any() == true)
            {
                var dbWarps = location.Warps.Select(w => new _2b2tAtlas.Server.Models.Warp
                {
                    Name = w.Name,
                    LocationRowid = dbLocation.Rowid,
                    TimeAdded = w.TimeAdded.ToString("o"),
                    WarpUuid = w.WarpUuid,
                    LocationUuidFk = w.LocationUuidFk,
                    ArchiveSha256 = w.ArchiveSha256,
                    WorldDownloadDate = w.WorldDownloadDate,
                    Source = w.Source,
                    ArchiveX = w.ArchiveX,
                    ArchiveY = w.ArchiveY,
                    ArchiveZ = w.ArchiveZ
                }).ToList();

                _context.Warps.AddRange(dbWarps);
                await _context.SaveChangesAsync();

                // Update the location with warp IDs
                location.Warps = dbWarps.Select(w => new Atlas.Locations.Warp
                {
                    Id = w.Id,
                    Name = w.Name,
                    LocationRowid = w.LocationRowid,
                    TimeAdded = DateTime.Parse(w.TimeAdded),
                    WarpUuid = w.WarpUuid,
                    LocationUuidFk = w.LocationUuidFk,
                    ArchiveSha256 = w.ArchiveSha256,
                    WorldDownloadDate = w.WorldDownloadDate,
                    Source = w.Source,
                    ArchiveX = w.ArchiveX,
                    ArchiveY = w.ArchiveY,
                    ArchiveZ = w.ArchiveZ
                }).ToList();
            }

            await _context.SaveChangesAsync();
            await transaction.CommitAsync();

            location.Rowid = dbLocation.Rowid;
            location.LocationUuid = dbLocation.LocationUuid;
            location.DateAddedUtc = DateTime.Parse(dbLocation.DateAddedUtc);
            location.ModifiedUtc = DateTime.Parse(dbLocation.ModifiedUtc);

            await _audit.LogAsync("location.create", "Location", dbLocation.Rowid, CurrentUserId(), CurrentUsername(),
                $"Created '{dbLocation.Name}'");

            return CreatedAtAction(nameof(GetLocation), new { id = location.Rowid }, location);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    /// <summary>
    /// Updates an existing location and its warps
    /// </summary>
    /// <param name="id">Location ID</param>
    /// <param name="location">Updated location data</param>
    /// <returns>Updated location</returns>
    [HttpPut("{id}")]
    [Authorize(Policy = Permissions.LocationsEdit)]
    public async Task<ActionResult<Atlas.Location>> UpdateLocation(int id, Atlas.Location location)
    {
        if (id != location.Rowid)
        {
            return BadRequest("ID mismatch");
        }

        try
        {
            using var transaction = await _context.Database.BeginTransactionAsync();

            var dbLocation = await _context.Locations.FindAsync(id);
            if (dbLocation == null)
            {
                return NotFound();
            }

            var before = SnapshotLocation(dbLocation);

            // Update location
            dbLocation.Name = location.Name;
            dbLocation.Description = location.Description;
            dbLocation.Tags = location.Tags;
            dbLocation.Wiki = location.Wiki;
            dbLocation.VideoUrl = location.VideoUrl;
            dbLocation.X = location.X;
            dbLocation.Y = location.Y ?? 64; // Default Y level if null
            dbLocation.Z = location.Z;
            dbLocation.Dimension = location.Dimension;
            dbLocation.ModifiedUtc = DateTime.UtcNow.ToString("o");

            // Synchronize warps in place. Recreating every row here used to discard Archive hashes,
            // landing coordinates, and the render's ArchiveWarpId whenever an admin changed unrelated
            // location metadata (including group attribution).
            var existingWarps = await _context.Warps
                .Where(w => w.LocationRowid == id)
                .ToListAsync();
            var requestedWarps = location.Warps ?? [];
            var requestedExistingIds = requestedWarps.Where(warp => warp.Id > 0).Select(warp => warp.Id).ToHashSet();
            _context.Warps.RemoveRange(existingWarps.Where(warp => !requestedExistingIds.Contains(warp.Id)));

            foreach (var requested in requestedWarps)
            {
                var persisted = requested.Id > 0
                    ? existingWarps.FirstOrDefault(warp => warp.Id == requested.Id)
                    : null;
                if (persisted is null)
                {
                    persisted = new _2b2tAtlas.Server.Models.Warp
                    {
                        LocationRowid = id,
                        ArchiveSha256 = requested.ArchiveSha256,
                        WorldDownloadDate = requested.WorldDownloadDate,
                        Source = requested.Source,
                        ArchiveX = requested.ArchiveX,
                        ArchiveY = requested.ArchiveY,
                        ArchiveZ = requested.ArchiveZ,
                    };
                    _context.Warps.Add(persisted);
                }

                persisted.Name = requested.Name;
                persisted.TimeAdded = requested.TimeAdded.ToString("o");
                persisted.WarpUuid = requested.WarpUuid;
                persisted.LocationUuidFk = requested.LocationUuidFk;
            }

            if (location.Groups is not null)
                await ReplaceLocationGroupsAsync(id, location.Groups, dbLocation.ModifiedUtc);

            await _context.SaveChangesAsync();
            await transaction.CommitAsync();

            location.ModifiedUtc = DateTime.Parse(dbLocation.ModifiedUtc);

            await _audit.LogDiffAsync("location.update", "Location", id, CurrentUserId(), CurrentUsername(),
                $"Updated '{dbLocation.Name}'", before, SnapshotLocation(dbLocation));

            return Ok(location);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    /// <summary>
    /// Updates attachments for a specific location
    /// </summary>
    /// <param name="id">Location ID</param>
    /// <param name="attachments">List of attachments to update</param>
    /// <returns>Success or error response</returns>
    [HttpPut("{id}/attachments")]
    [Authorize(Policy = Permissions.AttachmentsManage)]
    public async Task<ActionResult> UpdateLocationAttachments(int id, [FromBody] List<Atlas.Locations.Attachment> attachments)
    {
        try
        {
            if (attachments.Count > AttachmentLinkValidator.MaximumAttachmentsPerLocation)
                return BadRequest($"A location cannot have more than {AttachmentLinkValidator.MaximumAttachmentsPerLocation} attachments.");

            var validatedAttachments = new List<(string FileName, string Url, string MediaType, string? Thumbnail, string? Source, string? Caption, string? Attribution)>();
            var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < attachments.Count; index++)
            {
                var attachment = attachments[index];
                var fileName = attachment.FileName?.Trim();
                if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 255)
                    return BadRequest($"Attachment {index + 1} must have a name between 1 and 255 characters.");
                if (!AttachmentLinkValidator.TryNormalizeHttps(attachment.Path, out var url, out var error))
                    return BadRequest($"Attachment {index + 1}: {error}");
                if (!seenUrls.Add(url))
                    return BadRequest($"Attachment {index + 1} duplicates an existing URL.");
                if (!TryOptionalHttps(attachment.ThumbnailPath, out var thumbnail, out error) ||
                    !TryOptionalHttps(attachment.SourceUrl, out var source, out error))
                    return BadRequest($"Attachment {index + 1}: {error}");
                var mediaType = NormalizeMediaType(attachment.MediaType, url);
                var caption = NormalizeOptional(attachment.Caption, 500);
                var attribution = NormalizeOptional(attachment.Attribution, 500);
                validatedAttachments.Add((fileName, url, mediaType, thumbnail, source, caption, attribution));
            }

            using var transaction = await _context.Database.BeginTransactionAsync();

            // Verify the location exists
            var location = await _context.Locations.FindAsync(id);
            if (location == null)
            {
                return NotFound($"Location with ID {id} not found");
            }
            location.ModifiedUtc = DateTime.UtcNow.ToString("o");

            // Remove all existing attachments for this location
            var existingAttachments = await _context.Attachments
                .Where(a => a.LocationRowid == id)
                .ToListAsync();

            _context.Attachments.RemoveRange(existingAttachments);

            foreach (var attachment in validatedAttachments)
            {
                var newAttachment = new _2b2tAtlas.Server.Models.Attachment
                {
                    LocationRowid = id,
                    FileName = attachment.FileName,
                    FilePath = attachment.Url,
                    MediaType = attachment.MediaType,
                    ThumbnailPath = attachment.Thumbnail,
                    SourceUrl = attachment.Source,
                    Caption = attachment.Caption,
                    Attribution = attachment.Attribution,
                    DateAddedUtc = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")
                };
                _context.Attachments.Add(newAttachment);
            }

            await _context.SaveChangesAsync();
            await transaction.CommitAsync();

            await _audit.LogAsync("location.attachments", "Location", id, CurrentUserId(), CurrentUsername(),
                $"Updated attachments ({validatedAttachments.Count})");

            return Ok(new { message = $"Successfully updated {validatedAttachments.Count} attachments for location {id}" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Internal server error: {ex.Message}" });
        }
    }

    /// <summary>
    /// Deletes a location and all its associated warps
    /// </summary>
    /// <param name="id">Location ID</param>
    /// <returns>No content</returns>
    [HttpDelete("{id}")]
    [Authorize(Policy = Permissions.LocationsDelete)]
    public async Task<ActionResult> DeleteLocation(int id)
    {
        try
        {
            using var transaction = await _context.Database.BeginTransactionAsync();

            var dbLocation = await _context.Locations.FindAsync(id);
            if (dbLocation == null)
            {
                return NotFound();
            }

            // Delete associated warps first
            var warps = await _context.Warps
                .Where(w => w.LocationRowid == id)
                .ToListAsync();

            _context.Warps.RemoveRange(warps);

            // Delete associated attachments
            var attachments = await _context.Attachments
                .Where(a => a.LocationRowid == id)
                .ToListAsync();

            _context.Attachments.RemoveRange(attachments);

            // Delete associated renders
            var renders = await _context.Renders
                .Where(r => r.LocationRowid == id)
                .ToListAsync();

            _context.Renders.RemoveRange(renders);

            var groupLinks = await _context.LocationGroups
                .Where(link => link.LocationRowid == id)
                .ToListAsync();
            _context.LocationGroups.RemoveRange(groupLinks);

            // Delete the location
            _context.Locations.Remove(dbLocation);

            await _context.SaveChangesAsync();
            await transaction.CommitAsync();

            await _audit.LogAsync("location.delete", "Location", id, CurrentUserId(), CurrentUsername(),
                $"Deleted '{dbLocation.Name}'");

            return NoContent();
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    private static Atlas.Locations.Warp MapWarp(_2b2tAtlas.Server.Models.Warp warp, string? locationName) => new()
    {
        Id = warp.Id,
        ApiUrl = PublicAtlasUrls.WarpApi(warp.Id),
        WarpUuid = warp.WarpUuid,
        LocationUuidFk = warp.LocationUuidFk,
        LocationRowid = warp.LocationRowid,
        Name = warp.Name,
        TimeAdded = DateTime.TryParse(warp.TimeAdded, out var parsedDate) ? parsedDate : DateTime.Now,
        ArchiveSha256 = warp.ArchiveSha256,
        WorldDownloadUrl = HasPublicWorldDownload(warp) ? PublicAtlasUrls.WorldDownload(
            warp.Id,
            ArchiveWarpResolver.IsSinglePlayerConcept(warp.Name)
                ? $"{locationName ?? "2b2t-location"} singleplayer concept"
                : locationName, warp.ArchiveSha256) : null,
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

    private static bool IsPublicRenderSourceJob(IngestionJob job) =>
        job.RenderId.HasValue && job.WarpId is null &&
        job.Status.Equals("completed", StringComparison.OrdinalIgnoreCase) &&
        job.ArchiveSha256 is { Length: 64 } sha && sha.All(Uri.IsHexDigit) &&
        !string.IsNullOrWhiteSpace(job.Source);

    private static Atlas.Locations.Attachment MapAttachment(_2b2tAtlas.Server.Models.Attachment attachment) => new()
    {
        Id = attachment.Id,
        ApiUrl = PublicAtlasUrls.AttachmentApi(attachment.Id),
        LocationRowid = attachment.LocationRowid,
        FileName = attachment.FileName,
        Path = attachment.FilePath,
        MediaType = NormalizeMediaType(attachment.MediaType, attachment.FilePath),
        ThumbnailPath = attachment.ThumbnailPath,
        SourceUrl = attachment.SourceUrl,
        Caption = attachment.Caption,
        Attribution = attachment.Attribution,
        DateAddedUtc = attachment.DateAddedUtc,
    };

    private static string NormalizeMediaType(string? value, string url)
    {
        var allowed = new[] { "Image", "Video", "Wiki", "Link", "Timeline", "Article" };
        var match = allowed.FirstOrDefault(item => item.Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match;
        if (url.Contains("wiki", StringComparison.OrdinalIgnoreCase)) return "Wiki";
        if (url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) || url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase) ||
            url.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) return "Video";
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        return new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".avif" }
            .Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) ? "Image" : "Link";
    }

    private static bool TryOptionalHttps(string? value, out string? normalized, out string error)
    {
        normalized = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!AttachmentLinkValidator.TryNormalizeHttps(value, out var url, out error)) return false;
        normalized = url;
        return true;
    }

    private static string? NormalizeOptional(string? value, int maximumLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }

    private static LocationGroupAttribution MapGroupAttribution(LocationGroup link) => new()
    {
        GroupId = link.GroupId,
        GroupUrl = PublicAtlasUrls.Group(link.GroupId),
        GroupInteractiveUrl = PublicAtlasUrls.GroupInteractive(link.GroupId),
        GroupApiUrl = PublicAtlasUrls.GroupApi(link.GroupId),
        GroupName = link.Group.Name,
        Role = link.Role,
        Color = link.Group.Color,
        LogoUrl = link.Group.LogoUrl,
    };

    private async Task ReplaceLocationGroupsAsync(
        int locationId,
        IEnumerable<LocationGroupAttribution> requested,
        string now)
    {
        var normalized = requested
            .Where(item => item.GroupId > 0)
            .GroupBy(item => item.GroupId)
            .Select(group => new
            {
                GroupId = group.Key,
                Role = string.IsNullOrWhiteSpace(group.First().Role) ? "Builder" : group.First().Role.Trim(),
            })
            .ToList();
        var requestedIds = normalized.Select(item => item.GroupId).ToList();
        var validIds = requestedIds.Count == 0
            ? []
            : await _context.Groups.Where(group => requestedIds.Contains(group.Id)).Select(group => group.Id).ToListAsync();
        var validIdSet = validIds.ToHashSet();
        var existing = await _context.LocationGroups.Where(link => link.LocationRowid == locationId).ToListAsync();
        _context.LocationGroups.RemoveRange(existing);
        foreach (var item in normalized.Where(item => validIdSet.Contains(item.GroupId)))
        {
            _context.LocationGroups.Add(new LocationGroup
            {
                LocationRowid = locationId,
                GroupId = item.GroupId,
                Role = item.Role,
                DateAddedUtc = now,
            });
        }
    }
}
