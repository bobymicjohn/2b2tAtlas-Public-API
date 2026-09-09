using System.Security.Claims;
using Atlas.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using _2b2tAtlas.Server.Controllers;
using _2b2tAtlas.Server.Models;
using _2b2tAtlas.Server.Services;

namespace Atlas.Ingestor.Tests;

public sealed class AdminRoleEnforcementTests
{
    [Fact]
    public async Task UpdateUser_clears_discord_handle_when_blank()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var user = new _2b2tAtlas.Server.Models.User
        {
            Username = "member",
            Id = 20, // Account 1 is reserved for atlas-owner.
            DiscordHandle = "@member",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Test-password-123!"),
            Role = RoleNames.User,
            IsActive = 1,
            CreatedAt = DateTime.UtcNow.ToString("o"),
        };
        context.Users.Add(user);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var controller = CreateArchivistController(context);

        var response = await controller.UpdateUser(user.Id, new UpdateUserRequest
        {
            Username = user.Username,
            DiscordHandle = "   ",
            Role = user.Role,
            IsActive = true,
        });

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        Assert.Null(Assert.IsType<Atlas.Auth.User>(ok.Value).DiscordHandle);
        Assert.Null((await context.Users.FindAsync([user.Id], TestContext.Current.CancellationToken))!.DiscordHandle);
    }

    [Fact]
    public async Task Archivist_can_assign_specialist_but_not_peer_or_unknown_role()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var controller = CreateArchivistController(context);

        var specialist = await controller.CreateUser(new CreateUserRequest
        {
            Username = "specialist",
            Password = "Test-password-123!",
            Role = RoleNames.Cartographer,
            IsActive = true,
        });
        var created = Assert.IsType<CreatedAtActionResult>(specialist.Result);
        Assert.Equal(RoleNames.Cartographer, Assert.IsType<Atlas.Auth.User>(created.Value).Role);

        var peer = await controller.CreateUser(new CreateUserRequest
        {
            Username = "peer",
            Password = "Test-password-123!",
            Role = RoleNames.Admin,
        });
        Assert.IsType<ForbidResult>(peer.Result);

        var unknown = await controller.CreateUser(new CreateUserRequest
        {
            Username = "legacy",
            Password = "Test-password-123!",
            Role = "Technician",
        });
        Assert.IsType<BadRequestObjectResult>(unknown.Result);
    }

    private static AdminController CreateArchivistController(AtlasContext context)
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "20"),
            new Claim(ClaimTypes.Name, "archivist_test"),
            new Claim(ClaimTypes.Role, RoleNames.Admin),
            new Claim("perm", Permissions.UsersManage),
            new Claim("perm", Permissions.UsersRolesAssign),
        }, "test");
        return new AdminController(context, new AuditService(context), NullLogger<AdminController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
            },
        };
    }
}
