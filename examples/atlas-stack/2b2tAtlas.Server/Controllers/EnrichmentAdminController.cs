using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Atlas;
using Atlas.Auth;
using Atlas.Enrichment;
using _2b2tAtlas.Server.Models;
using _2b2tAtlas.Server.Services;
using _2b2tAtlas.Server.Services.AiEnrichment;
using ServerLocation = _2b2tAtlas.Server.Models.Location;

namespace _2b2tAtlas.Server.Controllers;

/// <summary>
/// Admin surface for the local AI wiki-enrichment engine. It lets moderators run a batch match/describe
/// pass over locations, review the resulting suggestions, and either keep or revert auto-applied changes.
/// Every change is recorded as a <see cref="Revision"/> tagged <c>Source = "AI"</c> so it is auditable and
/// reversible: confident, coordinate-confirmed matches are auto-applied (status <c>Applied</c>) but stay in
/// the review queue, while weaker matches are queued (<c>Pending</c>) without touching the location.
/// Enrichment only ever fills a blank wiki link or description — it never overwrites human-entered text and
/// never changes coordinates.
/// </summary>
[ApiController]
[Route("api/enrichment")]
[Authorize(Policy = Permissions.SubmissionsModerate)]
public class EnrichmentAdminController : ControllerBase
{
    private const string TypeLocation = "Location";
    private const string TypeGroup = "Group";
    private const string SourceAi = "AI";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly AtlasContext _context;
    private readonly AiEnrichmentService _enrichment;
    private readonly EnrichmentRunCoordinator _runCoordinator;
    private readonly GroupEvidenceIndexService _groupEvidence;
    private readonly AiEnrichmentOptions _options;
    private readonly AuditService _audit;
    private readonly ILogger<EnrichmentAdminController> _logger;

