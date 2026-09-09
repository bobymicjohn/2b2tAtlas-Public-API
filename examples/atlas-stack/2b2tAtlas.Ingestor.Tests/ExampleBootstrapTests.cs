using Atlas.Auth;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using _2b2tAtlas.Server.Models;
using _2b2tAtlas.Server.Services;

namespace Atlas.Ingestor.Tests;

public sealed class ExampleBootstrapTests
{
    [Fact]
    public async Task Bootstrap_requires_password_creates_only_local_owner_and_never_resets_accounts()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var db = new AtlasContext(new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var seeder = new DatabaseSeeder(db, config, NullLogger<DatabaseSeeder>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(seeder.SeedDefaultUsersAsync);
        Assert.Empty(db.Users);
        config["Bootstrap:OwnerPassword"] = "isolated-example-test-password";
        await seeder.SeedDefaultUsersAsync();
        var owner = Assert.Single(db.Users);
        Assert.Equal(1, owner.Id);
        Assert.Equal("atlas-owner", owner.Username);
        Assert.True(AtlasSessionValidator.IsOwner(owner));
        Assert.True(BCrypt.Net.BCrypt.Verify(config["Bootstrap:OwnerPassword"], owner.PasswordHash));
        var hash = owner.PasswordHash;
        owner.IsActive = 0;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        config["Bootstrap:OwnerPassword"] = "another-isolated-example-password";
        await seeder.SeedDefaultUsersAsync();
        Assert.Single(db.Users);
        Assert.Equal(hash, owner.PasswordHash);
        Assert.Equal(0, owner.IsActive);
    }
}
