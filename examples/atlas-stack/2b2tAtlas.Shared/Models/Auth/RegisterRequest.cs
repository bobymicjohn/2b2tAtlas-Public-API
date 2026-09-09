using System.ComponentModel.DataAnnotations;
using Atlas.Auth;

namespace Atlas.Auth;

/// <summary>
/// Registration request model.
/// </summary>
public class RegisterRequest
{
    /// <summary>Gets or sets the required unique username, from 3 through 50 characters.</summary>
    [Required(ErrorMessage = "Username is required")]
    [StringLength(50, MinimumLength = 3, ErrorMessage = "Username must be between 3 and 50 characters")]
    public string Username { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional Discord handle used as an alternate login identity.</summary>
    public string? DiscordHandle { get; set; }

    /// <summary>Gets or sets the required plaintext password, from 6 through 100 characters.</summary>
    [Required(ErrorMessage = "Password is required")]
    [StringLength(100, MinimumLength = 6, ErrorMessage = "Password must be at least 6 characters")]
    public string Password { get; set; } = string.Empty;

    /// <summary>Gets or sets the required confirmation value, which must equal <see cref="Password"/>.</summary>
    [Required(ErrorMessage = "Password confirmation is required")]
    [Compare("Password", ErrorMessage = "Passwords do not match")]
    public string ConfirmPassword { get; set; } = string.Empty;

    /// <summary>Gets or sets the user's optional first name.</summary>
    public string? FirstName { get; set; }

    /// <summary>Gets or sets the user's optional last name.</summary>
    public string? LastName { get; set; }
}