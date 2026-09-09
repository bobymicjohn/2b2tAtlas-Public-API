using System.ComponentModel.DataAnnotations;
using Atlas.Auth;

namespace Atlas.Auth;

/// <summary>
/// Create user request model.
/// </summary>
public class CreateUserRequest
{
    /// <summary>Gets or sets the unique sign-in name for the new user.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Gets or sets the plaintext credential to hash when the user is created.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Gets or sets the user's optional Discord handle.</summary>
    public string? DiscordHandle { get; set; }

    /// <summary>Gets or sets the canonical role identifier to assign.</summary>
    public string? Role { get; set; }

    /// <summary>Gets or sets the legacy administrator flag when explicitly supplied.</summary>
    public bool? IsAdmin { get; set; }

    /// <summary>Gets or sets whether the account may authenticate when explicitly supplied.</summary>
    public bool? IsActive { get; set; }
}