using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using _2b2tAtlas.Server.Models;
using _2b2tAtlas.Server.Services;
using Atlas.Auth;
using BCrypt.Net;
using ServerUser = _2b2tAtlas.Server.Models.User;

namespace _2b2tAtlas.Server.Controllers
{
    /// <summary>
    /// Provides authenticated Atlas account administration and aggregate database statistics.
    /// </summary>
    /// <remarks>
    /// Every route requires <c>users.manage</c>. Role assignment is additionally constrained by
    /// the caller's role rank, and only the master SuperAdmin may manage another SuperAdmin.
    /// Mutations persist immediately and append an immutable audit entry.
    /// </remarks>
    [ApiController]
    [Route("api/[controller]")]
    [Authorize(Policy = Permissions.UsersManage)]
    public class AdminController : ControllerBase
    {
        private readonly AtlasContext _context;
        private readonly AuditService _audit;
        private readonly ILogger<AdminController> _logger;

        /// <summary>Initializes the account-administration API.</summary>
        /// <param name="context">The Atlas persistence context used for account and statistics queries.</param>
        /// <param name="audit">The append-only recorder for account mutations.</param>
        /// <param name="logger">The diagnostic logger for administrative operations.</param>
        public AdminController(AtlasContext context, AuditService audit, ILogger<AdminController> logger)
        {
            _context = context;
            _audit = audit;
            _logger = logger;
        }

        private int? CurrentUserId() =>
            int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var uid) ? uid : null;

        private string? CurrentUsername() => User.FindFirstValue(ClaimTypes.Name);

        /// <summary>Whether the calling user is the master SuperAdmin.</summary>
        private bool CallerIsSuperAdmin => User.HasClaim("superadmin", "true");

        private bool CallerCanAssignRoles => CallerIsSuperAdmin || User.HasClaim("perm", Permissions.UsersRolesAssign);

        private string? CallerRole => User.FindFirstValue(ClaimTypes.Role);

        private bool CanManageUserRole(string role) =>
            CallerIsSuperAdmin || RoleNames.Rank(role) < RoleNames.Rank(CallerRole);

        #region User Management CRUD

        /// <summary>
        /// GET /api/admin/users - Get all users
        /// </summary>
        [HttpGet("users")]
        public async Task<ActionResult<IEnumerable<Atlas.Auth.User>>> GetUsers()
        {
            _logger.LogInformation("GetUsers called");

            try
            {
                var serverUsers = await _context.Users
                    .OrderBy(u => u.Username)
                    .ToListAsync();

                var users = serverUsers.Select(MapToSharedUser).ToList();
                return Ok(users);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving users");
                return StatusCode(500, "Error retrieving users");
            }
        }

        /// <summary>
        /// GET /api/admin/users/{id} - Get user by ID
        /// </summary>
        [HttpGet("users/{id}")]
        public async Task<ActionResult<Atlas.Auth.User>> GetUser(int id)
        {
            _logger.LogInformation("GetUser called for ID: {UserId}", id);

            try
            {
                var serverUser = await _context.Users.FindAsync(id);
                if (serverUser == null)
                {
                    return NotFound($"User with ID {id} not found");
                }

                return Ok(MapToSharedUser(serverUser));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving user with ID {UserId}", id);
                return StatusCode(500, "Error retrieving user");
            }
        }

        /// <summary>
        /// POST /api/admin/users - Create a new user
        /// </summary>
        [HttpPost("users")]
        public async Task<ActionResult<Atlas.Auth.User>> CreateUser([FromBody] CreateUserRequest request)
        {
            _logger.LogInformation("CreateUser called for username: {Username}", request?.Username);

            if (request == null)
            {
                return BadRequest("Request body is required");
            }

            if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            {
                return BadRequest("Username and password are required");
            }

            try
            {
                var requestedRole = request.Role ?? RoleNames.User;
                if (!RoleNames.TryNormalize(requestedRole, out var canonicalRole))
                    return BadRequest("Unknown role.");
                if (canonicalRole == RoleNames.SuperAdmin)
                    return BadRequest("Founder is reserved for the existing atlas-owner account.");
                if (canonicalRole != RoleNames.User && !CallerCanAssignRoles)
                    return Forbid();
                if (!RoleNames.CanAssign(CallerRole, canonicalRole, CallerIsSuperAdmin))
                {
                    return Forbid();
                }

                // Check if user already exists
                var discordHandle = NormalizeDiscordHandle(request.DiscordHandle);
                var existingUser = await _context.Users
                    .FirstOrDefaultAsync(u => u.Username == request.Username ||
                        (discordHandle != null && u.DiscordHandle == discordHandle));

                if (existingUser != null)
                {
                    return Conflict($"User with username '{request.Username}' or Discord handle '{request.DiscordHandle}' already exists");
                }

                // Create new server user
                var serverUser = new ServerUser
                {
                    Username = request.Username,
                    DiscordHandle = discordHandle,
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
                    Role = canonicalRole,
                    IsActive = request.IsActive?.ToInt() ?? 1,
                    IsAdmin = canonicalRole is RoleNames.Admin or RoleNames.SuperAdmin ? 1 : 0,
                    CreatedAt = DateTime.UtcNow.ToString("o")
                };

                _context.Users.Add(serverUser);
                await _context.SaveChangesAsync();

                await _audit.LogAsync("user.create", "User", serverUser.Id, CurrentUserId(), CurrentUsername(),
                    $"Created user '{serverUser.Username}' (role {serverUser.Role})");

                _logger.LogInformation("User '{Username}' created successfully with ID {UserId}", serverUser.Username, serverUser.Id);

                var createdUser = MapToSharedUser(serverUser);
                return CreatedAtAction(nameof(GetUser), new { id = serverUser.Id }, createdUser);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating user '{Username}'", request.Username);
                return StatusCode(500, "Error creating user");
            }
        }

