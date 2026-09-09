using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Atlas;
using Atlas.Auth;
using Atlas.Enrichment;
using _2b2tAtlas.Server.Models;
using _2b2tAtlas.Server.Services.AiEnrichment;

namespace _2b2tAtlas.Server.Controllers;

/// <summary>
/// Exposes the local AI wiki-enrichment engine to location editors. It returns a reviewable suggestion
/// (matched wiki page plus a drafted description) for a location without persisting anything, so an editor
/// can inspect the evidence — coordinate agreement and confidence — before saving. Applying a suggestion is
/// done through the normal location edit and revision flow.
/// </summary>
[ApiController]
[Route("api/locations")]
public class EnrichmentController : ControllerBase
{
    private readonly AtlasContext _context;
    private readonly AiEnrichmentService _enrichment;
    private readonly GroupEvidenceIndexService _groupEvidence;

    /// <summary>Initializes the enrichment endpoint.</summary>
    /// <param name="context">The Atlas context used to load the target location.</param>
    /// <param name="enrichment">The local AI enrichment engine.</param>
    /// <param name="groupEvidence">The revision-pinned group/build evidence index.</param>
    public EnrichmentController(
        AtlasContext context,
        AiEnrichmentService enrichment,
        GroupEvidenceIndexService groupEvidence)
    {
        _context = context;
        _enrichment = enrichment;
        _groupEvidence = groupEvidence;
    }

    /// <summary>
    /// Generates a wiki match and drafted description for a location. Nothing is saved; the returned
    /// suggestion is for review. Requires the location edit permission.
    /// </summary>
    /// <param name="id">The location row id to enrich.</param>
    /// <param name="regenerateDescription">When true, drafts a description even if one already exists.</param>
    /// <param name="cancellationToken">Token that cancels the request.</param>
    /// <returns>The enrichment suggestion, 404 when the location is missing, or 503 when the engine is unavailable.</returns>
    [HttpPost("{id:int}/enrich")]
    [Authorize(Policy = Permissions.LocationsEdit)]
    public async Task<ActionResult<WikiEnrichmentSuggestion>> Enrich(
        int id,
        [FromQuery] bool regenerateDescription = false,
        CancellationToken cancellationToken = default)
    {
        if (!_enrichment.IsAvailable)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "AI enrichment is disabled or paused.");

        var entity = await _context.Locations.AsNoTracking()
            .Include(location => location.LocationGroups).ThenInclude(link => link.Group)
            .FirstOrDefaultAsync(l => l.Rowid == id, cancellationToken);
        if (entity is null)
            return NotFound();

        var location = new Atlas.Location
        {
            Rowid = entity.Rowid,
            Name = entity.Name ?? string.Empty,
            Description = entity.Description,
            X = entity.X,
            Y = entity.Y,
            Z = entity.Z,
            Dimension = entity.Dimension,
            Wiki = entity.Wiki,
            Groups = entity.LocationGroups.Select(link => new LocationGroupAttribution
            {
                GroupId = link.GroupId,
                GroupName = link.Group.Name,
                Role = link.Role,
            }).ToList(),
        };

        var knownGroups = await _context.Groups.AsNoTracking().ToListAsync(cancellationToken);
        var groupSuggestions = _groupEvidence.FindMatches(location, knownGroups);
        var suggestion = await _enrichment.EnrichAsync(
            location, regenerateDescription, cancellationToken, groupSuggestions);
        return Ok(suggestion);
    }
}
