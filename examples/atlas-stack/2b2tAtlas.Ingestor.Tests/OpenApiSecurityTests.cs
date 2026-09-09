using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Atlas.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using _2b2tAtlas.Server.Controllers;
using _2b2tAtlas.Server.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using _2b2tAtlas.Server.Models;

namespace Atlas.Ingestor.Tests;

public sealed class OpenApiSecurityTests
{
    // Only this isolated host accepts the fixture key; no production config, DB,
    // user, credentials, workers or controller dependencies are loaded.
    private const string Key = "isolated-openapi-test-key-at-least-32-characters";

    [Fact]
    public async Task Documents_and_every_controller_authorization_boundary_are_consistent()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing", ContentRootPath = AppContext.BaseDirectory,
            ApplicationName = typeof(ApiIndexController).Assembly.FullName
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["JwtSettings:SecretKey"] = Key,
            ["JwtSettings:Issuer"] = "security-fixture", ["JwtSettings:Audience"] = "security-fixture"
        });
        builder.Services.AddControllers().AddApplicationPart(typeof(ApiIndexController).Assembly);
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        builder.Services.AddDbContext<AtlasContext>(options => options.UseSqlite(connection));
        builder.Services.AddAtlasAuthentication(builder.Configuration);
        builder.Services.AddAtlasOpenApi();
        await using var app = builder.Build();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AtlasContext>();
            await db.Database.EnsureCreatedAsync(ct);
            db.Users.AddRange(FixtureUser(false), FixtureUser(true));
            await db.SaveChangesAsync(ct);
        }
        app.MapAtlasOpenApi();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync(ct);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        var descriptions = app.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>()
            .ApiDescriptionGroups.Items.SelectMany(g => g.Items).ToArray();
        Assert.True(descriptions.Length > 70);

        using var publicResponse = await client.GetAsync("/openapi/v1.json", ct);
        Assert.Equal(HttpStatusCode.OK, publicResponse.StatusCode);
        using var publicDoc = JsonDocument.Parse(await publicResponse.Content.ReadAsStringAsync(ct));
        var publicPaths = publicDoc.RootElement.GetProperty("paths");
        Assert.True(publicPaths.TryGetProperty("/api/Locations", out _));
        Assert.False(publicPaths.TryGetProperty("/api/Admin/users", out _));
        Assert.DoesNotContain("IngestionJobRequest", publicDoc.RootElement.GetRawText());
        Assert.DoesNotContain("CreateUserRequest", publicDoc.RootElement.GetRawText());
        foreach (var path in publicPaths.EnumerateObject())
            Assert.All(path.Value.EnumerateObject(), method => Assert.Equal("get", method.Name));

        using var anonymousInternal = await client.GetAsync("/openapi/internal.json", ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousInternal.StatusCode);
        Assert.True(anonymousInternal.Headers.CacheControl?.NoStore);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token());
        using var viewerInternal = await client.GetAsync("/openapi/internal.json", ct);
        Assert.Equal(HttpStatusCode.Forbidden, viewerInternal.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(Permissions.UsersManage));
        using var internalResponse = await client.GetAsync("/openapi/internal.json", ct);
        Assert.Equal(HttpStatusCode.OK, internalResponse.StatusCode);
        Assert.True(internalResponse.Headers.CacheControl?.NoStore);
        using var internalDoc = JsonDocument.Parse(await internalResponse.Content.ReadAsStringAsync(ct));
        var full = internalDoc.RootElement;
        var schemes = full.GetProperty("components").GetProperty("securitySchemes");
        Assert.Equal("bearer", schemes.GetProperty("Bearer").GetProperty("scheme").GetString());
        Assert.Equal("X-Atlas-Worker-Key", schemes.GetProperty("AtlasWorkerKey").GetProperty("name").GetString());

        var protectedCount = 0;
        var workerCount = 0;
        foreach (var description in descriptions)
        {
            var metadata = description.ActionDescriptor.EndpointMetadata;
            var auth = metadata.OfType<IAuthorizeData>().ToArray();
            var anonymous = metadata.OfType<IAllowAnonymous>().Any();
            var worker = metadata.OfType<AtlasWorkerKeyAttribute>().Any();
            // An anonymous attribute overrides authorization in ASP.NET; forbid
            // accidentally combining them on any real controller action.
            Assert.False(anonymous && auth.Length > 0, description.RelativePath);
            var path = "/" + description.RelativePath;
            var operation = full.GetProperty("paths").GetProperty(path).GetProperty(description.HttpMethod!.ToLowerInvariant());
            var published = publicPaths.TryGetProperty(path, out var publicPath)
                && publicPath.TryGetProperty(description.HttpMethod.ToLowerInvariant(), out _);
            Assert.Equal(AtlasOpenApi.IsPublicRead(description), published);
            if (worker)
            {
                workerCount++;
                Assert.True(operation.GetProperty("security")[0].TryGetProperty("AtlasWorkerKey", out _));
                Assert.False(published);
            }
            else if (auth.Length > 0)
            {
                protectedCount++;
                Assert.True(operation.GetProperty("security")[0].TryGetProperty("Bearer", out _));
                Assert.True(operation.GetProperty("responses").TryGetProperty("401", out _));
                var requestPath = Regex.Replace(path, "\\{[^}]+\\}", "999999");
                client.DefaultRequestHeaders.Authorization = null;
                using var denied = await client.SendAsync(new HttpRequestMessage(new HttpMethod(description.HttpMethod), requestPath), ct);
                Assert.True(denied.StatusCode == HttpStatusCode.Unauthorized, $"Anonymous {description.HttpMethod} {path}: {denied.StatusCode}");
                if (auth.Any(a => !string.IsNullOrWhiteSpace(a.Policy)))
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token());
                    using var forbidden = await client.SendAsync(new HttpRequestMessage(new HttpMethod(description.HttpMethod), requestPath), ct);
                    Assert.True(forbidden.StatusCode == HttpStatusCode.Forbidden, $"Viewer {description.HttpMethod} {path}: {forbidden.StatusCode}");
                }
            }
            else
            {
                Assert.False(operation.TryGetProperty("security", out var security) && security.GetArrayLength() > 0);
                if (description.HttpMethod != "GET")
                    Assert.Contains(path, new[] { "/api/Auth/login", "/api/Auth/register", "/api/newWarp.php" });
            }
        }
        Assert.True(protectedCount > 40);
        Assert.Equal(4, workerCount);

        foreach (var token in new[] { "invalid", Token(expired: true), Token(key: Key + "wrong") })
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var denied = await client.GetAsync("/api/admin/collector", ct);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        client.DefaultRequestHeaders.Authorization = null;
        using var unknown = await client.GetAsync("/openapi/unknown.json", ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var query = await client.GetAsync("/openapi/v1.json?documentName=internal", ct);
        Assert.Equal(HttpStatusCode.OK, query.StatusCode);
        Assert.DoesNotContain("/api/Admin/users", await query.Content.ReadAsStringAsync(ct));
        await app.StopAsync(ct);
    }

    private static string Token(string? permission = null, bool expired = false, string key = Key)
    {
        var now = DateTime.UtcNow;
        var user = FixtureUser(permission != null);
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new("atlas_session", AtlasSessionValidator.Stamp(user, Key)) };
        if (permission != null) claims.Add(new Claim("perm", permission));
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            "security-fixture", "security-fixture", claims, now.AddMinutes(-5),
            expired ? now.AddMinutes(-1) : now.AddMinutes(5),
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256)));
    }

    private static _2b2tAtlas.Server.Models.User FixtureUser(bool admin) => new()
    {
        Id = admin ? 3 : 2, Username = admin ? "fixture-archivist" : "fixture-viewer",
        Role = admin ? RoleNames.Admin : RoleNames.User, IsActive = 1,
        PasswordHash = "isolated-fixture-password-digest", CreatedAt = DateTime.UtcNow.ToString("o")
    };
}
