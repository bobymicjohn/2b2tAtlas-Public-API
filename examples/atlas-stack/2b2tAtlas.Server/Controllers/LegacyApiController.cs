using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using _2b2tAtlas.Server.Models;

namespace _2b2tAtlas.Server.Controllers;

/// <summary>
/// Read-only compatibility surface for the original public PHP API. Modern clients
/// should use /api/locations. The historical anonymous write route remains closed.
/// </summary>
[ApiController]
[AllowAnonymous]
public class LegacyApiController : ControllerBase
{
    private readonly AtlasContext _context;

    /// <summary>Initializes the read-only adapter over the modern Atlas location tables.</summary>
    /// <param name="context">The Atlas context queried without entity tracking.</param>
    public LegacyApiController(AtlasContext context)
    {
        _context = context;
    }

    /// <summary>Returns locations in the exact snake-case shape expected by historical PHP API clients.</summary>
    /// <param name="x">Optional block X coordinate used with <paramref name="z"/> to sort nearest locations first.</param>
    /// <param name="z">Optional block Z coordinate used with <paramref name="x"/> to sort nearest locations first.</param>
    /// <param name="rows">Optional maximum row count, bounded to 10,000.</param>
    /// <param name="dimension">Legacy dimension code: 0 Overworld, 1 End, or -1 Nether.</param>
    /// <param name="warps">Set to 1 to retain only locations that have at least one warp.</param>
    /// <param name="search">Optional case-insensitive name fragment interpreted by SQLite <c>LIKE</c>.</param>
    /// <returns>A cacheable compatibility projection with coordinates encoded as strings.</returns>
    [HttpGet("api/locations.php")]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any)]
    public async Task<ActionResult<IReadOnlyList<LegacyLocation>>> GetLocations(
        [FromQuery] int? x,
        [FromQuery] int? z,
        [FromQuery] int? rows,
        [FromQuery] int? dimension,
        [FromQuery] int? warps,
        [FromQuery] string? search)
    {
        SetLegacyCors();
        if (rows is <= 0) return BadRequest("rows must be greater than zero.");
        if (rows is > 10000) return BadRequest("rows cannot exceed 10000.");
        if (dimension is not null and not (0 or 1 or -1))
            return BadRequest("dimension must be 0 (Overworld), 1 (End), or -1 (Nether).");

        var query = _context.Locations.AsNoTracking();
        if (dimension.HasValue)
        {
            var modernDimension = dimension.Value switch { 1 => 2, -1 => 1, _ => 0 };
            query = query.Where(location => location.Dimension == modernDimension);
        }
        if (!string.IsNullOrEmpty(search))
            query = query.Where(location => EF.Functions.Like(location.Name, $"%{search}%"));

        var locations = await query.ToListAsync();
        locations = x.HasValue && z.HasValue
            ? locations.OrderBy(location => DistanceSquared(location, x.Value, z.Value)).ToList()
            : locations.OrderBy(location => location.DateAddedUtc, StringComparer.Ordinal).ToList();
        if (rows.HasValue) locations = locations.Take(rows.Value).ToList();

        var rowIds = locations.Select(location => location.Rowid).ToList();
        var warpRows = await _context.Warps.AsNoTracking()
            .Where(warp => warp.LocationRowid.HasValue && rowIds.Contains(warp.LocationRowid.Value))
            .OrderBy(warp => warp.Id)
            .Select(warp => new { LocationId = warp.LocationRowid!.Value, warp.Name })
            .ToListAsync();
        var warpsByLocation = warpRows.GroupBy(warp => warp.LocationId)
            .ToDictionary(group => group.Key, group => group.Select(warp => new LegacyWarp(warp.Name)).ToList());

        var results = locations.Select(location => new LegacyLocation
        {
            LocationUuid = location.LocationUuid,
            UserUuid = string.Empty,
            Name = location.Name,
            X = location.X.ToString(),
            Y = location.Y.ToString(),
            Z = location.Z.ToString(),
            Description = location.Description ?? string.Empty,
            Tags = location.Tags ?? string.Empty,
            TimeAdded = location.DateAddedUtc,
            Wiki = location.Wiki ?? string.Empty,
            VideoUrl = location.VideoUrl ?? string.Empty,
            EndDimension = location.Dimension == 2 ? 1 : 0,
            Warps = warpsByLocation.GetValueOrDefault(location.Rowid),
        })
        .Where(location => warps != 1 || location.Warps is { Count: > 0 })
        .ToList();
        return Ok(results);
    }

    /// <summary>Returns the total location count using the legacy <c>locationCount</c> response property.</summary>
    /// <returns>A cacheable anonymous object containing the current persisted location count.</returns>
    [HttpGet("api/locationCount.php")]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any)]
    public async Task<ActionResult<object>> GetLocationCount()
    {
        SetLegacyCors();
        return Ok(new { locationCount = await _context.Locations.CountAsync() });
    }

    /// <summary>Rejects the retired anonymous legacy warp-write route.</summary>
    /// <returns><c>410 Gone</c> with guidance to use the authenticated modern API.</returns>
    [AcceptVerbs("GET", "POST")]
    [Route("api/newWarp.php")]
    public IActionResult LegacyWarpWriteRetired()
    {
        SetLegacyCors();
        return StatusCode(StatusCodes.Status410Gone, new
        {
            error = "The anonymous legacy warp-write endpoint was retired for security.",
            modernEndpoint = "/api/locations/{id}",
        });
    }

    private static long DistanceSquared(Location location, int x, int z)
    {
        var dx = (long)location.X - x;
        var dz = (long)location.Z - z;
        return dx * dx + dz * dz;
    }

    private void SetLegacyCors()
    {
        // Cross-origin access is granted by the global "PublicAPI" CORS policy (AllowAnyOrigin);
        // this only tags the legacy compatibility surface.
        Response.Headers["X-Atlas-Compatibility"] = "legacy-read-v1";
    }

    /// <summary>Wire-compatible location payload for consumers of the retired PHP implementation.</summary>
    public sealed class LegacyLocation
    {
        /// <summary>Gets the stable Atlas location UUID used by legacy clients and warp associations.</summary>
        [JsonPropertyName("location_uuid")]
        public string LocationUuid { get; init; } = string.Empty;

        /// <summary>Gets the historical submitter UUID field, which this read-only adapter leaves empty.</summary>
        [JsonPropertyName("user_uuid")]
        public string UserUuid { get; init; } = string.Empty;

        /// <summary>Gets the public Atlas location name.</summary>
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        /// <summary>Gets the native-dimension block X coordinate encoded as legacy JSON text.</summary>
        [JsonPropertyName("x")]
        public string X { get; init; } = string.Empty;

        /// <summary>Gets the Minecraft elevation encoded as legacy JSON text.</summary>
        [JsonPropertyName("y")]
        public string Y { get; init; } = string.Empty;

        /// <summary>Gets the native-dimension block Z coordinate encoded as legacy JSON text.</summary>
        [JsonPropertyName("z")]
        public string Z { get; init; } = string.Empty;

        /// <summary>Gets the public historical description, normalized to an empty string when absent.</summary>
        [JsonPropertyName("description")]
        public string Description { get; init; } = string.Empty;

        /// <summary>Gets the legacy free-form tag text used for location discovery.</summary>
        [JsonPropertyName("tags")]
        public string Tags { get; init; } = string.Empty;

        /// <summary>Gets the persisted location creation timestamp in its historical text representation.</summary>
        [JsonPropertyName("time_added")]
        public string TimeAdded { get; init; } = string.Empty;

        /// <summary>Gets the optional public 2b2t wiki reference, normalized to an empty string.</summary>
        [JsonPropertyName("wiki")]
        public string Wiki { get; init; } = string.Empty;

        /// <summary>Gets the optional public video reference, normalized to an empty string.</summary>
        [JsonPropertyName("video_url")]
        public string VideoUrl { get; init; } = string.Empty;

        /// <summary>Gets 1 for End locations and 0 for all other dimensions, matching the legacy contract.</summary>
        [JsonPropertyName("end_dimension")]
        public int EndDimension { get; init; }

        /// <summary>Gets associated public warp names, or <see langword="null"/> when none exist.</summary>
        [JsonPropertyName("warps")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<LegacyWarp>? Warps { get; init; }
    }

    /// <summary>Represents a warp name in the minimal legacy nested payload.</summary>
    /// <param name="Name">The historical public warp command name.</param>
    public sealed record LegacyWarp([property: JsonPropertyName("name")] string Name);
}
