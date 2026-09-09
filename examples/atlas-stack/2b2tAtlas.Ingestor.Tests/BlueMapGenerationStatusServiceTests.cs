using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using _2b2tAtlas.Server.Models;
using _2b2tAtlas.Server.Services;

namespace Atlas.Ingestor.Tests;

public sealed class BlueMapGenerationStatusServiceTests
{
    [Fact]
    public async Task Coordinated_status_exposes_three_sanitized_workers_and_resource_waiting()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"atlas-coordinator-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(cancellationToken);
            await using var context = new AtlasContext(new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync(cancellationToken);
            var path = Path.Combine(root, "status.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
            {
                SchemaVersion = 2, Coordinated = true, State = "running", UpdatedUtc = DateTimeOffset.UtcNow,
                Total = 3, Completed = 1,
                Workers = new[] {
                    new { WorkerId = 1, State = "running", Stage = "relighting", StartedUtc = DateTimeOffset.UtcNow.AddMinutes(-2), UpdatedUtc = DateTimeOffset.UtcNow,
                        Current = new { RenderId = 10, LocationId = 100, LocationName = "First", Dimension = "overworld", ArchiveSha256 = "private-source-hash" }, ProcessId = 888 },
                    new { WorkerId = 2, State = "stale", Stage = "rendering", StartedUtc = DateTimeOffset.UtcNow.AddHours(-1), UpdatedUtc = DateTimeOffset.UtcNow.AddMinutes(-35),
                        Current = new { RenderId = 11, LocationId = 101, LocationName = "Second", Dimension = "end", ArchiveSha256 = "private-source-hash" }, ProcessId = 999 },
                    new { WorkerId = 3, State = "running", Stage = "waiting for relighting resources", StartedUtc = DateTimeOffset.UtcNow.AddMinutes(-1), UpdatedUtc = DateTimeOffset.UtcNow,
                        Current = new { RenderId = 12, LocationId = 102, LocationName = "Third", Dimension = "nether", ArchiveSha256 = "private-source-hash" }, ProcessId = 1000 }
                }
            }), cancellationToken);
            var options = Options.Create(new BlueMapOptions { OutputRoot = root, StatusPath = path, ActivityLogPaths = [] });
            var service = new BlueMapGenerationStatusService(context, new BlueMapCatalogService(options), options,
                NullLogger<BlueMapGenerationStatusService>.Instance);
            var result = await service.GetAsync(cancellationToken);
            Assert.True(result.Coordinated);
            Assert.True(result.Active);
            Assert.Equal(3, result.Workers.Count);
            Assert.Equal(3, result.Workers[2].WorkerId);
            Assert.Equal("waiting for relighting resources", result.Workers[2].Stage);
            Assert.Equal("relighting", result.Workers[0].Stage);
            Assert.Equal("stale", result.Workers[1].State);
            Assert.Equal(101, result.Workers[1].Current?.LocationId);
            Assert.Contains("2 / 3 workers", result.Phase);
            var json = JsonSerializer.Serialize(result);
            Assert.DoesNotContain("private-source-hash", json);
            Assert.DoesNotContain("ProcessId", json);
            Assert.DoesNotContain(root, json);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    [Fact]
    public async Task Status_combines_batch_catalog_database_dimensions_and_storage_without_host_paths()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"atlas-bluemap-status-{Guid.NewGuid():N}");
        var outputRoot = Path.Combine(root, "outputs");
        var statusPath = Path.Combine(root, "status.json");
        Directory.CreateDirectory(outputRoot);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var dbOptions = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(dbOptions);
        await context.Database.EnsureCreatedAsync(cancellationToken);

        try
        {
            var first = AddEligibleRender(context, 1, 101, "Validated Overworld", 0);
            AddEligibleRender(context, 2, 102, "Next Nether", 1);
            await context.SaveChangesAsync(cancellationToken);
            WriteValidatedGeneration(outputRoot, first.Id, "overworld", 123456);
            await File.WriteAllTextAsync(statusPath, JsonSerializer.Serialize(new
            {
                SchemaVersion = 1,
                State = "running",
                StartedUtc = DateTimeOffset.UtcNow.AddHours(-1),
                UpdatedUtc = DateTimeOffset.UtcNow,
                Completed = 1,
                Total = 1,
                Percent = 100,
                Current = new
                {
                    RenderId = 2,
                    LocationId = 102,
                    LocationName = "Next Nether",
                    Dimension = "nether",
                    SourceSha256 = new string('f', 64)
                },
                Results = new[] { new { RenderId = 1, State = "complete" } },
                Message = string.Empty,
                OutputRoot = outputRoot,
                OutputQuotaBytes = 500L * 1024 * 1024 * 1024
            }), cancellationToken);

            var options = Options.Create(new BlueMapOptions
            {
                OutputRoot = outputRoot,
                StatusPath = statusPath,
                ActivityLogPaths = [],
                MinimumProfileVersion = 7,
                CatalogCacheSeconds = 5
            });
            var catalog = new BlueMapCatalogService(options);
            var service = new BlueMapGenerationStatusService(context, catalog, options,
                NullLogger<BlueMapGenerationStatusService>.Instance);

            var result = await service.GetAsync(cancellationToken);

            Assert.True(result.Available);
            Assert.True(result.Active);
            Assert.Equal("Active", result.Status);
            Assert.Equal(2, result.EligibleRenderCount);
            Assert.Equal(1, result.ValidatedRenderCount);
            Assert.Equal(1, result.RemainingRenderCount);
            Assert.Equal(1, result.PendingAfterSnapshot);
            Assert.Equal(1, result.OverworldValidated);
            Assert.Equal(0, result.NetherValidated);
            Assert.Equal(123456, result.OutputBytes);
            Assert.Equal(500L * 1024 * 1024 * 1024, result.OutputQuotaBytes);
            Assert.Equal(2, result.Current?.RenderId);
            Assert.Equal("Next Nether", result.Current?.LocationName);
            Assert.DoesNotContain(outputRoot, JsonSerializer.Serialize(result), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(new string('f', 64), JsonSerializer.Serialize(result), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static _2b2tAtlas.Server.Models.Render AddEligibleRender(
        AtlasContext context, int renderId, int locationId, string name, int dimension)
    {
        var now = DateTime.UtcNow.ToString("o");
        context.Locations.Add(new _2b2tAtlas.Server.Models.Location
        {
            Rowid = locationId,
            LocationUuid = Guid.NewGuid().ToString(),
            Name = name,
            Dimension = dimension,
            X = 1,
            Y = 64,
            Z = 1,
            DateAddedUtc = now,
            ModifiedUtc = now
        });
        var render = new _2b2tAtlas.Server.Models.Render
        {
            Id = renderId,
            LocationRowid = locationId,
            Name = name,
            Dimension = dimension,
            Scale = "1",
            TilesPath = $"https://example.test/{renderId}/{{z}}/{{y}}/{{x}}.png",
            DateAddedUtc = now
        };
        context.Renders.Add(render);
        context.IngestionJobs.Add(new IngestionJob
        {
            PublicId = Guid.NewGuid().ToString("N"),
            IntakeFileName = $"render-{renderId}.zip",
            Slug = $"render-{renderId}",
            Name = name,
            WorldDownloadDate = "2026-09-06",
            Source = "test",
            Scale = "1",
            Dimension = dimension == 1 ? "nether" : dimension == 2 ? "end" : "overworld",
            RenderId = renderId,
            Status = "completed",
            ArchiveSha256 = new string((char)('a' + renderId), 64),
            RequestedUtc = now
        });
        return render;
    }

    private static void WriteValidatedGeneration(string outputRoot, int renderId, string dimension, long outputBytes)
    {
        var generation = Path.Combine(outputRoot, $"render-{renderId}-source-v5.23-p7");
        var web = Path.Combine(generation, "web");
        Directory.CreateDirectory(web);
        File.WriteAllText(Path.Combine(web, "index.html"), "<!doctype html>");
        File.WriteAllText(Path.Combine(generation, "manifest.json"), JsonSerializer.Serialize(new
        {
            Status = "complete",
            RenderId = renderId,
            Dimension = dimension,
            RendererProfileVersion = 7,
            GeneratedUtc = DateTimeOffset.UtcNow,
            OutputBytes = outputBytes,
            QualityGate = new { Passed = true, LocationStartExact = true },
            RenderingProfile = new { Relight = new { FootprintAuditExact = true } }
        }));
    }
}
