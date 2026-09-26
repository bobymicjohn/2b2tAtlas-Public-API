using System.ComponentModel.DataAnnotations;
using Atlas.Auth;

namespace Atlas.Auth;

/// <summary>
/// Create user request model.
/// </summary>
public class CreateUserRequest
{
    /// <summary>Unique sign-in name for the new user.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Plaintext credential to hash when the user is created.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>User's optional Discord handle.</summary>
    public string? DiscordHandle { get; set; }

    /// <summary>Canonical role identifier to assign.</summary>
    public string? Role { get; set; }

    /// <summary>Legacy administrator flag when explicitly supplied.</summary>
    public bool? IsAdmin { get; set; }

    /// <summary>Whether the account may authenticate when explicitly supplied.</summary>
    public bool? IsActive { get; set; }
}
