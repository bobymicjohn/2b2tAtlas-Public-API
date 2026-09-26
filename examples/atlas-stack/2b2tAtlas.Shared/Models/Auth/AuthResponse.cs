using System.ComponentModel.DataAnnotations;
using Atlas.Auth;

namespace Atlas.Auth;

/// <summary>
/// Authentication response model.
/// </summary>
public class AuthResponse
{
    /// <summary>Whether the authentication operation succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>User-facing result or failure message.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Bearer token issued on success, or <see langword="null"/> when no token was issued.</summary>
    public string? Token { get; set; }

    /// <summary>Authenticated user's profile and resolved permissions when authentication succeeds.</summary>
    public User? User { get; set; }
}
