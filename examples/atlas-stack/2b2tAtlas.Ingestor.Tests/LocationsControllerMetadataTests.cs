using Atlas;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using _2b2tAtlas.Server.Controllers;
using _2b2tAtlas.Server.Models;
using _2b2tAtlas.Server.Services;

namespace Atlas.Ingestor.Tests;

public sealed class LocationsControllerMetadataTests
{
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/secret.txt")]
    [InlineData("ftp://example.test/file")]
    public void Research_links_reject_non_http_schemes(string value)
    {
        var location = new Location { Name = "Unsafe", Wiki = value, Dimension = 0, X = 0, Z = 0 };
        var results = new List<ValidationResult>();

        Assert.False(Validator.TryValidateObject(
            location, new ValidationContext(location), results, validateAllProperties: true));
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(Location.Wiki)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Optional_research_links_allow_empty_values(string? value)
    {
        var location = new Location
        {
            Name = "No Links",
            Wiki = value,
            VideoUrl = value,
            Dimension = 0,
            X = 0,
            Z = 0,
        };
        var results = new List<ValidationResult>();

        Assert.True(Validator.TryValidateObject(
            location, new ValidationContext(location), results, validateAllProperties: true));
        Assert.Empty(results);
    }

    [Fact]
    public async Task Create_and_get_round_trip_research_metadata()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var controller = new LocationsController(context, new AuditService(context))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        var input = new Location
        {
            Name = "Research Base",
            Description = "Source-derived summary.",
            Tags = "base,griefed",
            Wiki = "https://2b2t.miraheze.org/wiki/Research_Base",
            VideoUrl = "https://example.test/video",
            Dimension = 0,
            X = 123,
            Z = -456,
        };

        var createdAction = await controller.CreateLocation(input);
        var created = Assert.IsType<CreatedAtActionResult>(createdAction.Result);
        var createdLocation = Assert.IsType<Location>(created.Value);
        Assert.True(createdLocation.Rowid > 0);

        var getAction = await controller.GetLocation(createdLocation.Rowid);
        var ok = Assert.IsType<OkObjectResult>(getAction.Result);
        var result = Assert.IsType<Location>(ok.Value);
        Assert.Equal(input.Description, result.Description);
        Assert.Equal(input.Tags, result.Tags);
        Assert.Equal(input.Wiki, result.Wiki);
        Assert.Equal(input.VideoUrl, result.VideoUrl);
    }

    [Fact]
    public async Task Public_location_reads_hide_retained_equivalent_renders()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var location = new _2b2tAtlas.Server.Models.Location
        {
            LocationUuid = Guid.NewGuid().ToString(), Name = "Shared Base", Dimension = 0,
            X = 1, Y = 64, Z = 2, DateAddedUtc = DateTime.UtcNow.ToString("o"),
        };
        context.Locations.Add(location);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.Renders.AddRange(
            new _2b2tAtlas.Server.Models.Render
            {
                LocationRowid = location.Rowid, Name = "Canonical", Dimension = 0, Scale = "1",
                TilesPath = "https://example.test/canonical/{z}/{y}/{x}.png", IsPublic = 1,
                DateAddedUtc = DateTime.UtcNow.ToString("o"),
            },
            new _2b2tAtlas.Server.Models.Render
            {
                LocationRowid = location.Rowid, Name = "Equivalent", Dimension = 0, Scale = "1",
                TilesPath = "https://example.test/equivalent/{z}/{y}/{x}.png", IsPublic = 0,
                EquivalentToRenderId = 1, DateAddedUtc = DateTime.UtcNow.ToString("o"),
            });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var controller = new LocationsController(context, new AuditService(context))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var all = Assert.IsType<OkObjectResult>((await controller.GetLocations()).Result);
        var fromList = Assert.Single(Assert.IsAssignableFrom<IEnumerable<Location>>(all.Value));
        Assert.Equal("Canonical", Assert.Single(fromList.Renders).Name);

        var one = Assert.IsType<OkObjectResult>((await controller.GetLocation(location.Rowid)).Result);
        var fromDetail = Assert.IsType<Location>(one.Value);
        Assert.Equal("Canonical", Assert.Single(fromDetail.Renders).Name);
        Assert.Equal(2, await context.Renders.CountAsync(TestContext.Current.CancellationToken));
    }
}
