using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Atlas.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using _2b2tAtlas.Server.Models;
using _2b2tAtlas.Server.Services;

namespace Atlas.Ingestor.Tests;

public sealed class AtlasRecoverySecurityTests
{
    private const string Key = "isolated-recovery-fixture-signing-key-long-enough";

    [Fact]
    public async Task Revocation_current_roles_and_owner_identity_override_even_signed_privileged_claims()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var db = new AtlasContext(new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var owner = Account(1, "atlas-owner", RoleNames.SuperAdmin, 1);
        var archivist = Account(2, "terbin", RoleNames.Admin);
        var legacy = Account(3, "Hausemaster", RoleNames.SuperAdmin, 1);
        db.Users.AddRange(owner, archivist, legacy);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var validator = new AtlasSessionValidator(db, Config());
        var token = Principal(archivist);
        var validated = await validator.ValidateAsync(token, TestContext.Current.CancellationToken);
        Assert.NotNull(validated);
        Assert.False(AtlasSessionValidator.IsOwner(validated));
        Assert.False(validated.HasClaim("superadmin", "true"));
        Assert.True(validated.HasClaim("perm", Permissions.LocationsEdit));
        Assert.False(validated.HasClaim("perm", Permissions.LocationsDelete));
        Assert.True(AtlasSessionValidator.IsOwner((await validator.ValidateAsync(Principal(owner), TestContext.Current.CancellationToken))!));
        Assert.Null(await validator.ValidateAsync(Principal(legacy), TestContext.Current.CancellationToken));

        // Editable overrides cannot give an Archivist destructive powers.
        db.RolePermissions.Add(new RolePermission { Role = RoleNames.Admin, Permission = Permissions.LocationsDelete });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        validated = await validator.ValidateAsync(token, TestContext.Current.CancellationToken);
        Assert.False(validated!.HasClaim("perm", Permissions.LocationsDelete));
        Assert.False(validated.HasClaim("perm", Permissions.LocationsEdit));
        archivist.IsActive = 0;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.Null(await validator.ValidateAsync(token, TestContext.Current.CancellationToken));
        archivist.IsActive = 1; archivist.PasswordHash = "changed-password-digest";
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.Null(await validator.ValidateAsync(token, TestContext.Current.CancellationToken));
        Assert.NotNull(await validator.ValidateAsync(Principal(archivist), TestContext.Current.CancellationToken));
        var legacyToken = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "2")], "Bearer"));
        Assert.Null(await validator.ValidateAsync(legacyToken, TestContext.Current.CancellationToken));
        owner.Username = "renamed-owner";
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.Null(await validator.ValidateAsync(Principal(owner), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("DELETE", "Locations", "DeleteLocation")]
    [InlineData("DELETE", "Groups", "Delete")]
    [InlineData("DELETE", "Highways", "Delete")]
    [InlineData("DELETE", "FutureController", "FutureDelete")]
    [InlineData("PUT", "Admin", "UpdateUser")]
    [InlineData("POST", "Admin", "CreateUser")]
    [InlineData("PUT", "Roles", "Update")]
    [InlineData("PUT", "MapRenders", "Upsert")]
    [InlineData("POST", "RenderSettings", "RerenderAll")]
    [InlineData("POST", "EnrichmentAdmin", "Run")]
    [InlineData("POST", "EnrichmentAdmin", "RunGroupDiscovery")]
    [InlineData("POST", "IngestionJobs", "StartUploadSession")]
    public async Task Destructive_or_resource_heavy_writes_are_denied_before_execution(string method, string controller, string action)
    {
        var http = new DefaultHttpContext();
        http.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        http.Response.Body = new MemoryStream();
        http.Request.Method = method;
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "2"),
            new Claim("superadmin", "true"), new Claim("perm", Permissions.LocationsDelete)], "Bearer"));
        http.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(
            new ControllerActionDescriptor { ControllerName = controller, ActionName = action }), "fixture"));
        var executed = false;
        await new AtlasWriteProtection(_ => { executed = true; return Task.CompletedTask; })
            .InvokeAsync(http, new AtlasRecoveryStore(Config()), NullLogger<AtlasWriteProtection>.Instance);
        Assert.Equal(403, http.Response.StatusCode);
        Assert.False(executed);
    }

    [Fact]
    public async Task Pre_edit_snapshot_restores_deleted_rows_and_limits_survive_service_restart()
    {
        var root = Path.Combine(Path.GetTempPath(), "atlas-recovery-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var livePath = Path.Combine(root, "live.db");
        var backupRoot = Path.Combine(root, "backups");
        var config = Config(new() { ["Recovery:DatabasePath"] = livePath, ["Recovery:Root"] = backupRoot });
        try
        {
            using (var db = new SqliteConnection($"Data Source={livePath};Pooling=False"))
            {
                db.Open(); using var command = db.CreateCommand();
                command.CommandText = "CREATE TABLE Records(Id INTEGER PRIMARY KEY, Value TEXT); INSERT INTO Records VALUES(1,'preserved render reference');";
                command.ExecuteNonQuery();
            }
            var recovery = new AtlasRecoveryStore(config);
            for (var i = 0; i < 10; i++)
                Assert.True(await recovery.BeforeWriteAsync(2, false, "PUT Locations.Put", TestContext.Current.CancellationToken));
            Assert.False(await new AtlasRecoveryStore(config).BeforeWriteAsync(2, false, "PUT Locations.Put", TestContext.Current.CancellationToken));
            Assert.True(await recovery.BeforeWriteAsync(1, true, "owner edit", TestContext.Current.CancellationToken));
            using (var db = new SqliteConnection($"Data Source={livePath};Pooling=False"))
            {
                db.Open(); using var command = db.CreateCommand(); command.CommandText = "DELETE FROM Records"; command.ExecuteNonQuery();
            }
            var record = JsonSerializer.Deserialize<AtlasRecoveryStore.Admission>(File.ReadLines(
                Directory.GetFiles(backupRoot, "admissions.jsonl", SearchOption.AllDirectories).Single()).First())!;
            using (var stream = File.OpenRead(record.Snapshot)) Assert.Equal(record.Sha256, Convert.ToHexString(SHA256.HashData(stream)));
            var restoredPath = Path.Combine(root, "restored.db"); File.Copy(record.Snapshot, restoredPath);
            using (var restored = new SqliteConnection($"Data Source={restoredPath};Pooling=False"))
            {
                restored.Open(); using var query = restored.CreateCommand(); query.CommandText = "SELECT Value FROM Records WHERE Id=1";
                Assert.Equal("preserved render reference", query.ExecuteScalar());
            }
            // Corrupt/missing source cannot silently permit an edit without protection.
            var broken = Config(new() { ["Recovery:DatabasePath"] = Path.Combine(root, "missing.db"), ["Recovery:Root"] = backupRoot });
            await Assert.ThrowsAsync<SqliteException>(() => new AtlasRecoveryStore(broken).BeforeWriteAsync(1, true, "owner edit", TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Quotas_include_rolling_day_and_shared_account_budget()
    {
        var now = DateTimeOffset.UtcNow;
        AtlasRecoveryStore.Admission Row(int id, double hours) => new(now.AddHours(-hours), id, false, "edit", "snapshot", "hash");
        Assert.False(AtlasRecoveryStore.WithinLimits(Enumerable.Range(0, 200).Select(_ => Row(2, 2)), 2, now));
        Assert.False(AtlasRecoveryStore.WithinLimits(Enumerable.Range(0, 120).Select(i => Row(i % 4 + 2, 0.5)), 9, now));
        Assert.True(AtlasRecoveryStore.WithinLimits(Enumerable.Range(0, 200).Select(_ => Row(2, 25)), 2, now));
        Assert.False(AtlasWriteProtection.RequiresOwner("PUT", "Locations", "Put"));
    }

    private static IConfiguration Config(Dictionary<string, string?>? values = null)
    {
        values ??= new(); values["JwtSettings:SecretKey"] = Key;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
    private static _2b2tAtlas.Server.Models.User Account(int id, string name, string role, int super = 0) => new()
    { Id = id, Username = name, Role = role, IsSuperAdmin = super, IsActive = 1, PasswordHash = "fixture-digest", CreatedAt = DateTime.UtcNow.ToString("o") };
    private static ClaimsPrincipal Principal(_2b2tAtlas.Server.Models.User user) => new(new ClaimsIdentity([
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim("atlas_session", AtlasSessionValidator.Stamp(user, Key)),
        new Claim("superadmin", "true"), new Claim("atlas_owner", "true"), new Claim("perm", Permissions.LocationsDelete)], "Bearer"));
}
