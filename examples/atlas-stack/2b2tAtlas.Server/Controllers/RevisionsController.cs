using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Atlas;
using Atlas.Auth;
using _2b2tAtlas.Server.Models;
using _2b2tAtlas.Server.Services;
using ServerHighway = _2b2tAtlas.Server.Models.Highway;
using ServerLocation = _2b2tAtlas.Server.Models.Location;

namespace _2b2tAtlas.Server.Controllers;

/// <summary>
/// Proposed edits to existing Locations/Highways (GAMEPLAN §15). Contributors who
/// lack direct edit rights submit a revision; a moderator approves (applies the
/// payload + audits) or rejects. Direct editors can still edit inline — this is the
/// trust-gated path for adversarial/community submissions.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class RevisionsController : ControllerBase
{
    private const string TypeLocation = "Location";
    private const string TypeHighway = "Highway";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly AtlasContext _context;
    private readonly AuditService _audit;
    private readonly ILogger<RevisionsController> _logger;

    /// <summary>Initializes the contributor-to-moderator revision workflow.</summary>
    /// <param name="context">The Atlas context used to persist proposals and apply approved entity changes.</param>
    /// <param name="audit">The append-only recorder for submissions and moderation decisions.</param>
    /// <param name="logger">The diagnostic logger for revision application failures.</param>
    public RevisionsController(AtlasContext context, AuditService audit, ILogger<RevisionsController> logger)
    {
        _context = context;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>POST /api/revisions — submit a proposed edit to an existing entity.</summary>
    [HttpPost]
    [Authorize]
    public async Task<ActionResult<RevisionDto>> Submit([FromBody] RevisionDto dto)
    {
        if (dto == null) return BadRequest("Request body is required");

        var entityType = NormalizeEntityType(dto.EntityType);
        if (entityType == null) return BadRequest("EntityType must be 'Location' or 'Highway'.");
        if (string.IsNullOrWhiteSpace(dto.ProposedJson)) return BadRequest("ProposedJson is required.");

        // Must at least be able to contribute to this entity type.
        var createPerm = entityType == TypeLocation ? Permissions.LocationsCreate : Permissions.HighwaysCreate;
        var editPerm = entityType == TypeLocation ? Permissions.LocationsEdit : Permissions.HighwaysEdit;
        if (!HasPermission(createPerm) && !HasPermission(editPerm))
            return Forbid();

        // Target must exist, and the proposed payload must be valid for the entity type.
        if (!await TargetExistsAsync(entityType, dto.EntityId))
            return NotFound($"{entityType} {dto.EntityId} not found.");
        if (!TryValidateProposed(entityType, dto.ProposedJson, out var validationError))
            return BadRequest(validationError);

        var revision = new Revision
        {
            EntityType = entityType,
            EntityId = dto.EntityId,
            ProposedJson = dto.ProposedJson,
            Note = dto.Note,
            Status = "Pending",
            SubmittedByUserId = CurrentUserId(),
            SubmittedByUsername = CurrentUsername(),
            SubmittedUtc = DateTime.UtcNow.ToString("o"),
        };

        _context.Revisions.Add(revision);
        await _context.SaveChangesAsync();
        await _audit.LogAsync("revision.submit", entityType, dto.EntityId, CurrentUserId(), CurrentUsername(),
            $"Proposed edit to {entityType} {dto.EntityId} (revision {revision.Id})");

        return CreatedAtAction(nameof(GetPending), new { id = revision.Id }, MapToDto(revision));
    }

    /// <summary>GET /api/revisions/pending — proposed edits awaiting review.</summary>
    [HttpGet("pending")]
    [Authorize(Policy = Permissions.SubmissionsModerate)]
    public async Task<ActionResult<IEnumerable<RevisionDto>>> GetPending()
    {
        // AI-enrichment revisions are managed on the dedicated enrichment surface, not this queue.
        var rows = await _context.Revisions
            .Where(r => r.Status == "Pending" && (r.Source == null || r.Source != "AI"))
            .OrderBy(r => r.Id)
            .ToListAsync();
        return Ok(rows.Select(MapToDto).ToList());
    }

    /// <summary>GET /api/revisions — all revisions (optionally filtered by status).</summary>
    [HttpGet]
    [Authorize(Policy = Permissions.SubmissionsModerate)]
    public async Task<ActionResult<IEnumerable<RevisionDto>>> GetAll([FromQuery] string? status)
    {
        var query = _context.Revisions.AsQueryable();
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(r => r.Status == status);
        var rows = await query.OrderByDescending(r => r.Id).Take(500).ToListAsync();
        return Ok(rows.Select(MapToDto).ToList());
    }

    /// <summary>POST /api/revisions/{id}/approve — apply the proposed edit and audit it.</summary>
    [HttpPost("{id:int}/approve")]
    [Authorize(Policy = Permissions.SubmissionsModerate)]
    public async Task<IActionResult> Approve(int id)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync();
        var revision = await _context.Revisions.FindAsync(id);
        if (revision == null) return NotFound();
        if (revision.Status != "Pending") return BadRequest("Revision is not pending.");

        if (revision.EntityType == TypeHighway)
        {
            Atlas.Highway? proposed;
            try { proposed = JsonSerializer.Deserialize<Atlas.Highway>(revision.ProposedJson, JsonOptions); }
            catch (JsonException) { return BadRequest("Invalid highway proposal."); }
            if (proposed is null) return BadRequest("Invalid highway proposal.");
            var editor = new HighwaysController(_context, _audit, Microsoft.Extensions.Logging.Abstractions.NullLogger<HighwaysController>.Instance)
            { ControllerContext = ControllerContext };
            var result = await editor.UpdateHighway(revision.EntityId, proposed);
            if (result.Result is not OkObjectResult)
            {
                await transaction.CommitAsync(); // retain blocked-attempt audit records
                return result.Result!;
            }
        }

        var applied = revision.EntityType switch
        {
            TypeLocation => await ApplyLocationRevisionAsync(revision),
            TypeHighway => true,
            _ => false,
        };

        if (!applied) return NotFound($"{revision.EntityType} {revision.EntityId} no longer exists.");

        revision.Status = "Approved";
        revision.ReviewedByUserId = CurrentUserId();
        revision.ReviewedUtc = DateTime.UtcNow.ToString("o");
        await _context.SaveChangesAsync();

        await _audit.LogAsync("revision.approve", revision.EntityType, revision.EntityId, CurrentUserId(), CurrentUsername(),
            $"Approved revision {revision.Id} for {revision.EntityType} {revision.EntityId}");
        await transaction.CommitAsync();
        return Ok(MapToDto(revision));
    }

    /// <summary>POST /api/revisions/{id}/reject — discard the proposed edit.</summary>
    [HttpPost("{id:int}/reject")]
    [Authorize(Policy = Permissions.SubmissionsModerate)]
    public async Task<IActionResult> Reject(int id, [FromBody] RejectRequest? body)
    {
        var revision = await _context.Revisions.FindAsync(id);
        if (revision == null) return NotFound();
        if (revision.Status != "Pending") return BadRequest("Revision is not pending.");

        revision.Status = "Rejected";
        revision.ReviewedByUserId = CurrentUserId();
        revision.ReviewNote = body?.Reason;
        revision.ReviewedUtc = DateTime.UtcNow.ToString("o");
        await _context.SaveChangesAsync();

        await _audit.LogAsync("revision.reject", revision.EntityType, revision.EntityId, CurrentUserId(), CurrentUsername(),
            $"Rejected revision {revision.Id} for {revision.EntityType} {revision.EntityId}");
        return Ok(MapToDto(revision));
    }

    /// <summary>Supplies an optional moderator-facing explanation when a proposal is rejected.</summary>
    public class RejectRequest
    {
        /// <summary>Gets or sets the review note persisted with the rejected revision.</summary>
        public string? Reason { get; set; }
    }

    // ---- apply helpers ----

    private async Task<bool> ApplyLocationRevisionAsync(Revision revision)
    {
        var row = await _context.Locations.FindAsync(revision.EntityId);
        if (row == null) return false;

        var dto = JsonSerializer.Deserialize<Atlas.Location>(revision.ProposedJson, JsonOptions);
        if (dto == null) return false;

        var before = SnapshotLocation(row);
        row.Name = dto.Name;
        row.Description = dto.Description;
        row.X = dto.X;
        row.Y = dto.Y ?? 64;
        row.Z = dto.Z;
        row.Dimension = dto.Dimension;
        row.ModifiedUtc = DateTime.UtcNow.ToString("o");

        var existingWarps = await _context.Warps.Where(w => w.LocationRowid == row.Rowid).ToListAsync();
        _context.Warps.RemoveRange(existingWarps);
        if (dto.Warps?.Any() == true)
        {
            _context.Warps.AddRange(dto.Warps.Select(w => new _2b2tAtlas.Server.Models.Warp
            {
                Name = w.Name,
                LocationRowid = row.Rowid,
                TimeAdded = w.TimeAdded.ToString("o"),
                WarpUuid = w.WarpUuid,
                LocationUuidFk = w.LocationUuidFk,
            }));
        }

        await _audit.LogDiffAsync("location.update", "Location", row.Rowid, CurrentUserId(), CurrentUsername(),
            $"Applied revision {revision.Id} to '{row.Name}'", before, SnapshotLocation(row), save: false);
        return true;
    }

    // ---- validation helpers ----

    private async Task<bool> TargetExistsAsync(string entityType, int entityId) => entityType switch
    {
        TypeLocation => await _context.Locations.AnyAsync(l => l.Rowid == entityId),
        TypeHighway => await _context.Highways.AnyAsync(h => h.Id == entityId),
        _ => false,
    };

    private static bool TryValidateProposed(string entityType, string proposedJson, out string? error)
    {
        error = null;
        try
        {
            if (entityType == TypeLocation)
            {
                var loc = JsonSerializer.Deserialize<Atlas.Location>(proposedJson, JsonOptions);
                if (loc == null) { error = "Proposed location payload is invalid."; return false; }
            }
            else
            {
                var hw = JsonSerializer.Deserialize<Atlas.Highway>(proposedJson, JsonOptions);
                if (hw == null || string.IsNullOrWhiteSpace(hw.Name)) { error = "Proposed highway payload is invalid."; return false; }
            }
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Proposed payload is not valid JSON: {ex.Message}";
            return false;
        }
    }

    private static string? NormalizeEntityType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Trim().ToLowerInvariant() switch
        {
            "location" => TypeLocation,
            "highway" => TypeHighway,
            _ => null,
        };
    }

    private static Dictionary<string, object?> SnapshotLocation(ServerLocation loc) => new()
    {
        ["Name"] = loc.Name,
        ["Description"] = loc.Description,
        ["X"] = loc.X,
        ["Y"] = loc.Y,
        ["Z"] = loc.Z,
        ["Dimension"] = loc.Dimension,
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

    private bool HasPermission(string permission) =>
        User.HasClaim("superadmin", "true") || User.HasClaim("perm", permission);
}