    /// <summary>Initializes the enrichment admin controller.</summary>
    /// <param name="context">The Atlas context for locations and revisions.</param>
    /// <param name="enrichment">The local AI enrichment engine.</param>
    /// <param name="runCoordinator">The singleton owner of catalog-wide background runs.</param>
    /// <param name="groupEvidence">The revision-pinned group/build evidence index.</param>
    /// <param name="options">The bound enrichment options.</param>
    /// <param name="audit">The append-only audit recorder.</param>
    /// <param name="logger">The diagnostic logger.</param>
    public EnrichmentAdminController(
        AtlasContext context,
        AiEnrichmentService enrichment,
        EnrichmentRunCoordinator runCoordinator,
        GroupEvidenceIndexService groupEvidence,
        IOptions<AiEnrichmentOptions> options,
        AuditService audit,
        ILogger<EnrichmentAdminController> logger)
    {
        _context = context;
        _enrichment = enrichment;
        _runCoordinator = runCoordinator;
        _groupEvidence = groupEvidence;
        _options = options.Value;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>GET /api/enrichment/status — current engine state and outstanding work counts.</summary>
    /// <param name="cancellationToken">Token that cancels the request.</param>
    /// <returns>The engine status snapshot.</returns>
    [HttpGet("status")]
    public async Task<ActionResult<EnrichmentStatusDto>> GetStatus(CancellationToken cancellationToken)
    {
        var missing = await _context.Locations
            .CountAsync(l => l.Wiki == null || l.Wiki == "" || l.Description == null || l.Description == "", cancellationToken);
        var pendingReview = await _context.Revisions
            .CountAsync(r => r.Source == SourceAi && (r.Status == "Applied" || r.Status == "Pending"), cancellationToken);
        var knownGroups = await _context.Groups.AsNoTracking().ToListAsync(cancellationToken);
        var candidatePairs = _groupEvidence.GetCandidatePairs(knownGroups);
        var existingPairs = (await _context.LocationGroups.AsNoTracking()
                .Select(link => new { link.LocationRowid, link.GroupId }).ToListAsync(cancellationToken))
            .Select(link => new GroupEvidenceCandidate(link.LocationRowid, link.GroupId))
            .ToHashSet();
        var missingGroupLocations = candidatePairs.Except(existingPairs)
            .Select(pair => pair.LocationId).Distinct().Count();
        var discoveryCandidates = _groupEvidence.GetGroupDiscoveryCandidates(knownGroups).Count;

        var groupEvidence = _groupEvidence.GetStatus();
        return Ok(new EnrichmentStatusDto
        {
            Run = _runCoordinator.GetStatus(),
            Enabled = _options.Enabled,
            Paused = _options.Enabled && System.IO.File.Exists(_options.GameModeLockPath),
            Model = _options.Model,
            WikiApiBase = _options.WikiApiBase,
            AutoApplyMinConfidence = _options.AutoApplyMinConfidence,
            CoordinateToleranceBlocks = _options.CoordinateToleranceBlocks,
            LocationsMissingMetadata = missing,
            LocationsMissingGroupAttributions = missingGroupLocations,
            UnrepresentedGroupCandidates = discoveryCandidates,
            PendingReviewCount = pendingReview,
            GroupEvidenceAvailable = groupEvidence.Available,
            GroupEvidenceGeneratedUtc = groupEvidence.GeneratedUtc,
            GroupEvidenceArticleCount = groupEvidence.ArticleCount,
            GroupEvidenceMessage = groupEvidence.Message,
        });
    }

    /// <summary>GET /api/enrichment/revisions — AI enrichment revisions, optionally filtered by status.</summary>
    /// <param name="status">Optional status filter (Applied/Pending/Approved/Rejected/Reverted).</param>
    /// <param name="cancellationToken">Token that cancels the request.</param>
    /// <returns>The matching AI revisions, newest first.</returns>
    [HttpGet("revisions")]
    public async Task<ActionResult<IEnumerable<RevisionDto>>> GetRevisions(
        [FromQuery] string? status,
        CancellationToken cancellationToken)
    {
        var query = _context.Revisions.Where(r => r.Source == SourceAi);
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(r => r.Status == status);
        else
            query = query.Where(r => r.Status == "Applied" || r.Status == "Pending");

        var rows = await query.OrderByDescending(r => r.Id).Take(500).ToListAsync(cancellationToken);
        return Ok(rows.Select(MapToDto).ToList());
    }

    /// <summary>POST /api/enrichment/run — match and describe every eligible location.</summary>
    /// <param name="request">The run options; a null body uses defaults.</param>
    /// <returns>The new server-owned run, or the already-active run.</returns>
    [HttpPost("run")]
    public ActionResult<EnrichmentRunStatusDto> Run([FromBody] EnrichmentRunRequest? request)
    {
        request ??= new EnrichmentRunRequest();
        if (!_enrichment.IsAvailable)
        {
            return Conflict(new EnrichmentRunStatusDto
            {
                State = "unavailable",
                Message = _options.Enabled
                    ? "Paused: the GPU game-mode lock is present."
                    : "Disabled: set AiEnrichment:Enabled to true to use enrichment.",
            });
        }

        var userId = CurrentUserId();
        var username = CurrentUsername();
        if (!_runCoordinator.TryStart(request.AutoApply, userId, username, out var status))
            return Conflict(status);

        return Accepted(status);
    }

    /// <summary>
    /// POST /api/enrichment/groups/run — creates review-only proposals for unrepresented wiki groups that
    /// explicitly name an existing Atlas build. The model drafts prose, but never creates a group directly.
    /// </summary>
    [HttpPost("groups/run")]
    public async Task<ActionResult<GroupDiscoveryRunSummary>> RunGroupDiscovery(
        [FromBody] GroupDiscoveryRunRequest? request,
        CancellationToken cancellationToken)
    {
        request ??= new GroupDiscoveryRunRequest();
        if (_runCoordinator.GetStatus().IsRunning)
        {
            return Conflict(new GroupDiscoveryRunSummary
            {
                Ran = false,
                Message = "A catalog-wide location enrichment run is already active. Wait for it to finish.",
            });
        }
        if (request.DraftDescriptions && !_enrichment.IsAvailable)
        {
            return Ok(new GroupDiscoveryRunSummary
            {
                Ran = false,
                Message = _options.Enabled
                    ? "Paused: the GPU game-mode lock is present."
                    : "Disabled: enable AI enrichment to draft group descriptions.",
            });
        }

        var groups = await _context.Groups.AsNoTracking().ToListAsync(cancellationToken);
        var candidates = _groupEvidence.GetGroupDiscoveryCandidates(groups)
            .Take(Math.Clamp(request.MaxGroups, 1, 50)).ToArray();
        var summary = new GroupDiscoveryRunSummary { Ran = true, Candidates = candidates.Length };
        foreach (var candidate in candidates)
        {
            var revisionEntityId = -candidate.PageId;
            var open = await _context.Revisions.AnyAsync(revision =>
                revision.Source == SourceAi && revision.EntityType == TypeGroup &&
                revision.EntityId == revisionEntityId &&
                (revision.Status == "Pending" || revision.Status == "Applied"), cancellationToken);
            if (open)
            {
                summary.Skipped++;
                continue;
            }

            candidate.SuggestedDescription = request.DraftDescriptions
                ? await _enrichment.DraftGroupDescriptionAsync(candidate, cancellationToken)
                : null;
            var proposal = new GroupDiscoveryProposal
            {
                Group = new Atlas.Group
                {
                    Name = candidate.Name,
                    Type = candidate.Type,
                    Description = candidate.SuggestedDescription,
                    WikiUrl = candidate.WikiUrl,
                    // The wiki audit can surface outbound links, but it cannot prove that an
                    // arbitrary external link is still the group's official site or Discord.
                    // Keep those fields empty until a moderator adds a verified URL.
                    WebsiteUrl = null,
                    DiscordUrl = null,
                    LogoUrl = candidate.LogoUrl,
                    LogoSourceUrl = candidate.LogoSourceUrl,
                    Founded = candidate.Founded,
                    Status = candidate.Status,
                },
                Locations = candidate.Locations,
                EvidenceUrl = candidate.WikiUrl,
                SourceRevisionId = candidate.SourceRevisionId,
            };
            _context.Revisions.Add(new Revision
            {
                EntityType = TypeGroup,
                EntityId = revisionEntityId,
                Source = SourceAi,
                Confidence = 0.75,
                ProposedJson = JsonSerializer.Serialize(proposal, JsonOptions),
                Note = $"Unrepresented group with {candidate.Locations.Count} explicit Atlas build match(es); " +
                       $"source revision {candidate.SourceRevisionId}; {candidate.WikiUrl}. Verify identity, " +
                       "classification, public links, logo license, and every build before applying.",
                Status = "Pending",
                SubmittedByUserId = CurrentUserId(),
                SubmittedByUsername = "AI Group Discovery",
                SubmittedUtc = DateTime.UtcNow.ToString("o"),
            });
            summary.Queued++;
        }

        await _context.SaveChangesAsync(cancellationToken);
        await _audit.LogAsync("enrichment.groups.run", TypeGroup, 0, CurrentUserId(), CurrentUsername(),
            $"Group discovery: candidates {summary.Candidates}, queued {summary.Queued}, skipped {summary.Skipped}");
        return Ok(summary);
    }

    /// <summary>POST /api/enrichment/{id}/apply — apply a queued (Pending) AI suggestion.</summary>
    /// <param name="id">The revision id.</param>
    /// <returns>The updated revision.</returns>
    [HttpPost("{id:int}/apply")]
    public async Task<IActionResult> Apply(int id)
    {
        var revision = await LoadAiRevisionAsync(id);
        if (revision == null) return NotFound();
        if (revision.Status != "Pending") return BadRequest("Only pending suggestions can be applied.");

        var applied = revision.EntityType switch
        {
            TypeLocation => await WriteEnrichmentAsync(revision),
            TypeGroup => await WriteGroupDiscoveryAsync(revision),
            _ => false,
        };
        if (!applied) return BadRequest("The enrichment target is missing, duplicated, or no longer valid.");
        revision.Status = "Approved";
        revision.ReviewedByUserId = CurrentUserId();
        revision.ReviewedUtc = DateTime.UtcNow.ToString("o");
        await _context.SaveChangesAsync();
        return Ok(MapToDto(revision));
    }

    /// <summary>POST /api/enrichment/{id}/keep — accept an already auto-applied (Applied) suggestion.</summary>
    /// <param name="id">The revision id.</param>
    /// <returns>The updated revision.</returns>
    [HttpPost("{id:int}/keep")]
    public async Task<IActionResult> Keep(int id)
    {
        var revision = await LoadAiRevisionAsync(id);
        if (revision == null) return NotFound();
        if (revision.Status != "Applied") return BadRequest("Only auto-applied suggestions can be kept.");

        revision.Status = "Approved";
        revision.ReviewedByUserId = CurrentUserId();
        revision.ReviewedUtc = DateTime.UtcNow.ToString("o");
        await _context.SaveChangesAsync();
        await _audit.LogAsync("enrichment.keep", TypeLocation, revision.EntityId, CurrentUserId(), CurrentUsername(),
            $"Kept AI enrichment revision {revision.Id}");
        return Ok(MapToDto(revision));
    }

    /// <summary>POST /api/enrichment/{id}/reject — discard a queued (Pending) suggestion without applying it.</summary>
    /// <param name="id">The revision id.</param>
    /// <returns>The updated revision.</returns>
    [HttpPost("{id:int}/reject")]
    public async Task<IActionResult> Reject(int id)
    {
        var revision = await LoadAiRevisionAsync(id);
        if (revision == null) return NotFound();
        if (revision.Status != "Pending") return BadRequest("Only pending suggestions can be rejected.");

        revision.Status = "Rejected";
        revision.ReviewedByUserId = CurrentUserId();
        revision.ReviewedUtc = DateTime.UtcNow.ToString("o");
        await _context.SaveChangesAsync();
        await _audit.LogAsync("enrichment.reject", revision.EntityType, revision.EntityId, CurrentUserId(), CurrentUsername(),
            $"Rejected AI enrichment revision {revision.Id}");
        return Ok(MapToDto(revision));
    }

    /// <summary>POST /api/enrichment/{id}/revert — undo an auto-applied (Applied) suggestion.</summary>
    /// <param name="id">The revision id.</param>
    /// <returns>The updated revision.</returns>
    [HttpPost("{id:int}/revert")]
    public async Task<IActionResult> Revert(int id)
    {
        var revision = await LoadAiRevisionAsync(id);
        if (revision == null) return NotFound();
        if (revision.Status != "Applied") return BadRequest("Only auto-applied suggestions can be reverted.");
        if (string.IsNullOrWhiteSpace(revision.PreviousJson)) return BadRequest("No prior value stored to revert to.");

        var row = await _context.Locations
            .Include(location => location.LocationGroups)
            .FirstOrDefaultAsync(location => location.Rowid == revision.EntityId);
        if (row == null) return NotFound($"Location {revision.EntityId} no longer exists.");
        var previous = JsonSerializer.Deserialize<Atlas.Location>(revision.PreviousJson, JsonOptions);
        if (previous == null) return BadRequest("Stored prior value is unreadable.");
        var proposed = JsonSerializer.Deserialize<Atlas.Location>(revision.ProposedJson, JsonOptions);

        var before = Snapshot(row);
        row.Wiki = previous.Wiki;
        row.Description = previous.Description;
        var previousGroupIds = (previous.Groups ?? []).Select(group => group.GroupId).ToHashSet();
        var proposedGroupIds = (proposed?.Groups ?? []).Select(group => group.GroupId).ToHashSet();
        var addedByRevision = proposedGroupIds.Except(previousGroupIds).ToHashSet();
        var linksToRemove = row.LocationGroups.Where(link => addedByRevision.Contains(link.GroupId)).ToList();
        _context.LocationGroups.RemoveRange(linksToRemove);
        row.ModifiedUtc = DateTime.UtcNow.ToString("o");
        revision.Status = "Reverted";
        revision.ReviewedByUserId = CurrentUserId();
        revision.ReviewedUtc = DateTime.UtcNow.ToString("o");
        await _audit.LogDiffAsync("location.enrich.revert", TypeLocation, row.Rowid, CurrentUserId(), CurrentUsername(),
            $"Reverted AI enrichment revision {revision.Id} on '{row.Name}'", before, Snapshot(row), save: false);
        await _context.SaveChangesAsync();
        return Ok(MapToDto(revision));
    }

    // ---- helpers ----

    private async Task<Revision?> LoadAiRevisionAsync(int id)
    {
        var revision = await _context.Revisions.FindAsync(id);
        return revision is { Source: SourceAi } ? revision : null;
    }

    private async Task<bool> WriteEnrichmentAsync(Revision revision)
    {
        var row = await _context.Locations
            .Include(location => location.LocationGroups)
            .FirstOrDefaultAsync(location => location.Rowid == revision.EntityId);
        if (row == null) return false;
        var proposed = JsonSerializer.Deserialize<Atlas.Location>(revision.ProposedJson, JsonOptions);
        if (proposed == null) return false;

        var before = Snapshot(row);
        if (!string.IsNullOrWhiteSpace(proposed.Wiki)) row.Wiki = proposed.Wiki;
        if (!string.IsNullOrWhiteSpace(proposed.Description)) row.Description = proposed.Description;
        var requestedGroups = (proposed.Groups ?? [])
            .Where(group => group.GroupId > 0)
            .GroupBy(group => group.GroupId)
            .Select(group => group.First())
            .ToArray();
        var existingIds = row.LocationGroups.Select(link => link.GroupId).ToHashSet();
        var requestedIds = requestedGroups.Select(group => group.GroupId).ToArray();
        var validIds = requestedIds.Length == 0
            ? new HashSet<int>()
            : (await _context.Groups.Where(group => requestedIds.Contains(group.Id))
                .Select(group => group.Id).ToListAsync()).ToHashSet();
        var now = DateTime.UtcNow.ToString("o");
        foreach (var group in requestedGroups.Where(group =>
                     validIds.Contains(group.GroupId) && !existingIds.Contains(group.GroupId)))
        {
            row.LocationGroups.Add(new LocationGroup
            {
                LocationRowid = row.Rowid,
                GroupId = group.GroupId,
                Role = string.IsNullOrWhiteSpace(group.Role) ? "Builder" : group.Role.Trim(),
                DateAddedUtc = now,
            });
        }
        row.ModifiedUtc = now;
        await _audit.LogDiffAsync("location.enrich", TypeLocation, row.Rowid, CurrentUserId(), CurrentUsername(),
            $"Applied AI enrichment revision {revision.Id} to '{row.Name}'", before, Snapshot(row), save: false);
        return true;
    }

    private async Task<bool> WriteGroupDiscoveryAsync(Revision revision)
    {
        var proposal = JsonSerializer.Deserialize<GroupDiscoveryProposal>(revision.ProposedJson, JsonOptions);
        if (proposal?.Group is null || string.IsNullOrWhiteSpace(proposal.Group.Name))
            return false;
        var normalizedName = NormalizeIdentity(proposal.Group.Name);
        var existingNames = await _context.Groups.AsNoTracking().Select(group => group.Name).ToListAsync();
        if (existingNames.Any(name => NormalizeIdentity(name) == normalizedName))
            return false;

        var now = DateTime.UtcNow.ToString("o");
        var group = new _2b2tAtlas.Server.Models.Group
        {
            Name = proposal.Group.Name.Trim(),
            Type = proposal.Group.Type.ToString(),
            Description = NormalizeOptional(proposal.Group.Description, 2000),
            WikiUrl = SafeHttps(proposal.Group.WikiUrl),
            WebsiteUrl = SafeHttps(proposal.Group.WebsiteUrl),
            DiscordUrl = SafeHttps(proposal.Group.DiscordUrl),
            LogoUrl = SafeHttps(proposal.Group.LogoUrl),
            LogoSourceUrl = SafeHttps(proposal.Group.LogoSourceUrl),
            Founded = NormalizeOptional(proposal.Group.Founded, 200),
            Status = NormalizeOptional(proposal.Group.Status, 100),
            DateAddedUtc = now,
            ModifiedUtc = now,
        };
        _context.Groups.Add(group);
        await _audit.LogAsync("group.discover", TypeGroup, revision.EntityId, CurrentUserId(), CurrentUsername(),
            $"Created reviewed group '{group.Name}' from {proposal.EvidenceUrl} revision {proposal.SourceRevisionId}; " +
            "candidate builds remain subject to the normal location-attribution review pass",
            save: false);
        return true;
    }

    private static string NormalizeIdentity(string value) => new(value.ToLowerInvariant()
        .Where(char.IsLetterOrDigit).ToArray());

    private static string? NormalizeOptional(string? value, int maximumLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }

    private static string? SafeHttps(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri.AbsoluteUri
            : null;

    private static Dictionary<string, object?> Snapshot(ServerLocation l) => new()
    {
        ["Wiki"] = l.Wiki,
        ["Description"] = l.Description,
        ["Groups"] = l.LocationGroups
            .OrderBy(link => link.GroupId)
            .Select(link => new { link.GroupId, link.Role })
            .ToArray(),
    };

    private static RevisionDto MapToDto(Revision r) => new()
    {
        Id = r.Id,
        EntityType = r.EntityType,
        EntityId = r.EntityId,
        ProposedJson = r.ProposedJson,
        Note = r.Note,
        Source = r.Source,
        Confidence = r.Confidence,
        PreviousJson = r.PreviousJson,
        Status = Enum.TryParse<RevisionStatus>(r.Status, ignoreCase: true, out var s) ? s : RevisionStatus.Pending,
        SubmittedByUserId = r.SubmittedByUserId,
        SubmittedByUsername = r.SubmittedByUsername,
        ReviewedByUserId = r.ReviewedByUserId,
        ReviewNote = r.ReviewNote,
        SubmittedUtc = DateTime.TryParse(r.SubmittedUtc, out var sub) ? sub : DateTime.MinValue,
        ReviewedUtc = DateTime.TryParse(r.ReviewedUtc, out var rev) ? rev : null,
    };

    private int? CurrentUserId() =>
        int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var uid) ? uid : null;

    private string? CurrentUsername() => User.FindFirstValue(ClaimTypes.Name);
}
