using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using _2b2tAtlas.Server.Controllers;
using _2b2tAtlas.Server.Models;
using _2b2tAtlas.Server.Services;
using ServerAttachment = _2b2tAtlas.Server.Models.Attachment;
using ServerLocation = _2b2tAtlas.Server.Models.Location;
using AttachmentDto = Atlas.Locations.Attachment;

namespace Atlas.Ingestor.Tests;

public sealed class AttachmentEndpointTests
{
    [Fact]
    public async Task Unsafe_replacement_does_not_remove_existing_attachments()
    {
        await using var fixture = await AttachmentFixture.CreateAsync();
        var result = await fixture.Controller.UpdateLocationAttachments(fixture.LocationId,
            new List<AttachmentDto>
            {
                new() { FileName = "Unsafe", Path = "javascript:alert(1)" },
            });

        Assert.IsType<BadRequestObjectResult>(result);
        var stored = await fixture.Context.Attachments.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("https://example.test/original", stored.FilePath);
    }

    [Fact]
    public async Task Https_replacement_is_normalized_and_audited()
    {
        await using var fixture = await AttachmentFixture.CreateAsync();
        var result = await fixture.Controller.UpdateLocationAttachments(fixture.LocationId,
            new List<AttachmentDto>
            {
                new()
                {
                    FileName = "  Source Link  ", Path = " https://example.test/source?q=1 ",
                    MediaType = "Image", ThumbnailPath = " https://example.test/thumb.webp ",
                    SourceUrl = " https://example.test/history ", Caption = " A sourced view ",
                    Attribution = " Example photographer ",
                },
            });

        Assert.IsType<OkObjectResult>(result);
        var stored = await fixture.Context.Attachments.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Source Link", stored.FileName);
        Assert.Equal("https://example.test/source?q=1", stored.FilePath);
        Assert.Equal("Image", stored.MediaType);
        Assert.Equal("https://example.test/thumb.webp", stored.ThumbnailPath);
        Assert.Equal("https://example.test/history", stored.SourceUrl);
        Assert.Equal("A sourced view", stored.Caption);
        Assert.Equal("Example photographer", stored.Attribution);
        Assert.Contains(await fixture.Context.AuditLogs.ToListAsync(TestContext.Current.CancellationToken),
            entry => entry.Action == "location.attachments");
    }

    [Theory]
    [InlineData("timeline", "Timeline")]
    [InlineData("article", "Article")]
    public async Task Historical_reference_kind_survives_edit_and_location_projection(string input, string expected)
    {
        await using var fixture = await AttachmentFixture.CreateAsync();
        var result = await fixture.Controller.UpdateLocationAttachments(fixture.LocationId,
            new List<AttachmentDto>
            {
                new() { FileName = "Historical reference", Path = "https://example.test/history", MediaType = input },
            });
        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(expected, (await fixture.Context.Attachments.SingleAsync(TestContext.Current.CancellationToken)).MediaType);
        var detail = Assert.IsType<OkObjectResult>((await fixture.Controller.GetLocation(fixture.LocationId)).Result);
        var location = Assert.IsType<Atlas.Location>(detail.Value);
        Assert.Equal(expected, Assert.Single(location.Attachments).MediaType);
        var list = Assert.IsType<OkObjectResult>((await fixture.Controller.GetLocations()).Result);
        var listed = Assert.Single(Assert.IsAssignableFrom<IEnumerable<Atlas.Location>>(list.Value));
        Assert.Equal(expected, Assert.Single(listed.Attachments).MediaType);
    }

    private sealed class AttachmentFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public AtlasContext Context { get; }
        public LocationsController Controller { get; }
        public int LocationId { get; }

        private AttachmentFixture(SqliteConnection connection, AtlasContext context,
            LocationsController controller, int locationId)
        {
            _connection = connection;
            Context = context;
            Controller = controller;
            LocationId = locationId;
        }

        public static async Task<AttachmentFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var options = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
            var context = new AtlasContext(options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            var location = new ServerLocation
            {
                LocationUuid = Guid.NewGuid().ToString(),
                Name = "Attachment Test",
                X = 0,
                Y = 64,
                Z = 0,
                Dimension = 0,
                DateAddedUtc = DateTime.UtcNow.ToString("o"),
            };
            context.Locations.Add(location);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            context.Attachments.Add(new ServerAttachment
            {
                LocationRowid = location.Rowid,
                FileName = "Original",
                FilePath = "https://example.test/original",
                DateAddedUtc = DateTime.UtcNow.ToString("o"),
            });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            var controller = new LocationsController(context, new AuditService(context))
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            };
            return new AttachmentFixture(connection, context, controller, location.Rowid);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
