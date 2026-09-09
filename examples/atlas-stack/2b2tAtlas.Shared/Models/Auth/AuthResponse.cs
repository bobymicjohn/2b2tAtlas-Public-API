using System.ComponentModel.DataAnnotations;
using Atlas.Auth;

namespace Atlas.Auth;

/// <summary>
/// Authentication response model.
/// </summary>
public class AuthResponse
{
    /// <summary>Gets or sets whether the authentication operation succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>Gets or sets the user-facing result or failure message.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets the bearer token issued on success, or <see langword="null"/> when no token was issued.</summary>
    public string? Token { get; set; }

    /// <summary>Gets or sets the authenticated user's profile and resolved permissions when authentication succeeds.</summary>
    public User? User { get; set; }
}