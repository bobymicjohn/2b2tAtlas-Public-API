using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using BCrypt.Net;
using Microsoft.EntityFrameworkCore;
using Atlas.Auth;
using _2b2tAtlas.Server.Models;
using ServerUser = _2b2tAtlas.Server.Models.User;

namespace _2b2tAtlas.Server.Services;

/// <summary>
/// Enforces Atlas account registration, credential verification, lockout, effective permissions,
/// and JWT issuance at the server trust boundary.
/// </summary>
/// <remarks>
/// Passwords are stored only as BCrypt hashes. Failed login responses are deliberately uniform,
/// delayed, and audited to reduce account enumeration and brute-force risk. JWT permission claims
/// are resolved from database role overrides before falling back to code-defined role profiles.
/// </remarks>
public class AuthService
{
    private readonly AtlasContext _context;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthService> _logger;
    private readonly AuditService _audit;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly int _maxFailedLoginAttempts;
    private readonly TimeSpan _lockoutDuration;
    private readonly int _baseFailedLoginDelayMs;
    private readonly int _maxFailedLoginDelayMs;

    // Used to keep invalid-credential responses closer in timing to valid-user checks.
    private static readonly string DummyPasswordHash = BCrypt.Net.BCrypt.HashPassword("AtlasDummyPassword!2026");

    /// <summary>Initializes authentication and loads bounded lockout/backoff settings.</summary>
    /// <param name="context">The Atlas context containing accounts and role permission overrides.</param>
    /// <param name="configuration">JWT signing and authentication-hardening configuration.</param>
    /// <param name="logger">The diagnostic logger for authentication and audit failures.</param>
    /// <param name="audit">The append-only recorder for login outcomes.</param>
    /// <param name="httpContextAccessor">Access to request metadata included in security audit details.</param>
    public AuthService(
        AtlasContext context,
        IConfiguration configuration,
        ILogger<AuthService> logger,
        AuditService audit,
        IHttpContextAccessor httpContextAccessor)
    {
        _context = context;
        _configuration = configuration;
        _logger = logger;
        _audit = audit;
        _httpContextAccessor = httpContextAccessor;

        var hardening = _configuration.GetSection("AuthHardening");
        _maxFailedLoginAttempts = Math.Max(3, hardening.GetValue<int?>("MaxFailedLoginAttempts") ?? 8);
        var lockoutMinutes = hardening.GetValue<int?>("LockoutMinutes") ?? 15;
        _lockoutDuration = TimeSpan.FromMinutes(Math.Clamp(lockoutMinutes, 1, 1440));
        _baseFailedLoginDelayMs = Math.Clamp(hardening.GetValue<int?>("BaseFailedLoginDelayMs") ?? 250, 50, 2000);
        _maxFailedLoginDelayMs = Math.Clamp(hardening.GetValue<int?>("MaxFailedLoginDelayMs") ?? 5000, 250, 15000);
    }

