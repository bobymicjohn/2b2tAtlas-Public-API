using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Atlas;
using Atlas.Auth;
using _2b2tAtlas.Server.Models;

namespace _2b2tAtlas.Server.Controllers;

/// <summary>
/// Read-only access to the audit log (GAMEPLAN §15). Requires the
/// <c>audit.view</c> permission.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = Permissions.AuditView)]
public class AuditController : ControllerBase
{
    private readonly AtlasContext _context;

    /// <summary>Initializes the permission-gated audit reader.</summary>
    /// <param name="context">The Atlas context containing immutable mutation records.</param>
    public AuditController(AtlasContext context)
    {
        _context = context;
    }

    /// <summary>GET /api/audit — the most recent audit entries (newest first).</summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<AuditLogEntry>>> GetAudit([FromQuery] int take = 200)
    {
        take = Math.Clamp(take, 1, 1000);
        var rows = await _context.AuditLogs
            .OrderByDescending(a => a.Id)
            .Take(take)
            .ToListAsync();

        return Ok(rows.Select(a => new AuditLogEntry
        {
            Id = a.Id,
            Action = a.Action,
            EntityType = a.EntityType,
            EntityId = a.EntityId,
            UserId = a.UserId,
            Username = a.Username,
            Summary = a.Summary,
            DetailsJson = a.DetailsJson,
            CreatedUtc = DateTime.TryParse(a.CreatedUtc, out var t) ? t : DateTime.MinValue,
        }).ToList());
    }
}
