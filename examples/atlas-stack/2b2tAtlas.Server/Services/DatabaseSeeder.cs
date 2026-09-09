using Atlas.Auth;
using Microsoft.EntityFrameworkCore;
using _2b2tAtlas.Server.Models;

namespace _2b2tAtlas.Server.Services;

/// <summary>Creates one local owner on a fresh database; never promotes existing accounts.</summary>
public sealed class DatabaseSeeder(AtlasContext context, IConfiguration config, ILogger<DatabaseSeeder> logger)
{
    /// <summary>Bootstrap requires a private operator-supplied password. Restarts never reset it.</summary>
    public async Task SeedDefaultUsersAsync()
    {
        if (await context.Users.AnyAsync()) return;
        var password = config["Bootstrap:OwnerPassword"];
        if (string.IsNullOrWhiteSpace(password) || password.Length < 16)
            throw new InvalidOperationException("A fresh database requires Bootstrap__OwnerPassword (at least 16 characters). Run scripts/start-example.ps1 to generate local credentials.");
        context.Users.Add(new _2b2tAtlas.Server.Models.User
        {
            Id = 1, Username = "atlas-owner", PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            Role = RoleNames.SuperAdmin, IsSuperAdmin = 1, IsAdmin = 1, IsActive = 1,
            CreatedAt = DateTime.UtcNow.ToString("o")
        });
        await context.SaveChangesAsync();
        logger.LogInformation("Created local atlas-owner. No contributor accounts were provisioned.");
    }
}
