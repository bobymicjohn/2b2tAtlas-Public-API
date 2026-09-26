using Atlas.Locations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using _2b2tAtlas.Server.Controllers;
using _2b2tAtlas.Server.Models;

namespace Atlas.Ingestor.Tests;

public sealed class WarpPaginationTests
{
    [Fact]
    public async Task Unassigned_warps_do_not_shorten_a_page_and_hide_later_records()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var database = new AtlasContext(options);
        await database.Database.EnsureCreatedAsync(cancellationToken);
        database.Locations.Add(new()
        {
            Rowid = 1,
            Name = "Site",
            LocationUuid = "site",
            DateAddedUtc = "2020-01-01"
        });
        for (var id = 1; id <= 7; id++)
        {
            database.Warps.Add(new()
            {
                Id = id,
                Name = "Warp " + id,
                WarpUuid = id.ToString(),
                LocationRowid = id == 2 ? null : 1,
                TimeAdded = "2020-01-01",
            });
        }
        await database.SaveChangesAsync(cancellationToken);

        var controller = new WarpsController(database);
        var seen = new List<int>();
        for (var offset = 0; offset < 6; offset += 3)
        {
            var response = await controller.GetWarps(null, 3, offset, cancellationToken);
            var rows = Assert.IsType<List<WarpRecord>>(Assert.IsType<OkObjectResult>(response.Result).Value);
            Assert.Equal(3, rows.Count);
            seen.AddRange(rows.Select(warp => warp.Id));
        }
        Assert.Equal(new[] { 1, 3, 4, 5, 6, 7 }, seen);
    }
}
