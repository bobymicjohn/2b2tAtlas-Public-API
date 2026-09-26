using System.ComponentModel.DataAnnotations;
using Atlas.Auth;

namespace Atlas.Auth;

/// <summary>
/// Represents a user in the system.
/// </summary>
public class User
{
    /// <summary>
    /// Unique identifier for the user.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Username.
    /// </summary>
    [Required(ErrorMessage = "Username is required")]
    [StringLength(50, ErrorMessage = "Username cannot exceed 50 characters")]
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Optional Discord handle used as an alternate login identity.
    /// </summary>
    [StringLength(255, ErrorMessage = "Discord handle cannot exceed 255 characters")]
    public string? DiscordHandle { get; set; }

    /// <summary>
    /// Password hash.
    /// </summary>
    [Required]
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>
    /// First name.
    /// </summary>
    [StringLength(100, ErrorMessage = "First name cannot exceed 100 characters")]
    public string? FirstName { get; set; }

    /// <summary>
    /// Last name.
    /// </summary>
    [StringLength(100, ErrorMessage = "Last name cannot exceed 100 characters")]
    public string? LastName { get; set; }

    /// <summary>
    /// User role.
    /// </summary>
    [Required(ErrorMessage = "Role is required")]
    [StringLength(50, ErrorMessage = "Role cannot exceed 50 characters")]
    public string Role { get; set; } = "User";

    /// <summary>
    /// Whether the user is active.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Whether the user is an admin.
    /// </summary>
    public bool IsAdmin { get; set; } = false;

    /// <summary>
    /// Whether the user is the master SuperAdmin (full control,
    /// bypasses all permission checks; cannot be demoted/deleted by lower roles).
    /// </summary>
    public bool IsSuperAdmin { get; set; } = false;

    /// <summary>
    /// User's in-game Minecraft username (optional).
    /// </summary>
    [StringLength(50)]
    public string? MinecraftUsername { get; set; }

    /// <summary>
    /// User's Discord id/handle (optional).
    /// </summary>
    [StringLength(100)]
    public string? DiscordId { get; set; }

    /// <summary>
    /// A short public bio (optional).
    /// </summary>
    [StringLength(500)]
    public string? Bio { get; set; }

    /// <summary>
    /// Contributor trust level (0 = new). Higher trust can
    /// unlock auto-approval of submissions.
    /// </summary>
    public int TrustLevel { get; set; } = 0;

    /// <summary>
    /// Whether this user's submissions skip the moderation queue.
    /// </summary>
    public bool AutoApprove { get; set; } = false;

    /// <summary>
    /// Effective permissions resolved from the user's role
    /// (populated by the server; used by the client for conditional UI).
    /// </summary>
    public List<string> Permissions { get; set; } = new();

    /// <summary>
    /// Date and time when the user was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Date and time of the last login.
    /// </summary>
    public DateTime? LastLoginAt { get; set; }
}
