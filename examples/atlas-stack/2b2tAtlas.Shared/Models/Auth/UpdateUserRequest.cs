using System.ComponentModel.DataAnnotations;
using Atlas.Auth;

namespace Atlas.Auth;

/// <summary>
/// Update user request model.
/// </summary>
public class UpdateUserRequest
{
    /// <summary>Account's updated sign-in name.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Account's optional Discord handle.</summary>
    public string? DiscordHandle { get; set; }

    /// <summary>Canonical role identifier to assign.</summary>
    public string? Role { get; set; }

    /// <summary>Legacy administrator flag when it should be changed.</summary>
    public bool? IsAdmin { get; set; }

    /// <summary>Whether the account may authenticate when its state should be changed.</summary>
    public bool? IsActive { get; set; }

    /// <summary>A replacement plaintext credential to hash, or <see langword="null"/> to retain the current credential.</summary>
    public string? NewPassword { get; set; }
}
