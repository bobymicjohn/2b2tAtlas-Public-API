using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using _2b2tAtlas.Server.Controllers;
using _2b2tAtlas.Server.Models;
using _2b2tAtlas.Server.Services;
using ServerGroup = _2b2tAtlas.Server.Models.Group;
using ServerLocation = _2b2tAtlas.Server.Models.Location;
using ServerRender = _2b2tAtlas.Server.Models.Render;
using ServerWarp = _2b2tAtlas.Server.Models.Warp;

namespace Atlas.Ingestor.Tests;

public sealed class LocationGroupEndpointTests
{
    [Fact]
    public async Task Canonical_group_seed_backfills_once_then_preserves_admin_edits()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var legacy = new ServerGroup
        {
            Name = "DonFuer",
            Type = "Other",
            Description = "Legacy summary",
            DateAddedUtc = DateTime.UtcNow.ToString("o"),
        };
        context.Groups.Add(legacy);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var seeder = new GroupSeeder(context, NullLogger<GroupSeeder>.Instance);
        await seeder.SeedAsync();
        Assert.Equal("Build", legacy.Type);
        Assert.Equal("Active", legacy.Status);
        Assert.False(string.IsNullOrWhiteSpace(legacy.LogoUrl));

        legacy.Description = "Reviewed administrator correction";
        legacy.LogoUrl = "https://example.com/reviewed-logo.png";
        legacy.Status = "Historical";
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await seeder.SeedAsync();
        Assert.Equal("Reviewed administrator correction", legacy.Description);
        Assert.Equal("https://example.com/reviewed-logo.png", legacy.LogoUrl);
        Assert.Equal("Historical", legacy.Status);
    }

    [Fact]
    public async Task Updating_group_attribution_preserves_archive_warp_and_render_identity()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var now = DateTime.UtcNow.ToString("o");
        var location = new ServerLocation
        {
            LocationUuid = Guid.NewGuid().ToString(),
            Name = "Provenance Test",
            X = 120,
            Y = 64,
            Z = -240,
            Dimension = 0,
            DateAddedUtc = now,
            ModifiedUtc = now,
        };
        var group = new ServerGroup { Name = "Builder Test", Type = "Build", DateAddedUtc = now };
        context.AddRange(location, group);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var warp = new ServerWarp
        {
            WarpUuid = Guid.NewGuid().ToString(),
            LocationUuidFk = location.LocationUuid,
            LocationRowid = location.Rowid,
            Name = "Archive_Warp_2020-01-01",
            TimeAdded = now,
            ArchiveSha256 = new string('a', 64),
            WorldDownloadDate = "2020-01-01",
            Source = "archive-collector",
            ArchiveX = 120.5,
            ArchiveY = 65,
            ArchiveZ = -239.5,
        };
        context.Warps.Add(warp);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var render = new ServerRender
        {
            LocationRowid = location.Rowid,
            ArchiveWarpId = warp.Id,
            Name = "Archive capture",
            Dimension = 0,
            Scale = "base",
            TilesPath = "tests/provenance",
            Source = "archive-collector",
            IsPublic = 1,
            DateAddedUtc = now,
        };
        context.Renders.Add(render);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var controller = new LocationsController(context, new AuditService(context))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        var dto = new Atlas.Location
        {
            Rowid = location.Rowid,
            LocationUuid = location.LocationUuid,
            Name = location.Name,
            X = location.X,
            Y = location.Y,
            Z = location.Z,
            Dimension = location.Dimension,
            Warps =
            [
                new Atlas.Locations.Warp
                {
                    Id = warp.Id,
                    WarpUuid = warp.WarpUuid,
                    LocationUuidFk = warp.LocationUuidFk,
                    LocationRowid = location.Rowid,
                    Name = warp.Name,
                    TimeAdded = DateTime.Parse(warp.TimeAdded),
                    ArchiveSha256 = warp.ArchiveSha256,
                    WorldDownloadDate = warp.WorldDownloadDate,
                    Source = warp.Source,
                    ArchiveX = warp.ArchiveX,
                    ArchiveY = warp.ArchiveY,
                    ArchiveZ = warp.ArchiveZ,
                },
            ],
            Groups = [new Atlas.LocationGroupAttribution { GroupId = group.Id, GroupName = group.Name, Role = "Builder" }],
        };

        var action = await controller.UpdateLocation(location.Rowid, dto);
        Assert.IsType<OkObjectResult>(action.Result);
        context.ChangeTracker.Clear();

        var storedWarp = await context.Warps.SingleAsync(TestContext.Current.CancellationToken);
        var storedRender = await context.Renders.SingleAsync(TestContext.Current.CancellationToken);
        var storedLink = await context.LocationGroups.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(warp.Id, storedWarp.Id);
        Assert.Equal(new string('a', 64), storedWarp.ArchiveSha256);
        Assert.Equal(120.5, storedWarp.ArchiveX);
        Assert.Equal(warp.Id, storedRender.ArchiveWarpId);
        Assert.Equal(group.Id, storedLink.GroupId);
        Assert.Equal(location.Rowid, storedLink.LocationRowid);
    }
}
