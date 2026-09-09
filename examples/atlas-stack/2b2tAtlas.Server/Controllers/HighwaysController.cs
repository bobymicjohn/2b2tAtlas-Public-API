using System.Security.Claims;
using System.Text.Json;
using Atlas;
using Atlas.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using _2b2tAtlas.Server.Models;
using _2b2tAtlas.Server.Services;
using ServerHighway = _2b2tAtlas.Server.Models.Highway;

namespace _2b2tAtlas.Server.Controllers;

/// <summary>Public highway reads and bounded, attributable editor mutations.</summary>
[ApiController]
[Route("api/[controller]")]
public class HighwaysController(AtlasContext context, AuditService audit, ILogger<HighwaysController> logger) : ControllerBase
{
    private IQueryable<ServerHighway> Rows => context.Highways.Include(h => h.HighwayGroups).ThenInclude(g => g.Group);
    private int? UserId => int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
    private string? Username => User.FindFirstValue(ClaimTypes.Name);
    private bool Owner => AtlasSessionValidator.IsOwner(User);
    private bool Can(string permission) => User.HasClaim("superadmin", "true") || User.HasClaim("perm", permission);

    /// <summary>All public, approved highways.</summary>
    [HttpGet, AllowAnonymous]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any, VaryByHeader = "Origin")]
    public async Task<ActionResult<IEnumerable<Atlas.Highway>>> GetHighways() =>
        Ok((await Rows.AsNoTracking().Where(h => h.Visibility == "Public" && h.ReviewStatus == "Approved").OrderBy(h => h.Id).ToListAsync()).Select(HighwayMapping.ToDto));

    /// <summary>A highway and the version required to edit it.</summary>
    [HttpGet("{id:int}"), AllowAnonymous]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<Atlas.Highway>> GetHighway(int id)
    {
        var row = await Rows.AsNoTracking().SingleOrDefaultAsync(h => h.Id == id);
        if (row is null || ((row.Visibility != "Public" || row.ReviewStatus != "Approved") && !Can(Permissions.HighwaysEdit) && !Can(Permissions.SubmissionsModerate))) return NotFound();
        return Ok(HighwayMapping.ToDto(row));
    }

    /// <summary>Every highway for authorized highway editors.</summary>
    [HttpGet("all"), Authorize(Policy = Permissions.HighwaysEdit)]
    public async Task<ActionResult<IEnumerable<Atlas.Highway>>> GetAll() => Ok((await Rows.AsNoTracking().OrderBy(h => h.Id).ToListAsync()).Select(HighwayMapping.ToDto));

    /// <summary>Highways awaiting moderation.</summary>
    [HttpGet("pending"), Authorize(Policy = Permissions.SubmissionsModerate)]
    public async Task<ActionResult<IEnumerable<Atlas.Highway>>> GetPending() => Ok((await Rows.AsNoTracking().Where(h => h.ReviewStatus == "Pending").OrderBy(h => h.Id).ToListAsync()).Select(HighwayMapping.ToDto));

    /// <summary>Create a highway with full attribution and atomic audit history.</summary>
    [HttpPost, Authorize(Policy = Permissions.HighwaysCreate)]
    public async Task<ActionResult<Atlas.Highway>> CreateHighway([FromBody] Atlas.Highway dto)
    {
        var error = HighwayEditing.Validate(dto);
        if (error is not null) return BadRequest(new { message = error });
        await using var tx = await context.Database.BeginTransactionAsync();
        if (!await HighwayEditing.GroupsExist(context, dto)) return BadRequest(new { message = "An attributed group does not exist." });
        var risks = HighwayEditing.Risks(null, dto);
        if (!Owner && risks.Count > 0)
        {
            var denied = await Blocked(0, null, dto, risks);
            await tx.CommitAsync();
            return denied;
        }
        var row = new ServerHighway { CreatedByUserId = UserId };
        HighwayMapping.Apply(dto, row);
        if (string.IsNullOrWhiteSpace(row.Slug)) row.Slug = HighwayMapping.Slugify(row.Name);
        row.ReviewStatus = Can(Permissions.HighwaysEdit) ? "Approved" : "Pending";
        context.Highways.Add(row);
        HighwayEditing.ApplyGroups(context, row, dto);
        await context.SaveChangesAsync();
        var saved = HighwayMapping.ToDto(await Rows.SingleAsync(h => h.Id == row.Id));
        await HighwayEditing.Record(audit, "highway.create", row.Id, UserId, Username, null, saved, risks);
        await tx.CommitAsync();
        return CreatedAtAction(nameof(GetHighway), new { id = row.Id }, saved);
    }

    /// <summary>Update only the version the editor actually loaded.</summary>
    [HttpPut("{id:int}"), Authorize(Policy = Permissions.HighwaysEdit)]
    public async Task<ActionResult<Atlas.Highway>> UpdateHighway(int id, [FromBody] Atlas.Highway dto)
    {
        var error = HighwayEditing.Validate(dto);
        if (error is not null) return BadRequest(new { message = error });
        await using var tx = context.Database.CurrentTransaction is null ? await context.Database.BeginTransactionAsync() : null;
        var row = await Rows.SingleOrDefaultAsync(h => h.Id == id);
        if (row is null) return NotFound();
        var before = HighwayMapping.ToDto(row);
        if (string.IsNullOrEmpty(dto.EditVersion)) return StatusCode(428, new { message = "Reload the Atlas page before editing: this client has no highway version." });
        if (dto.EditVersion != before.EditVersion) return Conflict(new { message = "This highway changed while you were editing. Your draft has been kept; reload the highway and compare before saving." });
        if (!await HighwayEditing.GroupsExist(context, dto)) return BadRequest(new { message = "An attributed group does not exist." });
        var risks = await HighwayEditing.RisksSinceFirstEdit(context, id, UserId, before, dto);
        if (!Owner && risks.Count > 0)
        {
            var denied = await Blocked(id, before, dto, risks);
            if (tx is not null) await tx.CommitAsync();
            return denied;
        }
        HighwayMapping.Apply(dto, row);
        row.LastEditedByUserId = UserId;
        if (string.IsNullOrWhiteSpace(row.Slug)) row.Slug = HighwayMapping.Slugify(row.Name);
        HighwayEditing.ApplyGroups(context, row, dto);
        await context.SaveChangesAsync();
        var saved = HighwayMapping.ToDto(await Rows.SingleAsync(h => h.Id == id));
        await HighwayEditing.Record(audit, "highway.update", id, UserId, Username, before, saved, risks);
        if (tx is not null) await tx.CommitAsync();
        return Ok(saved);
    }

    private async Task<ObjectResult> Blocked(int id, Atlas.Highway? before, Atlas.Highway proposed, List<string> risks)
    {
        // After is the unchanged canonical state. The rejected proposal is not a restorable version.
        await HighwayEditing.Record(audit, "highway.blocked", id, UserId, Username, before, before, risks, proposed);
        logger.LogWarning("Blocked highway edit by {User} on {Id}: {Reasons}", Username, id, string.Join("; ", risks));
        return StatusCode(422, new { message = "Owner review required: " + string.Join("; ", risks) + ". Ask atlas-owner to review this change. Your draft is retained." });
    }

    /// <summary>Owner-only deletion; the complete record remains recoverable in history.</summary>
    [HttpDelete("{id:int}"), Authorize(Policy = Permissions.HighwaysDelete)]
    public async Task<IActionResult> DeleteHighway(int id)
    {
        if (!Owner) return Forbid();
        await using var tx = await context.Database.BeginTransactionAsync();
        var row = await Rows.SingleOrDefaultAsync(h => h.Id == id);
        if (row is null) return NotFound();
        var before = HighwayMapping.ToDto(row);
        context.Highways.Remove(row);
        await HighwayEditing.Record(audit, "highway.delete", id, UserId, Username, before, null, ["Deleted highway"]);
        await tx.CommitAsync();
        return NoContent();
    }

    /// <summary>Approve a pending highway.</summary>
    [HttpPost("{id:int}/approve"), Authorize(Policy = Permissions.SubmissionsModerate)]
    public Task<IActionResult> Approve(int id) => Moderate(id, "Approved");

    /// <summary>Reject a pending highway; withdrawing an approved route is owner-only.</summary>
    [HttpPost("{id:int}/reject"), Authorize(Policy = Permissions.SubmissionsModerate)]
    public Task<IActionResult> Reject(int id) => Moderate(id, "Rejected");

    private async Task<IActionResult> Moderate(int id, string status)
    {
        await using var tx = await context.Database.BeginTransactionAsync();
        var row = await Rows.SingleOrDefaultAsync(h => h.Id == id);
        if (row is null) return NotFound();
        if (row.ReviewStatus != "Pending" && !Owner) return BadRequest(new { message = "Only pending highways can be moderated. Ask atlas-owner to withdraw an approved route." });
        var before = HighwayMapping.ToDto(row);
        row.ReviewStatus = status; row.LastEditedByUserId = UserId;
        await HighwayEditing.Record(audit, status == "Approved" ? "highway.approve" : "highway.reject", id, UserId, Username, before, HighwayMapping.ToDto(row));
        await tx.CommitAsync();
        return Ok(HighwayMapping.ToDto(row));
    }

    /// <summary>Recent highway edits, warnings and full snapshots, including deleted highways.</summary>
    [HttpGet("history"), Authorize(Policy = Permissions.HighwaysEdit)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<List<HighwayChange>>> History([FromQuery] int? highwayId = null, [FromQuery] int take = 200)
    {
        var query = context.AuditLogs.AsNoTracking().Where(a => a.EntityType == "Highway" && a.Action.StartsWith("highway."));
        if (highwayId is int id) query = query.Where(a => a.EntityId == id);
        var entries = await query.OrderByDescending(a => a.Id).Take(Math.Clamp(take, 1, 500)).ToListAsync();
        return Ok(entries.Select(ReadChange).ToList());
    }

    /// <summary>Restore a complete prior highway state without rewinding unrelated catalog records.</summary>
    [HttpPost("history/{auditId:int}/restore"), Authorize(Policy = Permissions.HighwaysDelete)]
    public async Task<ActionResult<Atlas.Highway>> Restore(int auditId, [FromBody] HighwayRestoreRequest request)
    {
        if (!Owner) return Forbid();
        await using var tx = await context.Database.BeginTransactionAsync();
        var entry = await context.AuditLogs.SingleOrDefaultAsync(a => a.Id == auditId && a.EntityType == "Highway" && a.Action.StartsWith("highway."));
        if (entry is null) return NotFound();
        var change = ReadChange(entry);
        if (change.Before is null || entry.Action == "highway.blocked") return BadRequest(new { message = "This entry has no restorable prior state. Older edits can be recovered from the pre-edit database backups." });
        var row = await Rows.SingleOrDefaultAsync(h => h.Id == entry.EntityId);
        var before = row is null ? null : HighwayMapping.ToDto(row);
        if (request.ExpectedVersion != (before?.EditVersion ?? "deleted")) return Conflict(new { message = "The highway changed after the restore preview. Refresh history before restoring." });
        if (!await HighwayEditing.GroupsExist(context, change.Before)) return Conflict(new { message = "A saved group no longer exists. Restore that group before restoring this highway." });
        if (row is null)
        {
            row = new ServerHighway { Id = entry.EntityId, CreatedByUserId = change.Before.CreatedByUserId, DateAddedUtc = change.Before.DateAddedUtc.ToString("o") };
            context.Highways.Add(row);
        }
        HighwayMapping.Apply(change.Before, row);
        row.Slug = change.Before.Slug;
        row.ReviewStatus = change.Before.ReviewStatus.ToString(); row.LastEditedByUserId = UserId;
        HighwayEditing.ApplyGroups(context, row, change.Before);
        await context.SaveChangesAsync();
        var restored = HighwayMapping.ToDto(await Rows.SingleAsync(h => h.Id == row.Id));
        await HighwayEditing.Record(audit, "highway.restore", row.Id, UserId, Username, before, restored, [$"Restored state before audit #{auditId}"]);
        await tx.CommitAsync();
        return Ok(restored);
    }

    private static HighwayChange ReadChange(AuditLog entry)
    {
        HighwayChange change;
        try { change = JsonSerializer.Deserialize<HighwayChange>(entry.DetailsJson ?? "{}") ?? new(); }
        catch (JsonException) { change = new(); }
        change.Id = entry.Id; change.HighwayId = entry.EntityId; change.Action = entry.Action;
        change.Username = entry.Username; change.Summary = entry.Summary;
        change.CreatedUtc = DateTime.TryParse(entry.CreatedUtc, out var date) ? date : DateTime.MinValue;
        return change;
    }
}
