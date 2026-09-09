using System.ComponentModel.DataAnnotations;
using Atlas.Auth;

namespace Atlas.Auth;

/// <summary>
/// Login request model.
/// </summary>
public class LoginRequest
{
    /// <summary>Gets or sets the required account username.</summary>
    [Required(ErrorMessage = "Username is required")]
    public string Username { get; set; } = string.Empty;

    /// <summary>Gets or sets the required plaintext credential submitted for verification.</summary>
    [Required(ErrorMessage = "Password is required")]
    public string Password { get; set; } = string.Empty;
}