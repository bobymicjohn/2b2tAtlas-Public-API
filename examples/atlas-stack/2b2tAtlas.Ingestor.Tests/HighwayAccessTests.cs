using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Atlas.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using _2b2tAtlas.Server.Controllers;
using _2b2tAtlas.Server.Models;
using _2b2tAtlas.Server.Services;

namespace Atlas.Ingestor.Tests;

public sealed class HighwayAccessTests
{
    [Fact]
    public async Task Real_bearer_pipeline_limits_architect_to_highways_and_revokes_sessions_immediately()
    {
        var ct = TestContext.Current.CancellationToken;
        const string key = "isolated-highway-access-fixture-signing-key-never-production";
        var root = Path.Combine(Path.GetTempPath(), "atlas-highway-access-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var dbPath = Path.Combine(root, "atlas.db");
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing", ContentRootPath = AppContext.BaseDirectory });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["JwtSettings:SecretKey"] = key, ["JwtSettings:Issuer"] = "fixture", ["JwtSettings:Audience"] = "fixture",
                ["Recovery:DatabasePath"] = dbPath, ["Recovery:Root"] = Path.Combine(root, "backups")
            });
            builder.Services.AddControllers().AddApplicationPart(typeof(HighwaysController).Assembly);
            builder.Services.AddDbContext<AtlasContext>(options => options.UseSqlite($"Data Source={dbPath};Pooling=False"));
            builder.Services.AddAtlasAuthentication(builder.Configuration);
            builder.Services.AddScoped<AuditService>();
            builder.Services.AddSingleton<AtlasRecoveryStore>();
            await using var app = builder.Build();
            var user = new _2b2tAtlas.Server.Models.User { Id = 2, Username = "hwu-test", Role = RoleNames.HighwayArchitect, IsActive = 1, PasswordHash = "fixture-password-digest", CreatedAt = DateTime.UtcNow.ToString("o") };
            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AtlasContext>();
                await db.Database.EnsureCreatedAsync(ct);
                db.Users.Add(user);
                db.Highways.Add(new _2b2tAtlas.Server.Models.Highway { Id = 1, Name = "Test highway", PointsJson = "[[0,0],[0,10000]]" });
                await db.SaveChangesAsync(ct);
            }
            app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseMiddleware<AtlasWriteProtection>(); app.MapControllers();
            await app.StartAsync(ct);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("api/highways/history", ct)).StatusCode);
            var jwt = new JwtSecurityToken("fixture", "fixture", [new Claim(ClaimTypes.NameIdentifier, "2"),
                new Claim("atlas_session", AtlasSessionValidator.Stamp(user, key)), new Claim("superadmin", "true")],
                expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("api/highways/all", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("api/highways/history", ct)).StatusCode);
            foreach (var url in new[] { "api/audit", "api/admin/users", "api/roles" })
                Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(url, ct)).StatusCode);
            foreach (var url in new[] { "api/highways/1", "api/locations/1", "api/groups/1" })
                Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync(url, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("api/highways/history/1/restore", new Atlas.HighwayRestoreRequest(), ct)).StatusCode);
            var highway = await client.GetFromJsonAsync<Atlas.Highway>("api/highways/1", ct);
            highway!.Width = 7;
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("api/highways/1", highway, ct)).StatusCode);
            Assert.Single(Directory.GetFiles(Path.Combine(root, "backups"), "*.db", SearchOption.AllDirectories));
            Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync("api/highways/1", highway, ct)).StatusCode);
            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AtlasContext>();
                (await db.Users.SingleAsync(ct)).IsActive = 0;
                await db.SaveChangesAsync(ct);
            }
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("api/highways/all", ct)).StatusCode);
            await app.StopAsync(ct);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
