using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Atlas.Auth;
using Microsoft.EntityFrameworkCore;
using _2b2tAtlas.Server.Models;
using ServerUser = _2b2tAtlas.Server.Models.User;

namespace _2b2tAtlas.Server.Services;

/// <summary>Revalidates accounts and permissions on every authenticated request.</summary>
public sealed class AtlasSessionValidator(AtlasContext db, IConfiguration configuration)
{
    /// <summary>The existing, non-transferable owner account.</summary>
    public static bool IsOwner(ServerUser user) => user.Id == 1 &&
        user.Username == "atlas-owner" && user.IsSuperAdmin == 1 && user.IsActive == 1;

    /// <summary>Owner identity is issued only after database validation.</summary>
    public static bool IsOwner(ClaimsPrincipal user) => user.HasClaim("atlas_owner", "true");

    /// <summary>Password changes invalidate all previously issued sessions without exposing a password digest.</summary>
    public static string Stamp(ServerUser user, string signingKey) => Convert.ToHexString(
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingKey), Encoding.UTF8.GetBytes($"atlas-session-v1:{user.Id}:{user.PasswordHash}")));

    /// <summary>Ignores token permission/role claims and rebuilds them from current database state.</summary>
    public async Task<ClaimsPrincipal?> ValidateAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        if (!int.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)) return null;
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, ct);
        if (user == null || user.IsActive != 1) return null;
        var expected = Stamp(user, configuration["JwtSettings:SecretKey"]!);
        var supplied = principal.FindFirstValue("atlas_session");
        if (supplied == null || !CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(supplied))) return null;
        var owner = IsOwner(user);
        if (!owner && (user.IsSuperAdmin == 1 || user.Role == RoleNames.SuperAdmin)) return null;
        var permissions = await db.RolePermissions.AsNoTracking().Where(p => p.Role == user.Role)
            .Select(p => p.Permission).ToListAsync(ct);
        if (permissions.Count == 0) permissions = RolePermissions.ForRole(user.Role).ToList();
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username), new(ClaimTypes.Role, user.Role) };
        if (owner) { claims.Add(new("atlas_owner", "true")); claims.Add(new("superadmin", "true")); }
        foreach (var permission in owner ? Permissions.All : permissions.Where(p => !AtlasWriteProtection.OwnerPermissions.Contains(p)))
            claims.Add(new("perm", permission));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }
}
