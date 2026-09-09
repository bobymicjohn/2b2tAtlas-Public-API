using System.Security.Cryptography;
using System.Text.Json;
using Atlas;
using Atlas.Locations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using _2b2tAtlas.Server.Controllers;
using _2b2tAtlas.Server.Models;
using _2b2tAtlas.Server.Services;
using ServerLocation = _2b2tAtlas.Server.Models.Location;
using ServerRender = _2b2tAtlas.Server.Models.Render;
using ServerWarp = _2b2tAtlas.Server.Models.Warp;

namespace Atlas.Ingestor.Tests;

public sealed class WorldDownloadEndpointTests
{
    [Fact]
    public async Task Collector_footprint_exposes_metadata_and_resumable_immutable_zip_without_private_path()
    {
        var archiveRoot = Path.Combine(Path.GetTempPath(), "atlas-wdl-endpoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(archiveRoot);
        try
        {
            var bytes = "bounded collector world fixture"u8.ToArray();
            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var objectPath = WdlArchiveStore.ObjectPath(archiveRoot, sha);
            Directory.CreateDirectory(Path.GetDirectoryName(objectPath)!);
            await File.WriteAllBytesAsync(objectPath, bytes, TestContext.Current.CancellationToken);

            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var dbOptions = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
            await using var context = new AtlasContext(dbOptions);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

            var now = DateTime.UtcNow.ToString("o");
            var location = new ServerLocation
            {
                LocationUuid = Guid.NewGuid().ToString(), Name = "Preserved Build", X = 128, Y = 64, Z = -256,
                Dimension = 0, DateAddedUtc = now, ModifiedUtc = now,
            };
            context.Locations.Add(location);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            var warp = new ServerWarp
            {
                WarpUuid = Guid.NewGuid().ToString(), LocationUuidFk = location.LocationUuid,
                LocationRowid = location.Rowid, Name = "Preserved_Build_2021-02-03", TimeAdded = now,
                ArchiveSha256 = sha, WorldDownloadDate = "2021-02-03",
                Source = "The Archive automated sync (live /warps catalog)",
            };
            context.Warps.Add(warp);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            context.Renders.Add(new ServerRender
            {
                LocationRowid = location.Rowid, ArchiveWarpId = warp.Id, Name = "Preserved Build",
                Dimension = 0, Scale = "base", TilesPath = "https://example.test/tiles/{z}/{y}/{x}.png",
                MinX = 96, MinZ = -288, MaxXExclusive = 176, MaxZExclusive = -208,
                IsPublic = 1, DateAddedUtc = now,
            });
            context.IngestionJobs.Add(new IngestionJob
            {
                PublicId = Guid.NewGuid().ToString("N"), IntakeFileName = "fixture.zip", Slug = "fixture",
                Name = "Preserved Build", WorldDownloadDate = "2021-02-03", Source = warp.Source,
                Scale = "base", Dimension = "overworld", Status = "completed", WarpId = warp.Id,
                ArchiveSha256 = sha, RequestedUtc = now,
                InspectionJson = JsonSerializer.Serialize(new IngestionWorldInspection
                {
                    Dimensions =
                    [
                        new IngestionDimensionInspection
                        {
                            Key = "overworld", StorageEra = "anvil", ChunkCount = 42,
                            MinX = 80, MinZ = -304, MaxXExclusive = 192, MaxZExclusive = -192,
                        },
                    ],
                }),
            });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            var httpContext = new DefaultHttpContext();
            var controller = new WorldDownloadsController(context, Options.Create(new WdlArchiveOptions { Root = archiveRoot }))
            {
                ControllerContext = new ControllerContext { HttpContext = httpContext },
            };

            var metadataAction = await controller.GetMetadata(warp.Id, TestContext.Current.CancellationToken);
            var metadata = Assert.IsType<WorldDownloadRecord>(Assert.IsType<OkObjectResult>(metadataAction.Result).Value);
            Assert.Equal(bytes.LongLength, metadata.ByteLength);
            Assert.Equal(sha, metadata.Sha256);
            Assert.Equal(42, metadata.ChunkCount);
            Assert.Equal("bounded-footprint", metadata.CaptureType);
            Assert.Equal("partial-java-save", metadata.Playability);
            Assert.False(metadata.IsCompleteWorld);
            Assert.Equal(96, metadata.Bounds!.MinX);
            Assert.Equal(
                $"http://127.0.0.1:5297/api/warps/{warp.Id}/world-download.zip?filename=2b2tAtlas-Preserved-Build-warp-{warp.Id}.zip",
                metadata.DownloadUrl);
            Assert.Equal($"2b2tAtlas-Preserved-Build-warp-{warp.Id}.zip", metadata.FileName);
            Assert.DoesNotContain(archiveRoot, JsonSerializer.Serialize(metadata), StringComparison.OrdinalIgnoreCase);

            var download = Assert.IsType<PhysicalFileResult>(
                await controller.Download(warp.Id, TestContext.Current.CancellationToken));
            Assert.Equal(objectPath, download.FileName);
            Assert.Equal($"2b2tAtlas-Preserved-Build-warp-{warp.Id}.zip", download.FileDownloadName);
            Assert.True(download.EnableRangeProcessing);
            Assert.Equal(new EntityTagHeaderValue($"\"{sha}\""), download.EntityTag);
            Assert.Equal("public,max-age=31536000,immutable", httpContext.Response.Headers.CacheControl);
            Assert.Equal("bounded-footprint", httpContext.Response.Headers["X-Atlas-World-Scope"]);
            Assert.DoesNotContain(archiveRoot, string.Join("\n", httpContext.Response.Headers), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(archiveRoot)) Directory.Delete(archiveRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Non_collector_or_missing_archive_objects_are_not_downloaded()
    {
        var archiveRoot = Path.Combine(Path.GetTempPath(), "atlas-wdl-endpoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(archiveRoot);
        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var dbOptions = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
            await using var context = new AtlasContext(dbOptions);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

            var now = DateTime.UtcNow.ToString("o");
            var location = new ServerLocation
            {
                LocationUuid = Guid.NewGuid().ToString(), Name = "Manual WDL", Dimension = 0,
                DateAddedUtc = now, ModifiedUtc = now,
            };
            context.Locations.Add(location);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            var manual = new ServerWarp
            {
                WarpUuid = Guid.NewGuid().ToString(), LocationUuidFk = location.LocationUuid,
                LocationRowid = location.Rowid, Name = "Manual", TimeAdded = now,
                ArchiveSha256 = new string('a', 64), Source = "Community upload",
            };
            var missing = new ServerWarp
            {
                WarpUuid = Guid.NewGuid().ToString(), LocationUuidFk = location.LocationUuid,
                LocationRowid = location.Rowid, Name = "Missing", TimeAdded = now,
                ArchiveSha256 = new string('b', 64), Source = "The Archive automated sync",
            };
            context.Warps.AddRange(manual, missing);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            var controller = new WorldDownloadsController(context, Options.Create(new WdlArchiveOptions { Root = archiveRoot }))
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            };
            var hidden = Assert.IsType<ObjectResult>(
                (await controller.Download(manual.Id, TestContext.Current.CancellationToken)));
            Assert.Equal(StatusCodes.Status404NotFound, hidden.StatusCode);
            var unavailable = Assert.IsType<ObjectResult>(
                (await controller.Download(missing.Id, TestContext.Current.CancellationToken)));
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, unavailable.StatusCode);
        }
        finally
        {
            if (Directory.Exists(archiveRoot)) Directory.Delete(archiveRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Completed_public_legacy_render_exposes_its_preserved_source_without_a_fake_warp()
    {
        var archiveRoot = Path.Combine(Path.GetTempPath(), "atlas-legacy-wdl-endpoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(archiveRoot);
        try
        {
            var bytes = "preserved legacy render source"u8.ToArray();
            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var objectPath = WdlArchiveStore.ObjectPath(archiveRoot, sha);
            Directory.CreateDirectory(Path.GetDirectoryName(objectPath)!);
            await File.WriteAllBytesAsync(objectPath, bytes, TestContext.Current.CancellationToken);

            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var dbOptions = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
            await using var context = new AtlasContext(dbOptions);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

            var now = DateTime.UtcNow.ToString("o");
            var location = new ServerLocation
            {
                LocationUuid = Guid.NewGuid().ToString(), Name = "Mu Megabase", X = 35931, Y = 64, Z = 102450,
                Dimension = 0, DateAddedUtc = now, ModifiedUtc = now,
            };
            context.Locations.Add(location);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            var render = new ServerRender
            {
                LocationRowid = location.Rowid, Name = "Mu Megabase", Source = "wdl-ingestion",
                Dimension = 0, Scale = "base", TilesPath = "https://example.test/mu/{z}/{y}/{x}.png",
                MinX = 35000, MinZ = 101000, MaxXExclusive = 37000, MaxZExclusive = 104000,
                WorldDownloadDate = "2020-07-23", IsPublic = 1, DateAddedUtc = now,
            };
            context.Renders.Add(render);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            context.IngestionJobs.Add(new IngestionJob
            {
                PublicId = Guid.NewGuid().ToString("N"), IntakeFileName = "mu.zip", Slug = "mu-megabase",
                Name = "Mu Megabase", WorldDownloadDate = "2020-07-23",
                Source = "jumboman32/2b2t-wdl; bases/Mu Megabase; occupied tile verified",
                Scale = "base", Dimension = "overworld", Status = "completed", RenderId = render.Id,
                ArchiveSha256 = sha, RequestedUtc = now,
                InspectionJson = JsonSerializer.Serialize(new IngestionWorldInspection
                {
                    Dimensions =
                    [
                        new IngestionDimensionInspection
                        {
                            Key = "overworld", StorageEra = "anvil", ChunkCount = 1234,
                            MinX = 34992, MinZ = 100992, MaxXExclusive = 37008, MaxZExclusive = 104016,
                        },
                    ],
                }),
            });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            var httpContext = new DefaultHttpContext();
            var controller = new WorldDownloadsController(context, Options.Create(new WdlArchiveOptions { Root = archiveRoot }))
            {
                ControllerContext = new ControllerContext { HttpContext = httpContext },
            };

            var metadataAction = await controller.GetRenderMetadata(render.Id, TestContext.Current.CancellationToken);
            var metadata = Assert.IsType<WorldDownloadRecord>(Assert.IsType<OkObjectResult>(metadataAction.Result).Value);
            Assert.Null(metadata.WarpId);
            Assert.Equal(render.Id, metadata.RenderId);
            Assert.Equal("Mu Megabase", metadata.RenderName);
            Assert.Equal("preserved-render-source", metadata.CaptureType);
            Assert.Equal(1234, metadata.ChunkCount);
            Assert.Equal(sha, metadata.Sha256);
            Assert.Equal($"2b2tAtlas-Mu-Megabase-render-{render.Id}.zip", metadata.FileName);
            Assert.Contains($"/api/renders/{render.Id}/world-download.zip", metadata.DownloadUrl);
            Assert.Contains("jumboman32", metadata.Source);
            Assert.DoesNotContain(archiveRoot, JsonSerializer.Serialize(metadata), StringComparison.OrdinalIgnoreCase);

            var download = Assert.IsType<PhysicalFileResult>(
                await controller.DownloadRenderSource(render.Id, TestContext.Current.CancellationToken));
            Assert.Equal(objectPath, download.FileName);
            Assert.Equal($"2b2tAtlas-Mu-Megabase-render-{render.Id}.zip", download.FileDownloadName);
            Assert.True(download.EnableRangeProcessing);
            Assert.Equal(new EntityTagHeaderValue($"\"{sha}\""), download.EntityTag);
            Assert.Equal("preserved-render-source", httpContext.Response.Headers["X-Atlas-World-Scope"]);
        }
        finally
        {
            if (Directory.Exists(archiveRoot)) Directory.Delete(archiveRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("Unsafe / Build: One?", 42, "2b2tAtlas-Unsafe-Build-One-warp-42.zip")]
    [InlineData("***", 7, "2b2tAtlas-location-warp-7.zip")]
    [InlineData("La Rosa", 12, "2b2tAtlas-La-Rosa-warp-12.zip")]
    public void World_download_names_are_descriptive_and_filesystem_safe(
        string locationName, int warpId, string expected)
    {
        Assert.Equal(expected, DownloadFileNames.WorldDownload(locationName, warpId));
    }

    [Theory]
    [InlineData("Mu Megabase", 38, "2b2tAtlas-Mu-Megabase-render-38.zip")]
    [InlineData("+Z Border", 14, "2b2tAtlas-Z-Border-render-14.zip")]
    public void Render_world_download_names_are_descriptive_and_filesystem_safe(
        string locationName, int renderId, string expected)
    {
        Assert.Equal(expected, DownloadFileNames.RenderWorldDownload(locationName, renderId));
    }
}
