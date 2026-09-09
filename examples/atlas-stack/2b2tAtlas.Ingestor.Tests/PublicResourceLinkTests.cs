using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using _2b2tAtlas.Server.Controllers;
using _2b2tAtlas.Server.Models;
using _2b2tAtlas.Server.Services;
using ServerAttachment = _2b2tAtlas.Server.Models.Attachment;
using ServerGroup = _2b2tAtlas.Server.Models.Group;
using ServerLocation = _2b2tAtlas.Server.Models.Location;
using ServerRender = _2b2tAtlas.Server.Models.Render;
using ServerWarp = _2b2tAtlas.Server.Models.Warp;

namespace Atlas.Ingestor.Tests;

public sealed class PublicResourceLinkTests
{
    [Fact]
    public async Task Public_records_expose_reciprocal_canonical_and_api_links()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var now = DateTime.UtcNow.ToString("o");
        var location = new ServerLocation
        {
            LocationUuid = Guid.NewGuid().ToString(), Name = "Linked Build", X = 12, Y = 64, Z = -34,
            Dimension = 0, DateAddedUtc = now, ModifiedUtc = now,
        };
        var group = new ServerGroup
        {
            Name = "The Society Project", Type = "Build", DateAddedUtc = now, ModifiedUtc = now,
        };
        context.AddRange(location, group);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.LocationGroups.Add(new LocationGroup
        {
            LocationRowid = location.Rowid, GroupId = group.Id, Role = "Primary builder", DateAddedUtc = now,
        });
        var warp = new ServerWarp
        {
            WarpUuid = Guid.NewGuid().ToString(), LocationUuidFk = location.LocationUuid,
            LocationRowid = location.Rowid, Name = "Linked_Build_2020-01-01", TimeAdded = now,
            ArchiveSha256 = new string('a', 64), Source = "The Archive automated sync",
        };
        var attachment = new ServerAttachment
        {
            LocationRowid = location.Rowid, FileName = "History", FilePath = "https://example.test/history",
            DateAddedUtc = now,
        };
        context.AddRange(warp, attachment);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var render = new ServerRender
        {
            LocationRowid = location.Rowid, ArchiveWarpId = warp.Id, Name = "Linked render",
            Dimension = 0, Scale = "base", TilesPath = "https://tiles.atlas.example/AtlasTiles/linked/{z}/{y}/{x}.png",
            IsPublic = 1, DateAddedUtc = now,
        };
        context.Renders.Add(render);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var locations = new LocationsController(context, new AuditService(context))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        var locationAction = await locations.GetLocation(location.Rowid);
        var locationDto = Assert.IsType<Atlas.Location>(Assert.IsType<OkObjectResult>(locationAction.Result).Value);
        Assert.Equal($"https://atlas.example/entities/locations/{location.Rowid}/", locationDto.CanonicalUrl);
        Assert.Equal($"http://127.0.0.1:5297/api/locations/{location.Rowid}", locationDto.ApiUrl);
        Assert.Equal($"http://127.0.0.1:5297/api/warps/{warp.Id}", Assert.Single(locationDto.Warps).ApiUrl);
        Assert.Equal($"http://127.0.0.1:5297/api/renders/{render.Id}", Assert.Single(locationDto.Renders).ApiUrl);
        Assert.Equal($"http://127.0.0.1:5297/api/attachments/{attachment.Id}", Assert.Single(locationDto.Attachments).ApiUrl);
        var attribution = Assert.Single(locationDto.Groups!);
        Assert.Equal($"https://atlas.example/entities/groups/{group.Id}/", attribution.GroupUrl);
        Assert.Equal($"http://127.0.0.1:5297/api/groups/{group.Id}", attribution.GroupApiUrl);

        var groups = new GroupsController(context, new AuditService(context));
        var groupAction = await groups.GetGroup(group.Id);
        var groupDto = Assert.IsType<Atlas.Group>(Assert.IsType<OkObjectResult>(groupAction.Result).Value);
        Assert.Equal($"https://atlas.example/entities/groups/{group.Id}/", groupDto.CanonicalUrl);
        Assert.Equal(["The Society"], groupDto.Aliases);
        Assert.Equal(locationDto.CanonicalUrl, Assert.Single(groupDto.Locations).LocationUrl);

        var warps = new WarpsController(context);
        var warpAction = await warps.GetWarp(warp.Id, TestContext.Current.CancellationToken);
        var warpDto = Assert.IsType<Atlas.Locations.WarpRecord>(Assert.IsType<OkObjectResult>(warpAction.Result).Value);
        Assert.Equal(locationDto.CanonicalUrl, warpDto.LocationUrl);
        Assert.Equal(locationDto.ApiUrl, warpDto.LocationApiUrl);
        Assert.Equal(
            $"http://127.0.0.1:5297/api/warps/{warp.Id}/world-download.zip?filename=2b2tAtlas-Linked-Build-warp-{warp.Id}.zip",
            warpDto.WorldDownloadUrl);
        Assert.Equal($"http://127.0.0.1:5297/api/warps/{warp.Id}/world-download", warpDto.WorldDownloadMetadataUrl);
        Assert.Equal("bounded-footprint", warpDto.WorldDownloadScope);

        var renders = new RendersController(context);
        var renderAction = await renders.GetRender(render.Id, TestContext.Current.CancellationToken);
        var renderDto = Assert.IsType<Atlas.LocationRenderDto>(Assert.IsType<OkObjectResult>(renderAction.Result).Value);
        Assert.Equal($"http://127.0.0.1:5297/api/renders/{render.Id}", renderDto.ApiUrl);
        Assert.Equal(warpDto.ApiUrl, renderDto.ArchiveWarpApiUrl);

        var attachments = new AttachmentsController(context);
        var attachmentAction = await attachments.GetAttachment(attachment.Id);
        var attachmentDto = Assert.IsType<Atlas.Locations.AttachmentRecord>(Assert.IsType<OkObjectResult>(attachmentAction.Result).Value);
        Assert.Equal($"http://127.0.0.1:5297/api/attachments/{attachment.Id}", attachmentDto.ApiUrl);
        Assert.Equal(locationDto.CanonicalUrl, attachmentDto.LocationUrl);
    }
}
