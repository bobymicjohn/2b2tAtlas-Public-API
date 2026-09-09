using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using Atlas.Auth;
using _2b2tAtlas.Server.Services;

namespace _2b2tAtlas.Server.Controllers;

/// <summary>
/// Exposes rate-limited account registration and login plus authenticated profile and token checks.
/// </summary>
/// <remarks>
/// Password verification, lockout state, audit logging, and permission claims are owned by
/// <see cref="AuthService"/>. This controller never returns a persisted password hash.
/// </remarks>
[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly AuthService _authService;
    private readonly ILogger<AuthController> _logger;

    /// <summary>Initializes the authentication endpoints.</summary>
    /// <param name="authService">The service that verifies credentials and issues Atlas JWTs.</param>
    /// <param name="logger">The diagnostic logger for request-level authentication failures.</param>
    public AuthController(AuthService authService, ILogger<AuthController> logger)
    {
        _authService = authService;
        _logger = logger;
    }

    /// <summary>
    /// Register a new user account
    /// </summary>
    /// <param name="request">Registration details</param>
    /// <returns>Authentication response with JWT token</returns>
    [HttpPost("register")]
    [EnableRateLimiting("auth-register")]
    [ProducesResponseType(typeof(AuthResponse), 200)]
    [ProducesResponseType(typeof(AuthResponse), 400)]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new AuthResponse
            {
                Success = false,
                Message = "Invalid registration data",
                Token = null,
                User = null
            });
        }

        var result = await _authService.RegisterAsync(request);
        
        if (result == null)
        {
            return StatusCode(500, new AuthResponse
            {
                Success = false,
                Message = "Server error during registration",
                Token = null,
                User = null
            });
        }

        if (!result.Success)
        {
            return BadRequest(result);
        }

        return Ok(result);
    }

    /// <summary>
    /// Login with username/Discord handle and password
    /// </summary>
    /// <param name="request">Login credentials</param>
    /// <returns>Authentication response with JWT token</returns>
    [HttpPost("login")]
    [EnableRateLimiting("auth-login")]
    [ProducesResponseType(typeof(AuthResponse), 200)]
    [ProducesResponseType(typeof(AuthResponse), 401)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new AuthResponse
            {
                Success = false,
                Message = "Invalid login data",
                Token = null,
                User = null
            });
        }

        var result = await _authService.LoginAsync(request);

        if (result == null)
        {
            return StatusCode(500, new AuthResponse
            {
                Success = false,
                Message = "Server error during login",
                Token = null,
                User = null
            });
        }

        if (!result.Success)
        {
            return Unauthorized(result);
        }

        return Ok(result);
    }

    /// <summary>
    /// Get current user profile (requires authentication)
    /// </summary>
    /// <returns>User profile information</returns>
    [HttpGet("profile")]
    [Authorize]
    [ProducesResponseType(typeof(User), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetProfile()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
        if (userIdClaim == null || !int.TryParse(userIdClaim.Value, out int userId))
        {
            return Unauthorized();
        }

        var userProfile = await _authService.GetUserProfileAsync(userId);
        if (userProfile == null)
        {
            return NotFound();
        }

        return Ok(userProfile);
    }

    /// <summary>
    /// Update last login timestamp for current user (requires authentication)
    /// </summary>
    /// <returns>Success status</returns>
    [HttpPost("update-last-login")]
    [Authorize]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(401)]
    public async Task<IActionResult> UpdateLastLogin()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
        if (userIdClaim == null || !int.TryParse(userIdClaim.Value, out int userId))
        {
            return Unauthorized();
        }

        var success = await _authService.UpdateLastLoginAsync(userId);
        if (!success)
        {
            return BadRequest(new { message = "Failed to update last login" });
        }

        return Ok(new { message = "Last login updated successfully" });
    }

    /// <summary>
    /// Validate current JWT token (requires authentication)
    /// </summary>
    /// <returns>Token validation status</returns>
    [HttpGet("validate")]
    [Authorize]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(401)]
    public IActionResult ValidateToken()
    {
        try
        {
            // If we reach here, the JWT middleware has validated the token
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized();
            }

            return Ok(new { valid = true, userId });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }
}
