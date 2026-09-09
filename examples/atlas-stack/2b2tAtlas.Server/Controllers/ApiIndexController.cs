using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace _2b2tAtlas.Server.Controllers;

/// <summary>
/// A small public discovery index at <c>/api</c> for people building external tools against the Atlas API.
/// It points at the machine-readable OpenAPI document and lists the main public read endpoints with a short
/// description of each, so a newcomer can orient without prior knowledge of the route layout.
/// </summary>
[ApiController]
public sealed class ApiIndexController : ControllerBase
{
    /// <summary>GET /api — a discovery index of public endpoints and the OpenAPI document location.</summary>
    /// <returns>A static description of the public API surface.</returns>
    [HttpGet("api")]
    [AllowAnonymous]
    [ResponseCache(Duration = 300, Location = ResponseCacheLocation.Any)]
    public IActionResult GetIndex() => Ok(new
    {
        service = "Atlas Example API",
        description = "Read-only public data for the 2b2t Atlas: locations, Archive warps, bounded world downloads, sourced media, per-location renders, dimension map layers, highways, groups, and their reviewed relationships. CORS is open; write routes require a bearer token and an explicit permission.",
        openApi = "/openapi/v1.json",
        mcp = new
        {
            endpoint = "http://127.0.0.1:5297/mcp",
            transport = "Streamable HTTP",
            sessionMode = "stateless",
            access = "public read-only",
            documentation = "https://atlas.example/mcp/",
        },
        endpoints = new object[]
        {
            new { method = "GET", path = "/api/nocom", description = "Historical Nocom World Pulse provenance, per-dimension counts, coverage, caveats and tile links." },
            new { method = "GET", path = "/api/nocom/periods", description = "Fixed 30-day observation aggregates; optional dimension and overlapping from/to date filters." },
            new { method = "GET", path = "/api/nocom/highways", description = "Released highway observation counts by native dimension and compass direction; not player or trip counts." },
            new { method = "GET", path = "/api/locations", description = "All locations with warps, attachments, and renders." },
            new { method = "GET", path = "/api/locations/{id}", description = "A single location." },
            new { method = "GET", path = "/api/warps", description = "Archive warp records with owning-location links. Filter: locationId; paging: limit, offset." },
            new { method = "GET", path = "/api/warps/{id}", description = "One Archive warp with its owning location and provenance." },
            new { method = "GET", path = "/api/warps/{id}/world-download", description = "Metadata, bounds, digest, size, and partial-world warning for one collector WDL." },
            new { method = "GET", path = "/api/warps/{id}/world-download.zip", description = "The immutable bounded Minecraft Java world ZIP; supports byte-range downloads." },
            new { method = "GET", path = "/api/locations/{id}/renders", description = "The base renders attached to a location." },
            new { method = "GET", path = "/api/attachments", description = "Sourced media and reference links. Filters: locationId, mediaType; paging: limit, offset." },
            new { method = "GET", path = "/api/attachments/{id}", description = "One attachment with owning-location context and provenance." },
            new { method = "GET", path = "/api/renders", description = "All per-location base renders. Filters: locationId, dimension, scale; paging: limit, offset." },
            new { method = "GET", path = "/api/renders/{id}", description = "A single per-location render with its owning location." },
            new { method = "GET", path = "/api/maprenders", description = "Published dimension-level primary map layers (registry; may be empty)." },
            new { method = "GET", path = "/api/maprenders/catalog", description = "Combined catalog: primary layers plus every per-location render." },
            new { method = "GET", path = "/api/highways", description = "Public approved highways." },
            new { method = "GET", path = "/api/highways/{id}", description = "One public approved highway or canal segment." },
            new { method = "GET", path = "/api/groups", description = "Builder and infrastructure groups with verified public links and attribution counts." },
            new { method = "GET", path = "/api/groups/{id}", description = "One group with its attributed locations and highways." },
        },
    });
}