        /// <summary>
        /// PUT /api/admin/users/{id} - Update an existing user
        /// </summary>
        [HttpPut("users/{id}")]
        public async Task<ActionResult<Atlas.Auth.User>> UpdateUser(int id, [FromBody] UpdateUserRequest request)
        {
            _logger.LogInformation("UpdateUser called for user ID: {UserId}", id);

            if (request == null)
            {
                return BadRequest("Request body is required");
            }

            if (string.IsNullOrWhiteSpace(request.Username))
            {
                return BadRequest("Username is required");
            }

            try
            {
                var serverUser = await _context.Users.FindAsync(id);
                if (serverUser == null)
                {
                    return NotFound($"User with ID {id} not found");
                }

                // A SuperAdmin can only be modified by a SuperAdmin, and only a
                // SuperAdmin may assign the SuperAdmin role (no privilege escalation).
                if (serverUser.IsSuperAdmin == 1 && !CallerIsSuperAdmin)
                {
                    return Forbid();
                }
                if (!CanManageUserRole(serverUser.Role))
                {
                    return Forbid();
                }

                var requestedRole = request.Role ?? serverUser.Role;
                if (!RoleNames.TryNormalize(requestedRole, out var canonicalRole))
                    return BadRequest("Unknown role.");
                if (id == 1 && (request.Username != "atlas-owner" || canonicalRole != RoleNames.SuperAdmin || request.IsActive == false))
                    return BadRequest("The owner account cannot be renamed, demoted or disabled.");
                if (id != 1 && canonicalRole == RoleNames.SuperAdmin)
                    return BadRequest("Founder is reserved for atlas-owner.");
                if (!string.Equals(canonicalRole, serverUser.Role, StringComparison.Ordinal))
                {
                    if (!CallerCanAssignRoles || !RoleNames.CanAssign(CallerRole, canonicalRole, CallerIsSuperAdmin))
                        return Forbid();
                }

                var discordHandle = NormalizeDiscordHandle(request.DiscordHandle);

                // Check for username/Discord handle conflicts with other users
                var conflictingUser = await _context.Users
                    .Where(u => u.Id != id && (u.Username == request.Username ||
                        (discordHandle != null && u.DiscordHandle == discordHandle)))
                    .FirstOrDefaultAsync();

                if (conflictingUser != null)
                {
                    return Conflict($"Another user with username '{request.Username}' or Discord handle '{request.DiscordHandle}' already exists");
                }

                // Update user properties
                serverUser.Username = request.Username;
                serverUser.DiscordHandle = discordHandle;

                serverUser.Role = canonicalRole;

                if (request.IsActive.HasValue)
                    serverUser.IsActive = request.IsActive.Value.ToInt();

                serverUser.IsAdmin = canonicalRole is RoleNames.Admin or RoleNames.SuperAdmin ? 1 : 0;

                // Update password if provided
                if (!string.IsNullOrWhiteSpace(request.NewPassword))
                {
                    serverUser.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
                }

                await _context.SaveChangesAsync();

                await _audit.LogAsync("user.update", "User", serverUser.Id, CurrentUserId(), CurrentUsername(),
                    $"Updated user '{serverUser.Username}'");

                _logger.LogInformation("User '{Username}' (ID: {UserId}) updated successfully", serverUser.Username, serverUser.Id);

                return Ok(MapToSharedUser(serverUser));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating user with ID {UserId}", id);
                return StatusCode(500, "Error updating user");
            }
        }

