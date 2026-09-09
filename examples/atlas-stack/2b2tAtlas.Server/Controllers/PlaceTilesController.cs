using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;

namespace _2b2tAtlas.Server.Controllers;

/// <summary>
/// Reverse-proxy for 2b2t.place live map tiles (used with explicit permission from
/// the 2b2t.place team). Serving them from our own origin lets Cloudflare cache them
/// at the edge — offloading 2b2t.place's bandwidth and avoiding browser CORS/taint.
/// The client resamples these onto our CRS grid (see atlas-map.js).
/// </summary>
[ApiController]
[Route("tiles/place")]
public class PlaceTilesController : ControllerBase
{
    private const string Upstream = "https://2b2t.place";
    private const string AtlasCache = "http://127.0.0.1:5297/tiles/place";
    private const string ParentLookupHeader = "X-Atlas-Place-Parent-Lookup";
    private static readonly HashSet<string> AllowedLayers = new(StringComparer.Ordinal) { "base", "overlay", "newchunks" };
    private static readonly Regex TileFile = new(
        @"^t\.(?<tx>-?\d{1,9})\.(?<ty>-?\d{1,9})\.webp$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<PlaceTilesController> _logger;

    /// <summary>Initializes the validated and cache-aware 2b2t.place tile proxy.</summary>
    /// <param name="httpFactory">Factory used to create bounded-lifetime upstream HTTP clients.</param>
    /// <param name="logger">Logger for upstream failures without exposing internal exceptions to callers.</param>
    public PlaceTilesController(IHttpClientFactory httpFactory, ILogger<PlaceTilesController> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    /// <summary>
    /// GET /tiles/place/{layer}/{lod}/{dim}/{sx}/{sy}/t.{tx}.{ty}.webp — proxies the
    /// matching 2b2t.place tile. All path components are validated so the upstream URL
    /// is built only from known-safe values (no SSRF / path traversal).
    /// </summary>
    [HttpGet("{layer}/{lod:int}/{dim:int}/{sx:int}/{sy:int}/{file}")]
    public async Task<IActionResult> Get(string layer, int lod, int dim, int sx, int sy, string file, CancellationToken ct)
    {
        if (!AllowedLayers.Contains(layer)) return BadRequest("Unknown layer.");
        if (lod < 0 || lod > 10) return BadRequest("LOD out of range.");
        if (dim < 0 || dim > 2) return BadRequest("Dimension out of range.");
        var tileMatch = string.IsNullOrEmpty(file) ? null : TileFile.Match(file);
        if (tileMatch is not { Success: true } ||
            !int.TryParse(tileMatch.Groups["tx"].Value, out var tx) ||
            !int.TryParse(tileMatch.Groups["ty"].Value, out var ty))
            return BadRequest("Bad tile name.");

        try
        {
            var client = _httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            var parentLookupOnly = Request.Headers.ContainsKey(ParentLookupHeader);
            for (var sourceLod = lod; sourceLod <= 10; sourceLod++)
            {
                var sourceSx = (int)Math.Truncate(tx / 32d);
                var sourceSy = (int)Math.Truncate(ty / 32d);
                var sourceFile = $"t.{tx}.{ty}.webp";
                var url = sourceLod == lod
                    ? $"{Upstream}/tiles/{layer}/{sourceLod}/{dim}/{sourceSx}/{sourceSy}/{sourceFile}"
                    : $"{AtlasCache}/{layer}/{sourceLod}/{dim}/{sourceSx}/{sourceSy}/{sourceFile}";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.ParseAdd("2b2tAtlas/1.0 (+https://atlas.example)");
                if (sourceLod != lod) req.Headers.TryAddWithoutValidation(ParentLookupHeader, "1");

                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (resp.IsSuccessStatusCode)
                {
                    var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                    var isFallback = sourceLod != lod;
                    var effectiveLod = ReadIntHeader(resp, "X-Atlas-Place-Lod") ?? sourceLod;
                    var effectiveTx = ReadIntHeader(resp, "X-Atlas-Place-Tx") ?? tx;
                    var effectiveTy = ReadIntHeader(resp, "X-Atlas-Place-Ty") ?? ty;
                    if (isFallback)
                    {
                        Response.Headers.CacheControl = "no-store";
                        Response.Headers["CDN-Cache-Control"] = "public, max-age=900";
                        Response.Headers["Cloudflare-CDN-Cache-Control"] = "public, max-age=900";
                    }
                    else
                        Response.Headers.CacheControl = "public, max-age=86400, s-maxage=604800";
                    Response.Headers["X-Atlas-Place-Lod"] = effectiveLod.ToString();
                    Response.Headers["X-Atlas-Place-Tx"] = effectiveTx.ToString();
                    Response.Headers["X-Atlas-Place-Ty"] = effectiveTy.ToString();
                    Response.Headers.AccessControlExposeHeaders =
                        "X-Atlas-Place-Lod, X-Atlas-Place-Tx, X-Atlas-Place-Ty";
                    var contentType = resp.Content.Headers.ContentType?.MediaType ?? "image/webp";
                    return File(bytes, contentType);
                }

                if (parentLookupOnly) break;
                if (sourceLod == 10) break;
                tx = FloorDivide(tx, 2);
                ty = FloorDivide(ty, 2);
            }
            Response.Headers.CacheControl = "no-store";
            Response.Headers["CDN-Cache-Control"] = "public, max-age=120";
            Response.Headers["Cloudflare-CDN-Cache-Control"] = "public, max-age=120";
            return NoContent();
        }
        catch (OperationCanceledException)
        {
            return new StatusCodeResult(499); // client closed request
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Place tile proxy failed for {Layer}/{Lod}/{Dimension}/{File}", layer, lod, dim, file);
            return StatusCode(StatusCodes.Status502BadGateway);
        }
    }

    private static int FloorDivide(int value, int divisor) =>
        checked((int)Math.Floor(value / (double)divisor));

    private static int? ReadIntHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) &&
        int.TryParse(values.FirstOrDefault(), out var value)
            ? value
            : null;
}
