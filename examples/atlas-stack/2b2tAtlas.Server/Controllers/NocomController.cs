using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using _2b2tAtlas.Server.Services;

namespace _2b2tAtlas.Server.Controllers;

/// <summary>Public historical Nocom aggregate metadata, counts and tile discovery.</summary>
[ApiController, Route("api/nocom"), AllowAnonymous]
[ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Any)]
public sealed class NocomController(NocomDataService data) : ControllerBase
{
    /// <summary>Dataset provenance, observation counts, dimensions, temporal scope, caveats and public tile links.</summary>
    [HttpGet]
    public ActionResult<NocomDataset> Get() => Ok(data.Dataset);

    /// <summary>At most 39 dimension/period aggregates; dates select overlapping fixed 30-day buckets, not exact-day counts.</summary>
    [HttpGet("periods")]
    public ActionResult<IReadOnlyList<NocomPeriod>> Periods(string? dimension = null, DateOnly? from = null, DateOnly? to = null)
    {
        try { return Ok(data.Periods(dimension, from, to)); }
        catch (ArgumentException exception) { return BadRequest(new { message = exception.Message }); }
    }

    /// <summary>Authors' historical highway observation aggregate: at most 136 rows per dimension, or 17 per compass direction. Not unique players or trips.</summary>
    [HttpGet("highways")]
    public ActionResult<IReadOnlyList<NocomHighwayPeriod>> Highways(string dimension = "nether", string? direction = null)
    {
        try { return Ok(data.Highways(dimension, direction)); }
        catch (ArgumentException exception) { return BadRequest(new { message = exception.Message }); }
    }
}