    /// <summary>Creates an active Member account after enforcing unique username and optional Discord handle values.</summary>
    /// <param name="request">Validated public registration data including the plaintext password to hash.</param>
    /// <returns>An authentication response containing a new JWT on success, or a non-success response.</returns>
    /// <remarks>The plaintext password is not persisted or logged.</remarks>
    public async Task<AuthResponse?> RegisterAsync(RegisterRequest request)
    {
        try
        {
            var username = (request.Username ?? string.Empty).Trim();
            var discordHandle = NormalizeDiscordHandle(request.DiscordHandle);

            // Check if user already exists
            if (await _context.Users.AnyAsync(u => u.Username == username ||
                (discordHandle != null && u.DiscordHandle == discordHandle)))
            {
                return new AuthResponse
                {
                    Success = false,
                    Message = "User with this username or Discord handle already exists",
                    Token = null,
                    User = null
                };
            }

            // Create new user
            var user = new ServerUser
            {
                Username = username,
                DiscordHandle = discordHandle,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
                Role = "User", // Default role
                CreatedAt = DateTime.UtcNow.ToString("o"),
                IsActive = 1,
                IsAdmin = 0,
            };

            _context.Users.Add(user);
            await _context.SaveChangesAsync();

            // Generate JWT token
            var token = GenerateJwtToken(user);

            return new AuthResponse
            {
                Success = true,
                Message = "Registration successful",
                Token = token,
                User = MapToDto(user)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during user registration");
            return new AuthResponse
            {
                Success = false,
                Message = "Registration failed due to server error",
                Token = null,
                User = null
            };
        }
    }

    /// <summary>Verifies a username-or-Discord-handle credential pair and issues a permission-bearing Atlas JWT.</summary>
    /// <param name="request">The supplied identity and plaintext password.</param>
    /// <returns>A uniform authentication response that does not reveal whether an account exists.</returns>
    /// <remarks>
    /// Failed attempts update persisted lockout state, apply exponential delay, and write security
    /// audit records. Success clears lockout state and persists the last-login timestamp.
    /// </remarks>
    public async Task<AuthResponse?> LoginAsync(LoginRequest request)
    {
        try
        {
            var identity = (request.Username ?? string.Empty).Trim();
            var password = request.Password ?? string.Empty;

            if (string.IsNullOrWhiteSpace(identity) || string.IsNullOrEmpty(password))
            {
                PerformDummyPasswordVerify(password);
                await ApplyFailedLoginDelayAsync(0);
                return InvalidCredentialsResponse();
            }

            // Find user by username or Discord handle
            var user = await _context.Users
                .FirstOrDefaultAsync(u => u.Username == identity || u.DiscordHandle == identity);

            if (user == null || user.IsActive != 1)
            {
                PerformDummyPasswordVerify(password);
                await AuditLoginAsync(
                    action: "auth.login.failed_unknown",
                    user: null,
                    identity: identity,
                    summary: "Login failed for unknown or inactive account");

                await ApplyFailedLoginDelayAsync(0);
                return InvalidCredentialsResponse();
            }

            if (IsLockoutActive(user, out var lockoutUntilUtc))
            {
                PerformDummyPasswordVerify(password);
                _logger.LogWarning(
                    "Blocked login for locked account {UserId} until {LockoutEndUtc}",
                    user.Id,
                    lockoutUntilUtc);

                await AuditLoginAsync(
                    action: "auth.login.locked",
                    user: user,
                    identity: identity,
                    summary: $"Blocked login for locked account until {lockoutUntilUtc:o}");

                await ApplyFailedLoginDelayAsync(_maxFailedLoginAttempts);
                return InvalidCredentialsResponse();
            }

            // Verify password
            var passwordOk = false;
            try
            {
                passwordOk = BCrypt.Net.BCrypt.Verify(password, user.PasswordHash);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Password hash verification failed for user {UserId}", user.Id);
            }

            if (!passwordOk)
            {
                var attemptsBefore = Math.Max(0, user.FailedLoginAttempts);
                var lockoutTriggered = ApplyFailedLogin(user, DateTime.UtcNow);

                await AuditLoginAsync(
                    action: lockoutTriggered ? "auth.login.lockout_triggered" : "auth.login.failed_password",
                    user: user,
                    identity: identity,
                    summary: lockoutTriggered
                        ? $"Login lockout triggered after {_maxFailedLoginAttempts} failed attempts"
                        : "Login failed due to invalid password",
                    extraDetails: new Dictionary<string, object?>
                    {
                        ["failedAttemptsBefore"] = attemptsBefore,
                        ["failedAttemptsAfter"] = user.FailedLoginAttempts,
                        ["lockoutEndUtc"] = user.LockoutEndUtc,
                    });

                await _context.SaveChangesAsync();

                await ApplyFailedLoginDelayAsync(attemptsBefore + 1);
                return InvalidCredentialsResponse();
            }

            // Update last login
            ResetFailedLoginState(user);
            user.LastLoginAt = DateTime.UtcNow.ToString("o");
            await _context.SaveChangesAsync();

            await AuditLoginAsync(
                action: "auth.login.success",
                user: user,
                identity: identity,
                summary: "Login successful");

            // Generate JWT token
            var token = GenerateJwtToken(user);

            return new AuthResponse
            {
                Success = true,
                Message = "Login successful",
                Token = token,
                User = MapToDto(user)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during user login");
            return new AuthResponse
            {
                Success = false,
                Message = "Login failed due to server error",
                Token = null,
                User = null
            };
        }
    }

    /// <summary>Loads an active user's public profile with server-resolved effective permissions.</summary>
    /// <param name="userId">The authenticated account identifier from a validated token.</param>
    /// <returns>The profile with no password hash, or <see langword="null"/> for absent or inactive accounts.</returns>
    public async Task<Atlas.Auth.User?> GetUserProfileAsync(int userId)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null || !(user.IsActive == 1))
            return null;

        return MapToDto(user);
    }