        /// <summary>
        /// DELETE /api/admin/users/{id} - Delete a user
        /// </summary>
        [HttpDelete("users/{id}")]
        public async Task<IActionResult> DeleteUser(int id)
        {
            if (id == 1) return BadRequest("The owner account cannot be deleted.");
            _logger.LogInformation("DeleteUser called for user ID: {UserId}", id);

            try
            {
                var serverUser = await _context.Users.FindAsync(id);
                if (serverUser == null)
                {
                    return NotFound($"User with ID {id} not found");
                }

                // The SuperAdmin account cannot be deleted (except by a SuperAdmin).
                if (serverUser.IsSuperAdmin == 1 && !CallerIsSuperAdmin)
                {
                    return Forbid();
                }
                if (!CanManageUserRole(serverUser.Role))
                {
                    return Forbid();
                }

                var username = serverUser.Username;
                _context.Users.Remove(serverUser);
                await _context.SaveChangesAsync();

                await _audit.LogAsync("user.delete", "User", id, CurrentUserId(), CurrentUsername(),
                    $"Deleted user '{username}'");

                _logger.LogInformation("User '{Username}' (ID: {UserId}) deleted successfully", username, id);

                return NoContent();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting user with ID {UserId}", id);
                return StatusCode(500, "Error deleting user");
            }
        }

        #endregion

        #region Database Statistics

        /// <summary>
        /// GET /api/admin/stats - Get database statistics
        /// </summary>
        [HttpGet("stats")]
        public async Task<ActionResult<DatabaseStats>> GetDatabaseStats()
        {
            try
            {
                // Calculate one month ago as ISO string for comparison
                var oneMonthAgo = DateTime.UtcNow.AddMonths(-1).ToString("o");

                var stats = new DatabaseStats
                {
                    Locations = await _context.Locations.CountAsync(),
                    Warps = await _context.Warps.CountAsync(),
                    Attachments = await _context.Attachments.CountAsync(),
                    Renders = await _context.Renders.CountAsync(),
                    LocationsMonth = await _context.Locations
                        .Where(l => string.Compare(l.DateAddedUtc, oneMonthAgo) >= 0)
                        .CountAsync(),
                    WarpsMonth = await _context.Warps
                        .Where(w => string.Compare(w.TimeAdded, oneMonthAgo) >= 0)
                        .CountAsync(),
                    AttachmentsMonth = await _context.Attachments
                        .Where(a => string.Compare(a.DateAddedUtc, oneMonthAgo) >= 0)
                        .CountAsync(),
                    RendersMonth = await _context.Renders
                        .Where(r => string.Compare(r.DateAddedUtc, oneMonthAgo) >= 0)
                        .CountAsync()
                };

                return Ok(stats);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting database stats");
                return StatusCode(500, "Error retrieving database statistics");
            }
        }


        #endregion

        #region Utility Methods

        private static string? NormalizeDiscordHandle(string? discordHandle) =>
            string.IsNullOrWhiteSpace(discordHandle) ? null : discordHandle.Trim();

        private static Atlas.Auth.User MapToSharedUser(ServerUser serverUser)
        {
            var isSuper = serverUser.IsSuperAdmin == 1;
            return new Atlas.Auth.User
            {
                Id = serverUser.Id,
                Username = serverUser.Username,
                DiscordHandle = serverUser.DiscordHandle,
                Role = serverUser.Role,
                IsActive = serverUser.IsActive == 1,
                IsAdmin = serverUser.IsAdmin == 1,
                IsSuperAdmin = isSuper,
                MinecraftUsername = serverUser.MinecraftUsername,
                DiscordId = serverUser.DiscordId,
                Bio = serverUser.Bio,
                TrustLevel = serverUser.TrustLevel,
                AutoApprove = serverUser.AutoApprove == 1,
                Permissions = RolePermissions.ForRole(serverUser.Role, isSuper).ToList(),
                CreatedAt = DateTime.TryParse(serverUser.CreatedAt, out var createdAt) ? createdAt : DateTime.MinValue,
                LastLoginAt = DateTime.TryParse(serverUser.LastLoginAt, out var lastLogin) ? lastLogin : null,
                // Don't expose PasswordHash
                PasswordHash = string.Empty
            };
        }

        #endregion
    }

    #region Extension Methods

    /// <summary>Converts application booleans to the integer representation used by the legacy SQLite schema.</summary>
    public static class BoolExtensions
    {
        /// <summary>Converts a Boolean value to the persisted Atlas flag representation.</summary>
        /// <param name="value">The application-level flag.</param>
        /// <returns><c>1</c> when true; otherwise <c>0</c>.</returns>
        public static int ToInt(this bool value) => value ? 1 : 0;
    }

    #endregion
}
