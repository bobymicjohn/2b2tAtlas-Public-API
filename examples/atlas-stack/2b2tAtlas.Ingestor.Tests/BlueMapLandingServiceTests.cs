using _2b2tAtlas.Server.Models;
using _2b2tAtlas.Server.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Ingestor.Tests;

public sealed class BlueMapLandingServiceTests
{
    private static Render Capture() => new()
    {
        MinX = -200,
        MinZ = -300,
        MaxXExclusive = 200,
        MaxZExclusive = 300,
        Dimension = 0,
        LocationRow = new _2b2tAtlas.Server.Models.Location { X = 10, Y = 80, Z = 20, Dimension = 0 },
        ArchiveWarp = new Warp { ArchiveX = -100.125, ArchiveY = 0, ArchiveZ = 250.75 }
    };

    [Theory]
    [InlineData(0)]
    [InlineData(-48.25)]
    [InlineData(153.6366)]
    public void Exact_dated_warp_wins_over_in_bounds_catalog_and_retains_all_axes(double y)
    {
        var render = Capture();
        render.ArchiveWarp.ArchiveY = y;
        Assert.Equal(new BlueMapLanding(-100.125, y, 250.75, "archive-warp"), BlueMapLandingService.Resolve(render));
    }

    [Fact]
    public void Different_dimension_location_is_not_used_for_missing_or_outside_warp()
    {
        var render = Capture();
        render.Dimension = 1;
        render.ArchiveWarp.ArchiveX = 200;
        Assert.Equal(new BlueMapLanding(0, null, 0, "footprint-fallback"), BlueMapLandingService.Resolve(render));
        render.ArchiveWarp.ArchiveX = -100.125;
        Assert.Equal("archive-warp", BlueMapLandingService.Resolve(render)!.Source);
    }

    [Fact]
    public void Missing_height_uses_terrain_without_borrowing_catalog_height()
    {
        var render = Capture();
        render.ArchiveWarp.ArchiveY = null;
        Assert.Equal(new BlueMapLanding(-100.125, null, 250.75, "warp-terrain-height"), BlueMapLandingService.Resolve(render));
        render.ArchiveWarp.ArchiveY = double.NaN;
        Assert.Null(BlueMapLandingService.Resolve(render)!.Y);
    }

    [Fact]
    public void Unsafe_coordinates_and_unknown_bounds_cannot_become_warp_landings()
    {
        var render = Capture();
        render.ArchiveWarp.ArchiveX = double.PositiveInfinity;
        Assert.Equal("catalog-fallback", BlueMapLandingService.Resolve(render)!.Source);
        render.MinX = null;
        Assert.Null(BlueMapLandingService.Resolve(render));
        render.MinX = 500;
        Assert.Null(BlueMapLandingService.Resolve(render));
    }

    [Fact]
    public async Task Lookup_joins_only_the_render_warp_and_does_not_expose_hidden_records()
    {
        var token = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(token);
        await using var db = new AtlasContext(new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync(token);
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=OFF", token);
        db.Locations.Add(new _2b2tAtlas.Server.Models.Location
        {
            Rowid = 1,
            LocationUuid = "dated-base-fixture",
            DateAddedUtc = "2026-09-10",
            Name = "Dated base",
            X = 10,
            Y = 80,
            Z = 20,
            Dimension = 0
        });
        db.Warps.AddRange(
            new Warp
            {
                Id = 1,
                Name = "older date",
                TimeAdded = "2026-09-10",
                LocationRowid = 1,
                ArchiveX = -100.125,
                ArchiveY = -48.25,
                ArchiveZ = 250.75
            },
            new Warp
            {
                Id = 2,
                Name = "newer date",
                TimeAdded = "2026-09-10",
                LocationRowid = 1,
                ArchiveX = 100,
                ArchiveY = 100,
                ArchiveZ = 100
            });
        db.Renders.Add(new Render
        {
            Id = 1,
            LocationRowid = 1,
            ArchiveWarpId = 1,
            Name = "older date",
            Scale = "1",
            TilesPath = "https://example.invalid/tiles",
            DateAddedUtc = "2026-09-10",
            MinX = -200,
            MinZ = -300,
            MaxXExclusive = 200,
            MaxZExclusive = 300
        });
        await db.SaveChangesAsync(token);
        var service = new BlueMapLandingService(db);
        Assert.Equal(new BlueMapLanding(-100.125, -48.25, 250.75, "archive-warp"), await service.FindAsync(1, token));
        await db.Renders.Where(r => r.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(r => r.IsPublic, 0), token);
        Assert.Null(await service.FindAsync(1, token));
        Assert.Null(await service.FindAsync(999, token));
    }
}
