using System.ComponentModel.DataAnnotations;

namespace _2b2tAtlas.Server.Models;

/// <summary>
/// RBAC / profile extensions to the auto-generated <see cref="User"/> entity
/// (see GAMEPLAN §15). Kept in a separate partial so EF Core Power Tools
/// re-scaffolds never clobber these. New columns are added to the existing
/// SQLite table at startup by <c>SchemaUpgrader</c> (the app uses
/// EnsureCreated, not migrations).
/// </summary>
public partial class User
{
    /// <summary>1 = master SuperAdmin (bypasses all permission checks).</summary>
    public int IsSuperAdmin { get; set; }

    /// <summary>Optional in-game Minecraft username.</summary>
    public string? MinecraftUsername { get; set; }

    /// <summary>Optional Discord id/handle.</summary>
    public string? DiscordId { get; set; }

    /// <summary>Optional short public bio.</summary>
    public string? Bio { get; set; }

    /// <summary>Contributor trust level (0 = new).</summary>
    public int TrustLevel { get; set; }

    /// <summary>1 = this user's submissions skip the moderation queue.</summary>
    public int AutoApprove { get; set; }

    /// <summary>ISO 8601 timestamp of the user's last canonical edit (nullable).</summary>
    public string? LastEditAt { get; set; }

    /// <summary>Consecutive failed login count since last successful login.</summary>
    public int FailedLoginAttempts { get; set; }

    /// <summary>ISO 8601 timestamp of the most recent failed login (nullable).</summary>
    public string? LastFailedLoginAt { get; set; }

    /// <summary>ISO 8601 lockout end timestamp after too many failed logins (nullable).</summary>
    public string? LockoutEndUtc { get; set; }
}
