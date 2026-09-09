using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using _2b2tAtlas.Server.Controllers;
using _2b2tAtlas.Server.Models;
using ServerLocation = _2b2tAtlas.Server.Models.Location;
using ServerWarp = _2b2tAtlas.Server.Models.Warp;

namespace Atlas.Ingestor.Tests;

public sealed class LegacyApiCompatibilityTests
{
    [Fact]
    public async Task Legacy_location_query_preserves_filters_sort_shape_and_cors()
    {
        await using var fixture = await LegacyFixture.CreateAsync();
        var result = await fixture.Controller.GetLocations(0, 0, 1, 0, 1, "Near");
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var rows = Assert.IsAssignableFrom<IReadOnlyList<LegacyApiController.LegacyLocation>>(ok.Value);
        var location = Assert.Single(rows);

        Assert.Equal(fixture.NearUuid, location.LocationUuid);
        Assert.Equal("Near Warp Base", location.Name);
        Assert.Equal("0", location.X);
        Assert.Equal(0, location.EndDimension);
        Assert.Equal("archive_spawn", Assert.Single(location.Warps!).Name);
        Assert.Equal("legacy-read-v1", (string?)fixture.Controller.Response.Headers["X-Atlas-Compatibility"]);

        var json = JsonSerializer.Serialize(location);
        Assert.Contains("\"location_uuid\"", json, StringComparison.Ordinal);
        Assert.Contains("\"time_added\"", json, StringComparison.Ordinal);
        Assert.Contains("\"video_url\"", json, StringComparison.Ordinal);
        Assert.Contains("\"end_dimension\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Legacy_dimension_count_and_retired_write_contracts_are_preserved()
    {
        await using var fixture = await LegacyFixture.CreateAsync();
        var endResult = await fixture.Controller.GetLocations(null, null, null, 1, null, null);
        var endRows = Assert.IsAssignableFrom<IReadOnlyList<LegacyApiController.LegacyLocation>>(
            Assert.IsType<OkObjectResult>(endResult.Result).Value);
        Assert.Equal("End Archive", Assert.Single(endRows).Name);
        Assert.Equal(1, endRows[0].EndDimension);

        var count = await fixture.Controller.GetLocationCount();
        var countJson = JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(count.Result).Value);
        Assert.Contains("\"locationCount\":3", countJson, StringComparison.Ordinal);

        var retired = Assert.IsType<ObjectResult>(fixture.Controller.LegacyWarpWriteRetired());
        Assert.Equal(StatusCodes.Status410Gone, retired.StatusCode);
    }

    private sealed class LegacyFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public AtlasContext Context { get; }
        public LegacyApiController Controller { get; }
        public string NearUuid { get; }

        private LegacyFixture(SqliteConnection connection, AtlasContext context,
            LegacyApiController controller, string nearUuid)
        {
            _connection = connection;
            Context = context;
            Controller = controller;
            NearUuid = nearUuid;
        }

        public static async Task<LegacyFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var options = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
            var context = new AtlasContext(options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            var nearUuid = Guid.NewGuid().ToString();
            var near = NewLocation(nearUuid, "Near Warp Base", 0, 0, 0, "2020-01-01T00:00:00Z");
            var far = NewLocation(Guid.NewGuid().ToString(), "Far Base", 10000, 10000, 0, "2021-01-01T00:00:00Z");
            var end = NewLocation(Guid.NewGuid().ToString(), "End Archive", 500, 500, 2, "2022-01-01T00:00:00Z");
            context.Locations.AddRange(near, far, end);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            context.Warps.Add(new ServerWarp
            {
                WarpUuid = Guid.NewGuid().ToString(),
                LocationUuidFk = nearUuid,
                LocationRowid = near.Rowid,
                Name = "archive_spawn",
                TimeAdded = "2020-01-02T00:00:00Z",
            });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            var controller = new LegacyApiController(context)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            };
            return new LegacyFixture(connection, context, controller, nearUuid);
        }

        private static ServerLocation NewLocation(string uuid, string name, int x, int z, int dimension, string added) => new()
        {
            LocationUuid = uuid,
            Name = name,
            X = x,
            Y = 64,
            Z = z,
            Dimension = dimension,
            DateAddedUtc = added,
        };

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}