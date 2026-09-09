using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using _2b2tAtlas.Server.Models;
using _2b2tAtlas.Server.Services;

namespace Atlas.Ingestor.Tests;

public sealed class ArchiveGroupAttributionTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task Enrichment_handles_disabled_AI_and_existing_revisions_but_respects_review_only_mode(bool enabled, bool autoApply)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        await using var db = new AtlasContext(new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync(ct);
        db.Groups.Add(new _2b2tAtlas.Server.Models.Group { Name = "Spawn Builders Association", Type = "Build" });
        var row = new _2b2tAtlas.Server.Models.Location { LocationUuid = Guid.NewGuid().ToString(), DateAddedUtc = DateTime.UtcNow.ToString("o"), Name = "SBA 99", Description = "Keep this" };
        db.Locations.Add(row);
        await db.SaveChangesAsync(ct);
        db.Revisions.Add(new Revision { EntityType = "Location", EntityId = row.Rowid, Source = "AI", Status = "Applied", ProposedJson = "{}" });
        await db.SaveChangesAsync(ct);
        var options = Microsoft.Extensions.Options.Options.Create(new _2b2tAtlas.Server.Services.AiEnrichment.AiEnrichmentOptions
        {
            Enabled = enabled, GameModeLockPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
        });
        // Any attempt to call the wiki/model/index fails this test: these paths must not need them.
        var ai = new _2b2tAtlas.Server.Services.AiEnrichment.AiEnrichmentService(null!, null!, options,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<_2b2tAtlas.Server.Services.AiEnrichment.AiEnrichmentService>.Instance);
        var pipeline = new _2b2tAtlas.Server.Services.AiEnrichment.EnrichmentPipeline(db, ai, null!, new AuditService(db));
        var outcome = await pipeline.EnrichLocationAsync(row.Rowid, autoApply, null, "test", ct);
        Assert.Equal(autoApply ? 1 : 0, await db.LocationGroups.CountAsync(ct));
        Assert.Equal(autoApply ? _2b2tAtlas.Server.Services.AiEnrichment.EnrichmentOutcome.AutoApplied :
            _2b2tAtlas.Server.Services.AiEnrichment.EnrichmentOutcome.Skipped, outcome);
        Assert.Equal("Keep this", row.Description);
        Assert.Equal(1, await db.Revisions.CountAsync(ct));
    }

    [Theory]
    [InlineData("Space Base", "SBA_44:_Space_Base_2022-12-04@End", "Spawn Builders Association")]
    [InlineData("Yellow Brick PATH", "SBA_48:_Yellow_Brick_PATH_2023-02-09", "Spawn Builders Association")]
    [InlineData("Halloween", "SBA_77:_Halloween_2025-10-30", "Spawn Builders Association")]
    [InlineData("Unknown", "Name@SBA@End", "Spawn Builders Association")]
    [InlineData("Unknown", "Name@spawn_builders_association", "Spawn Builders Association")]
    [InlineData("kriZz SBA initial base", "kriZz_SBA_initial_base_2025-10-27", "Spawn Builders Association")]
    [InlineData("SBA 70.5", "", "Spawn Builders Association")]
    [InlineData("Underground", "l18w08_Underground_2018-02-24@Spawnmason_lodge", "SpawnMasons")]
    [InlineData("End Island", "l22w24_End_island_2022-06-12@Spawnmason_lodge@End", "SpawnMasons")]
    [InlineData("Test", "Mothra_Cube@1.16_test_server@Spawnmason_lodge", "SpawnMasons")]
    [InlineData("Hotel Ukraina", "Sky:_Hotel_Ukraina_2022-10-03@Spawnmasons", "SpawnMasons")]
    [InlineData("Unknown", "Name@SpawnMason-Lodge ", "SpawnMasons")]
    [InlineData("Spawnmason Guest Base", "", "SpawnMasons")]
    [InlineData("Unknown", "SpawnMasons:_Guest_Base_2020-01-01", "SpawnMasons")]
    [InlineData("Unknown", "Name@Emperium@End", "The Emperium")]
    [InlineData("Unknown", "Name@Imperator's_Base", "Imperator's Group")]
    [InlineData("DonFuer 99", "", "DonFuer")]
    [InlineData("Unknown", "Nerds_Inc:_Base_2026-01-01", "Nerds Inc")]
    [InlineData("Unknown", "Base@Nerds_Inc.@End", "Nerds Inc")]
    public void Recognizes_reviewed_families_and_complete_owner_tags(string name, string warp, string group)
    {
        Assert.Equal(group, Assert.Single(ArchiveGroupAttributionService.FindMatches(name, [warp])).GroupName);
    }

    [Theory]
    [InlineData("Lensbase", "Lensbase_2024-07-08")]
    [InlineData("SBAFan monument", "Name@NotSBA")]
    [InlineData("Unknown", "Name@SBA_Fan")]
    [InlineData("Unknown", "Name@NotSpawnmasons")]
    [InlineData("Unknown", "Name@Spawnmason_lodge_fan")]
    [InlineData("The Archive", "The_Archive_2016")]
    [InlineData("Tyranny", "Tyranny_2022-12-11")]
    [InlineData("Nerdy Castle", "Base@Nerds")]
    [InlineData("DonFuerian memorial", "")]
    [InlineData("Lodge", "l22w24_unknown_2022-06-12")]
    [InlineData("Generic", "archive.spawnmason.com")]
    public void Rejects_substrings_generic_aliases_and_hostnames(string name, string warp)
    {
        Assert.Empty(ArchiveGroupAttributionService.FindMatches(name, [warp]));
    }

    [Fact]
    public async Task Backfill_is_additive_audited_and_idempotent_in_the_callers_transaction()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var db = new AtlasContext(new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var group = new _2b2tAtlas.Server.Models.Group { Name = "SpawnMasons", Type = "Build" };
        var other = new _2b2tAtlas.Server.Models.Group { Name = "Vortex Coalition", Type = "Build" };
        var location = new _2b2tAtlas.Server.Models.Location { LocationUuid = Guid.NewGuid().ToString(), DateAddedUtc = DateTime.UtcNow.ToString("o"), Name = "VoCo on top", Description = "Reviewed description", X = 123, Y = 64, Z = -456 };
        db.AddRange(group, other, location);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.LocationGroups.Add(new LocationGroup { LocationRowid = location.Rowid, GroupId = other.Id, Role = "Contributor" });
        db.Warps.Add(new Warp { TimeAdded = DateTime.UtcNow.ToString("o"), Name = "l23w01_VoCo_on_top_2023-01-07@Spawnmason_lodge", LocationRowid = location.Rowid });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = new ArchiveGroupAttributionService(db);
        Assert.Equal(1, await service.StageAsync(location.Rowid, TestContext.Current.CancellationToken));
        Assert.Equal(0, await service.StageAsync(location.Rowid, TestContext.Current.CancellationToken));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        Assert.Equal(0, await service.StageAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, await db.LocationGroups.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Contributor", (await db.LocationGroups.SingleAsync(link => link.GroupId == other.Id, TestContext.Current.CancellationToken)).Role);
        var saved = await db.Locations.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal((123, 64, -456, "Reviewed description"), (saved.X, saved.Y, saved.Z, saved.Description));
        var audit = await db.AuditLogs.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Contains("@spawnmason lodge", audit.DetailsJson);
        Assert.Equal("location.group.archive", audit.Action);
    }
}