    /// <summary>Persists the current UTC last-login timestamp for an active account.</summary>
    /// <param name="userId">The authenticated account identifier to update.</param>
    /// <returns><see langword="true"/> when saved; otherwise <see langword="false"/>.</returns>
    public async Task<bool> UpdateLastLoginAsync(int userId)
    {
        try
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null || !(user.IsActive == 1))
                return false;

            user.LastLoginAt = DateTime.UtcNow.ToString("o");
            await _context.SaveChangesAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating last login for user {UserId}", userId);
            return false;
        }
    }

    private Atlas.Auth.User MapToDto(ServerUser user)
    {
        var isSuper = user.IsSuperAdmin == 1;
        return new Atlas.Auth.User
        {
            Id = user.Id,
            Username = user.Username,
            DiscordHandle = user.DiscordHandle,
            Role = user.Role,
            IsActive = user.IsActive == 1,
            IsAdmin = user.IsAdmin == 1,
            IsSuperAdmin = isSuper,
            MinecraftUsername = user.MinecraftUsername,
            DiscordId = user.DiscordId,
            Bio = user.Bio,
            TrustLevel = user.TrustLevel,
            AutoApprove = user.AutoApprove == 1,
            Permissions = ResolveEffectivePermissions(user.Role, isSuper),
            CreatedAt = DateTime.TryParse(user.CreatedAt, out var created) ? created : DateTime.MinValue,
            LastLoginAt = DateTime.TryParse(user.LastLoginAt, out var last) ? last : null,
            PasswordHash = string.Empty,
        };
    }

    private static AuthResponse InvalidCredentialsResponse() => new()
    {
        Success = false,
        Message = "Invalid credentials",
        Token = null,
        User = null,
    };

    private static void PerformDummyPasswordVerify(string password)
    {
        var probe = string.IsNullOrWhiteSpace(password) ? " " : password;
        _ = BCrypt.Net.BCrypt.Verify(probe, DummyPasswordHash);
    }

    private static bool IsLockoutActive(ServerUser user, out DateTime? lockoutUntilUtc)
    {
        lockoutUntilUtc = null;
        if (string.IsNullOrWhiteSpace(user.LockoutEndUtc)) return false;

        // Parse as an offset-aware timestamp to avoid locale/timezone ambiguity.
        if (!DateTimeOffset.TryParse(user.LockoutEndUtc, out var parsed))
        {
            // Fail closed for safety if the lockout timestamp is malformed.
            return true;
        }

        var lockoutUtc = parsed.UtcDateTime;
        lockoutUntilUtc = lockoutUtc;
        return lockoutUtc > DateTime.UtcNow;
    }

    private bool ApplyFailedLogin(ServerUser user, DateTime utcNow)
    {
        var attempts = Math.Max(0, user.FailedLoginAttempts) + 1;
        user.FailedLoginAttempts = attempts;
        user.LastFailedLoginAt = utcNow.ToString("o");

        if (attempts < _maxFailedLoginAttempts) return false;

        user.LockoutEndUtc = utcNow.Add(_lockoutDuration).ToString("o");
        user.FailedLoginAttempts = 0;

        _logger.LogWarning(
            "Account lockout triggered for user {UserId} until {LockoutEndUtc}",
            user.Id,
            user.LockoutEndUtc);

        return true;
    }

    private static void ResetFailedLoginState(ServerUser user)
    {
        user.FailedLoginAttempts = 0;
        user.LastFailedLoginAt = null;
        user.LockoutEndUtc = null;
    }

    private async Task ApplyFailedLoginDelayAsync(int failedAttempts)
    {
        var exponent = Math.Clamp(Math.Max(0, failedAttempts), 0, 6);
        var multiplier = 1 << exponent;
        var delayMs = Math.Min(_maxFailedLoginDelayMs, _baseFailedLoginDelayMs * multiplier);
        if (delayMs <= 0) return;
        await Task.Delay(delayMs);
    }

    private async Task AuditLoginAsync(
        string action,
        ServerUser? user,
        string identity,
        string summary,
        Dictionary<string, object?>? extraDetails = null)
    {
        try
        {
            var details = new Dictionary<string, object?>
            {
                ["identity"] = identity,
                ["remoteIp"] = GetRemoteIp(),
                ["userAgent"] = GetUserAgent(),
            };

            if (extraDetails != null)
            {
                foreach (var pair in extraDetails)
                    details[pair.Key] = pair.Value;
            }

            await _audit.LogAsync(
                action,
                "Auth",
                user?.Id ?? 0,
                user?.Id,
                user?.Username,
                summary,
                JsonSerializer.Serialize(details));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write auth audit event {Action}", action);
        }
    }

    private string? GetRemoteIp()
    {
        var ctx = _httpContextAccessor.HttpContext;
        if (ctx == null) return null;

        var forwarded = ctx.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            return forwarded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
        }

        return ctx.Connection.RemoteIpAddress?.ToString();
    }

    private string? GetUserAgent()
    {
        var ctx = _httpContextAccessor.HttpContext;
        if (ctx == null) return null;
        var ua = ctx.Request.Headers.UserAgent.ToString();
        return string.IsNullOrWhiteSpace(ua) ? null : ua;
    }

    /// <summary>
    /// Effective permissions for a role: SuperAdmin gets everything; otherwise DB
    /// <c>RolePermissions</c> rows for the role REPLACE the code default when present,
    /// else the code-defined bundle (<see cref="RolePermissions.ForRole"/>) applies.
    /// </summary>
    private List<string> ResolveEffectivePermissions(string role, bool isSuper)
    {
        if (isSuper) return Atlas.Auth.Permissions.All.ToList();
        var overrides = _context.RolePermissions
            .Where(rp => rp.Role == role)
            .Select(rp => rp.Permission)
            .ToList();
        if (overrides.Count > 0) return overrides;
        return RolePermissions.ForRole(role, false).ToList();
    }

    private string GenerateJwtToken(ServerUser user)
    {
        var jwtSettings = _configuration.GetSection("JwtSettings");
        var secretKey = jwtSettings["SecretKey"] ?? throw new InvalidOperationException("JWT SecretKey not configured");
        var issuer = jwtSettings["Issuer"] ?? "2b2tAtlas";
        var audience = jwtSettings["Audience"] ?? "2b2tAtlas";
        var expiryMinutes = int.Parse(jwtSettings["ExpiryMinutes"] ?? "60");

        var key = Encoding.UTF8.GetBytes(secretKey);
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role),
            new Claim("atlas_session", AtlasSessionValidator.Stamp(user, secretKey)),
        };
        if (!string.IsNullOrWhiteSpace(user.DiscordHandle))
            claims.Add(new Claim("discord_handle", user.DiscordHandle));
        if (user.IsSuperAdmin == 1)
            claims.Add(new Claim("superadmin", "true"));
        // Effective permissions resolved from the role (DB overrides or code default)
        // → one claim each so the server's per-permission authorization policies match.
        foreach (var perm in ResolveEffectivePermissions(user.Role, user.IsSuperAdmin == 1))
            claims.Add(new Claim("perm", perm));

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddMinutes(expiryMinutes),
            Issuer = issuer,
            Audience = audience,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256)
        };

        var tokenHandler = new JwtSecurityTokenHandler();
        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }

    private static string? NormalizeDiscordHandle(string? discordHandle) =>
        string.IsNullOrWhiteSpace(discordHandle) ? null : discordHandle.Trim();

    /// <summary>Cryptographically validates an Atlas JWT against the configured issuer, audience, key, and lifetime.</summary>
    /// <param name="token">The compact JWT supplied by an untrusted caller.</param>
    /// <returns>The validated claims principal, or <see langword="null"/> for any invalid token.</returns>
    public ClaimsPrincipal? ValidateToken(string token)
    {
        try
        {
            var jwtSettings = _configuration.GetSection("JwtSettings");
            var secretKey = jwtSettings["SecretKey"] ?? throw new InvalidOperationException("JWT SecretKey not configured");
            var issuer = jwtSettings["Issuer"] ?? "2b2tAtlas";
            var audience = jwtSettings["Audience"] ?? "2b2tAtlas";

            var key = Encoding.UTF8.GetBytes(secretKey);
            var tokenHandler = new JwtSecurityTokenHandler();

            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(key),
                ValidateIssuer = true,
                ValidIssuer = issuer,
                ValidateAudience = true,
                ValidAudience = audience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero
            };

            var principal = tokenHandler.ValidateToken(token, validationParameters, out SecurityToken validatedToken);
            return principal;
        }
        catch
        {
            return null;
        }
    }
}
