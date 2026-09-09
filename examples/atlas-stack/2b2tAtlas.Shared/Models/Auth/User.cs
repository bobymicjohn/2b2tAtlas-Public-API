using System.ComponentModel.DataAnnotations;
using Atlas.Auth;

namespace Atlas.Auth;

/// <summary>
/// Represents a user in the system.
/// </summary>
public class User
{
    /// <summary>
    /// Gets or sets the unique identifier for the user.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the username.
    /// </summary>
    [Required(ErrorMessage = "Username is required")]
    [StringLength(50, ErrorMessage = "Username cannot exceed 50 characters")]
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the optional Discord handle used as an alternate login identity.
    /// </summary>
    [StringLength(255, ErrorMessage = "Discord handle cannot exceed 255 characters")]
    public string? DiscordHandle { get; set; }

    /// <summary>
    /// Gets or sets the password hash.
    /// </summary>
    [Required]
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the first name.
    /// </summary>
    [StringLength(100, ErrorMessage = "First name cannot exceed 100 characters")]
    public string? FirstName { get; set; }

    /// <summary>
    /// Gets or sets the last name.
    /// </summary>
    [StringLength(100, ErrorMessage = "Last name cannot exceed 100 characters")]
    public string? LastName { get; set; }

    /// <summary>
    /// Gets or sets the user role.
    /// </summary>
    [Required(ErrorMessage = "Role is required")]
    [StringLength(50, ErrorMessage = "Role cannot exceed 50 characters")]
    public string Role { get; set; } = "User";

    /// <summary>
    /// Gets or sets whether the user is active.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the user is an admin.
    /// </summary>
    public bool IsAdmin { get; set; } = false;

    /// <summary>
    /// Gets or sets whether the user is the master SuperAdmin (full control,
    /// bypasses all permission checks; cannot be demoted/deleted by lower roles).
    /// </summary>
    public bool IsSuperAdmin { get; set; } = false;

    /// <summary>
    /// Gets or sets the user's in-game Minecraft username (optional).
    /// </summary>
    [StringLength(50)]
    public string? MinecraftUsername { get; set; }

    /// <summary>
    /// Gets or sets the user's Discord id/handle (optional).
    /// </summary>
    [StringLength(100)]
    public string? DiscordId { get; set; }

    /// <summary>
    /// Gets or sets a short public bio (optional).
    /// </summary>
    [StringLength(500)]
    public string? Bio { get; set; }

    /// <summary>
    /// Gets or sets the contributor trust level (0 = new). Higher trust can
    /// unlock auto-approval of submissions.
    /// </summary>
    public int TrustLevel { get; set; } = 0;

    /// <summary>
    /// Gets or sets whether this user's submissions skip the moderation queue.
    /// </summary>
    public bool AutoApprove { get; set; } = false;

    /// <summary>
    /// Gets or sets the effective permissions resolved from the user's role
    /// (populated by the server; used by the client for conditional UI).
    /// </summary>
    public List<string> Permissions { get; set; } = new();

    /// <summary>
    /// Gets or sets the date and time when the user was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets the date and time of the last login.
    /// </summary>
    public DateTime? LastLoginAt { get; set; }
}