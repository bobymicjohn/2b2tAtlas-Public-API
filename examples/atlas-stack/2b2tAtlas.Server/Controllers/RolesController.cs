using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Atlas.Auth;
using _2b2tAtlas.Server.Models;
using _2b2tAtlas.Server.Services;

namespace _2b2tAtlas.Server.Controllers;

/// <summary>
/// Role → permission matrix editor (GAMEPLAN §15). Requires <c>users.roles.assign</c>.
/// Storing rows for a role in <c>RolePermissions</c> overrides its code-defined default;
/// deleting them reverts the role to the default. SuperAdmin is never editable.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = Permissions.RolesManage)]
public class RolesController : ControllerBase
{
    private readonly AtlasContext _context;
    private readonly AuditService _audit;

    private static readonly Dictionary<string, string> EditableRoleLookup =
        RoleNames.All
            .Where(r => r != RoleNames.SuperAdmin)
            .ToDictionary(r => r, r => r, StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes the Founder-only role-profile editor.</summary>
    /// <param name="context">The Atlas context containing explicit role permission overrides.</param>
    /// <param name="audit">The append-only recorder for role-profile changes.</param>
    public RolesController(AtlasContext context, AuditService audit)
    {
        _context = context;
        _audit = audit;
    }

    /// <summary>Editable roles = every named role except SuperAdmin.</summary>
    private static IEnumerable<string> EditableRoles =>
        RoleNames.All.Where(r => r != RoleNames.SuperAdmin);

    private static bool TryResolveEditableRole(string? role, out string canonicalRole)
    {
        canonicalRole = string.Empty;
        if (string.IsNullOrWhiteSpace(role)) return false;
        if (!EditableRoleLookup.TryGetValue(role.Trim(), out var resolved)) return false;
        canonicalRole = resolved;
        return true;
    }

    /// <summary>Returns effective grants for every editable canonical role.</summary>
    /// <returns>
    /// A matrix combining database overrides with code defaults and marking roles that are customized.
    /// SuperAdmin is intentionally excluded from the editable set.
    /// </returns>
    [HttpGet]
    public async Task<ActionResult<RoleMatrix>> GetMatrix()
    {
        var overrides = await _context.RolePermissions.ToListAsync();
        var byRole = overrides.GroupBy(o => o.Role)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Permission).Distinct().ToList());

        var matrix = new RoleMatrix
        {
            Roles = EditableRoles.ToList(),
            Permissions = Permissions.All.ToList(),
        };
        foreach (var role in matrix.Roles)
        {
            if (byRole.TryGetValue(role, out var custom))
            {
                matrix.Grants[role] = custom;
                matrix.Customized.Add(role);
            }
            else
            {
                matrix.Grants[role] = RolePermissions.ForRole(role, false).ToList();
            }
        }
        return Ok(matrix);
    }

    /// <summary>Replaces one canonical role's effective permission bundle with an explicit database override.</summary>
    /// <param name="role">The editable canonical role name; SuperAdmin is rejected.</param>
    /// <param name="permissions">The complete distinct set of known permission identifiers to grant.</param>
    /// <returns>The refreshed role matrix after persistence and audit logging.</returns>
    [HttpPut("{role}")]
    public async Task<ActionResult<RoleMatrix>> SetRole(string role, [FromBody] List<string> permissions)
    {
        if (!TryResolveEditableRole(role, out var canonicalRole))
            return BadRequest("Unknown or non-editable role.");

        permissions ??= new List<string>();

        if (permissions.Count > Permissions.All.Count())
            return BadRequest("Too many permissions supplied.");

        var normalized = permissions
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var invalid = normalized.Where(p => !Permissions.All.Contains(p)).ToList();
        if (invalid.Count > 0)
            return BadRequest($"Unknown permission(s): {string.Join(", ", invalid)}");

        var existing = await _context.RolePermissions.Where(rp => rp.Role == canonicalRole).ToListAsync();
        _context.RolePermissions.RemoveRange(existing);

        foreach (var p in normalized)
            _context.RolePermissions.Add(new RolePermission { Role = canonicalRole, Permission = p });

        await _context.SaveChangesAsync();

        await _audit.LogAsync("role.update", "Role", 0, CurrentUserId(), CurrentUsername(),
            $"Set '{canonicalRole}' permissions ({normalized.Count})");
        return await GetMatrix();
    }

    /// <summary>Deletes a role's override rows so its code-defined permission profile becomes effective again.</summary>
    /// <param name="role">The editable canonical role name to reset.</param>
    /// <returns>The refreshed role matrix after persistence and audit logging.</returns>
    [HttpDelete("{role}")]
    public async Task<ActionResult<RoleMatrix>> ResetRole(string role)
    {
        if (!TryResolveEditableRole(role, out var canonicalRole))
            return BadRequest("Unknown or non-editable role.");

        var existing = await _context.RolePermissions.Where(rp => rp.Role == canonicalRole).ToListAsync();
        _context.RolePermissions.RemoveRange(existing);
        await _context.SaveChangesAsync();

        await _audit.LogAsync("role.reset", "Role", 0, CurrentUserId(), CurrentUsername(),
            $"Reset '{canonicalRole}' to default permissions");
        return await GetMatrix();
    }

    private int? CurrentUserId() =>
        int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var uid) ? uid : null;

    private string? CurrentUsername() => User.FindFirstValue(ClaimTypes.Name);
}
