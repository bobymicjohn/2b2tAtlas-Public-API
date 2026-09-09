using System.Security.Cryptography;
using System.Security.Claims;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Atlas;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using _2b2tAtlas.Server.Controllers;
using _2b2tAtlas.Server.Models;
using _2b2tAtlas.Server.Services;
using _2b2tAtlas.Server.Services.AiEnrichment;

namespace Atlas.Ingestor.Tests;

public sealed class IngestionJobsControllerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("wrong-worker-key")]
    public async Task Every_worker_endpoint_rejects_missing_or_wrong_key_before_work(string? suppliedKey)
    {
        var options = new DbContextOptionsBuilder<AtlasContext>().UseSqlite("Data Source=:memory:").Options;
        await using var context = new AtlasContext(options);
        var controller = CreateController(context, "test-worker-key-that-is-at-least-32-characters");
        controller.Request.Headers.Remove("X-Atlas-Worker-Key");
        if (suppliedKey != null) controller.Request.Headers["X-Atlas-Worker-Key"] = suppliedKey;
        Assert.IsType<UnauthorizedResult>((await controller.CreateLocal(new IngestionJobRequest())).Result);
        Assert.IsType<UnauthorizedResult>((await controller.QueueLocalIntake(new LocalIntakeQueueRequest(), TestContext.Current.CancellationToken)).Result);
        Assert.IsType<UnauthorizedResult>((await controller.Claim()).Result);
        Assert.IsType<UnauthorizedResult>((await controller.UpdateStatus("nonexistent", new IngestionJobUpdate())).Result);
    }

    [Fact]
    public async Task Claim_and_completion_are_token_bound_and_idempotent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var dbOptions = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(dbOptions);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        var job = CreateJob();
        context.IngestionJobs.Add(job);
        await context.SaveChangesAsync(cancellationToken);
        context.ChangeTracker.Clear();

        const string workerKey = "test-worker-key-that-is-at-least-32-characters";
        var controller = CreateController(context, workerKey);
        var claimResult = await controller.Claim();
        var claimed = Assert.IsType<IngestionJobDto>(Assert.IsType<OkObjectResult>(claimResult.Result).Value);
        Assert.NotNull(claimed.ClaimToken);
        Assert.Equal("claimed", claimed.Status);

        var allResult = await controller.GetAll();
        var visibleJobs = Assert.IsAssignableFrom<IReadOnlyList<IngestionJobDto>>(
            Assert.IsType<OkObjectResult>(allResult.Result).Value);
        Assert.Null(Assert.Single(visibleJobs).ClaimToken);
        Assert.IsType<ConflictObjectResult>(await controller.Cancel(job.PublicId));

        var inspection = CreateInspection();
        inspection.LastPlayedUtc = new DateTime(2019, 7, 14, 12, 0, 0, DateTimeKind.Utc);
        var inspectionResult = await controller.UpdateStatus(job.PublicId, new IngestionJobUpdate
        {
            ClaimToken = claimed.ClaimToken!,
            Status = "running",
            Stage = "prepare",
            ProgressPercent = 25,
            Inspection = inspection,
        });
        var inspected = Assert.IsType<IngestionJobDto>(
            Assert.IsType<OkObjectResult>(inspectionResult.Result).Value);
        Assert.Equal("1.12.2", inspected.Inspection?.VersionName);
        context.ChangeTracker.Clear();
        var inspectedRow = await context.IngestionJobs.SingleAsync(cancellationToken);
        Assert.Contains("overlap review", inspectedRow.InspectionJson);
        Assert.Equal("2019-07-14", inspectedRow.WorldDownloadDate);

        var validRender = CreateRender(job);
        var forgedRender = CreateRender(job);
        forgedRender.TilesPath = "https://tiles.atlas.example/AtlasTiles/existing/overworld/{z}/{y}/{x}.png";
        var forged = await controller.UpdateStatus(job.PublicId, new IngestionJobUpdate
        {
            ClaimToken = claimed.ClaimToken!,
            Status = "completed",
            Stage = "registered",
            ArchiveSha256 = new string('a', 64),
            LocationRender = forgedRender,
        });
        Assert.True(forged.Result is BadRequestObjectResult,
            JsonSerializer.Serialize((forged.Result as ObjectResult)?.Value));

        var wrongToken = await controller.UpdateStatus(job.PublicId, new IngestionJobUpdate
        {
            ClaimToken = new string('A', 43),
            Status = "running",
            Stage = "render",
        });
        Assert.IsType<UnauthorizedResult>(wrongToken.Result);

        var completion = new IngestionJobUpdate
        {
            ClaimToken = claimed.ClaimToken!,
            Status = "completed",
            Stage = "registered",
            Message = "Registered location render.",
            ArchiveSha256 = new string('a', 64),
            LocationRender = validRender,
        };
        var completedResult = Assert.IsType<OkObjectResult>(
            (await controller.UpdateStatus(job.PublicId, completion)).Result);
        var completed = Assert.IsType<IngestionJobDto>(completedResult.Value);
        Assert.Equal(100, completed.ProgressPercent);
        Assert.IsType<OkObjectResult>((await controller.UpdateStatus(job.PublicId, completion)).Result);
        Assert.Equal(1, await context.Locations.CountAsync(cancellationToken));
        var render = await context.Renders.SingleAsync(cancellationToken);
        Assert.Equal(-1024, render.MinX);
        Assert.Equal("atlas-sparse-v1", render.CoordinateScheme);
        Assert.Equal(render.Id, (await context.IngestionJobs.SingleAsync(cancellationToken)).RenderId);
        var completedJobs = Assert.IsAssignableFrom<IReadOnlyList<IngestionJobDto>>(
            Assert.IsType<OkObjectResult>((await controller.GetAll()).Result).Value);
        Assert.Equal(render.LocationRowid, Assert.Single(completedJobs).ExistingLocationId);
        Assert.Equal(100, Assert.Single(completedJobs).ProgressPercent);
        Assert.Equal(1, await context.AuditLogs.CountAsync(
            entry => entry.Action == "ingestion.complete", cancellationToken));
    }

    [Fact]
    public async Task Archive_capture_preserves_catalog_date_instead_of_museum_last_played()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var dbOptions = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(dbOptions);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        var job = CreateJob();
        job.WorldDownloadDate = "2025-11-01";
        job.UseArchiveLastPlayed = 1;
        context.IngestionJobs.Add(job);
        await context.SaveChangesAsync(cancellationToken);
        context.ChangeTracker.Clear();

        var controller = CreateController(context, "test-worker-key-that-is-at-least-32-characters");
        var claimed = Assert.IsType<IngestionJobDto>(
            Assert.IsType<OkObjectResult>((await controller.Claim()).Result).Value);
        var inspection = CreateInspection();
        inspection.LastPlayedUtc = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);
        inspection.ArchiveEvidence = new ArchiveWdlEvidence { IsArchiveSource = true };

        Assert.IsType<OkObjectResult>((await controller.UpdateStatus(job.PublicId, new IngestionJobUpdate
        {
            ClaimToken = claimed.ClaimToken!,
            Status = "running",
            Stage = "prepare",
            ProgressPercent = 25,
            Inspection = inspection,
        })).Result);

        context.ChangeTracker.Clear();
        Assert.Equal("2025-11-01", (await context.IngestionJobs.SingleAsync(cancellationToken)).WorldDownloadDate);
    }

    [Fact]
    public async Task Completion_reuses_existing_warp_and_links_the_new_render_without_duplicate()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(options);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        var location = new _2b2tAtlas.Server.Models.Location
        {
            LocationUuid = Guid.NewGuid().ToString(), Name = "Temple of the Talion", Dimension = 0,
            X = -169_902, Y = 64, Z = 311_836, DateAddedUtc = DateTime.UtcNow.ToString("o"),
        };
        context.Locations.Add(location);
        await context.SaveChangesAsync(cancellationToken);
        var warp = new _2b2tAtlas.Server.Models.Warp
        {
            WarpUuid = Guid.NewGuid().ToString(), LocationUuidFk = location.LocationUuid,
            LocationRowid = location.Rowid, Name = "Temple_of_the_Talion_2017-03-06",
            TimeAdded = DateTime.UtcNow.ToString("o"),
        };
        context.Warps.Add(warp);
        var job = CreateJob();
        job.Source = "The Archive automated sync";
        job.ExistingLocationId = location.Rowid;
        job.ArchiveWarpName = warp.Name;
        job.ArchiveWarpSource = "archive-download-report";
        job.ArchiveSha256 = new string('b', 64);
        context.IngestionJobs.Add(job);
        await context.SaveChangesAsync(cancellationToken);
        context.ChangeTracker.Clear();

        var controller = CreateController(context, "test-worker-key-that-is-at-least-32-characters");
        var claim = Assert.IsType<IngestionJobDto>(Assert.IsType<OkObjectResult>((await controller.Claim()).Result).Value);
        var completion = Completion(job, claim.ClaimToken!);
        completion.ArchiveSha256 = job.ArchiveSha256;
        var completed = Assert.IsType<IngestionJobDto>(Assert.IsType<OkObjectResult>(
            (await controller.UpdateStatus(job.PublicId, completion)).Result).Value);

        Assert.Equal(location.Rowid, completed.ExistingLocationId);
        Assert.Equal(1, await context.Warps.CountAsync(cancellationToken));
        var storedWarp = await context.Warps.SingleAsync(cancellationToken);
        Assert.Equal(job.ArchiveSha256, storedWarp.ArchiveSha256);
        Assert.Equal(storedWarp.Id, completed.WarpId);
        var storedRender = await context.Renders.SingleAsync(cancellationToken);
        Assert.Equal(location.Rowid, storedRender.LocationRowid);
        Assert.Equal(storedWarp.Id, storedRender.ArchiveWarpId);
        Assert.Equal("archive-collector", storedRender.Source);
        Assert.Equal("Temple of the Talion", storedRender.Name);
        Assert.Null(storedRender.Description);
    }

    [Fact]
    public async Task Exact_warp_recapture_replaces_its_one_linked_render()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(options);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        var location = new _2b2tAtlas.Server.Models.Location
        {
            LocationUuid = Guid.NewGuid().ToString(), Name = "Fusionia I", Dimension = 0,
            X = -1_070_536, Y = 64, Z = 1_472_920, DateAddedUtc = DateTime.UtcNow.ToString("o"),
        };
        context.Locations.Add(location);
        await context.SaveChangesAsync(cancellationToken);
        var warp = new _2b2tAtlas.Server.Models.Warp
        {
            WarpUuid = Guid.NewGuid().ToString(), LocationUuidFk = location.LocationUuid,
            LocationRowid = location.Rowid, Name = "Fusionia_I_2025-03-28",
            TimeAdded = DateTime.UtcNow.ToString("o"), ArchiveSha256 = new string('b', 64),
        };
        context.Warps.Add(warp);
        await context.SaveChangesAsync(cancellationToken);
        var originalRender = new _2b2tAtlas.Server.Models.Render
        {
            LocationRowid = location.Rowid, LocationRow = location, Name = "Fusionia I",
            Dimension = 0, Scale = "1", TilesPath = "https://tiles.atlas.example/AtlasTiles/original/overworld/{z}/{y}/{x}.png",
            DateAddedUtc = DateTime.UtcNow.ToString("o"), Source = "archive-collector", ArchiveWarpId = warp.Id,
        };
        context.Renders.Add(originalRender);
        var job = CreateJob();
        job.Name = "Fusionia I 2025 03 28";
        job.Source = "The Archive automated sync";
        job.ArchiveWarpName = warp.Name;
        job.ArchiveWarpSource = "archive-download-report";
        job.ArchiveSha256 = new string('c', 64);
        job.MatchResolved = 0;
        context.IngestionJobs.Add(job);
        await context.SaveChangesAsync(cancellationToken);
        context.ChangeTracker.Clear();

        var controller = CreateController(context, "test-worker-key-that-is-at-least-32-characters");
        var claim = Assert.IsType<IngestionJobDto>(Assert.IsType<OkObjectResult>((await controller.Claim()).Result).Value);
        var prepare = await controller.UpdateStatus(job.PublicId, new IngestionJobUpdate
        {
            ClaimToken = claim.ClaimToken!, Status = "running", Stage = "prepare", ProgressPercent = 20,
            Inspection = CreateInspection(),
        });
        var prepared = Assert.IsType<IngestionJobDto>(Assert.IsType<OkObjectResult>(prepare.Result).Value);
        Assert.True(prepared.RerenderRequested);
        var completed = Completion(job, claim.ClaimToken!);
        completed.ArchiveSha256 = job.ArchiveSha256;
        Assert.IsType<OkObjectResult>((await controller.UpdateStatus(job.PublicId, completed)).Result);

        Assert.Equal(1, await context.Renders.CountAsync(cancellationToken));
        var replaced = await context.Renders.SingleAsync(cancellationToken);
        Assert.Equal(originalRender.Id, replaced.Id);
        Assert.Equal(CreateRender(job).TilesPath, replaced.TilesPath);
        Assert.Equal(warp.Id, replaced.ArchiveWarpId);
        Assert.Equal("Fusionia I", replaced.Name);
        Assert.Equal(job.ArchiveSha256, (await context.Warps.SingleAsync(cancellationToken)).ArchiveSha256);
    }

    [Fact]
    public async Task Concurrent_completion_creates_exactly_one_location_render()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var connectionString = $"Data Source={temporary.Resolve("queue.db")};Default Timeout=10;Pooling=False";
        var dbOptions = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connectionString).Options;
        var job = CreateJob();
        const string workerKey = "test-worker-key-that-is-at-least-32-characters";
        string claimToken;
        await using (var setup = new AtlasContext(dbOptions))
        {
            await setup.Database.EnsureCreatedAsync(cancellationToken);
            setup.IngestionJobs.Add(job);
            await setup.SaveChangesAsync(cancellationToken);
            var claim = await CreateController(setup, workerKey).Claim();
            claimToken = Assert.IsType<IngestionJobDto>(
                Assert.IsType<OkObjectResult>(claim.Result).Value).ClaimToken!;
        }

        await using var firstContext = new AtlasContext(dbOptions);
        await using var secondContext = new AtlasContext(dbOptions);
        var first = CreateController(firstContext, workerKey);
        var second = CreateController(secondContext, workerKey);
        var firstTask = first.UpdateStatus(job.PublicId, Completion(job, claimToken));
        var secondTask = second.UpdateStatus(job.PublicId, Completion(job, claimToken));
        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.Contains(results, result => result.Result is OkObjectResult);
        Assert.All(results, result => Assert.True(
            result.Result is OkObjectResult or ConflictObjectResult,
            JsonSerializer.Serialize((result.Result as ObjectResult)?.Value)));
        await using var verification = new AtlasContext(dbOptions);
        Assert.Equal(1, await verification.Renders.CountAsync(cancellationToken));
        Assert.Equal(1, await verification.Locations.CountAsync(cancellationToken));
    }

    [Fact]
    public async Task Completion_enqueues_location_for_background_enrichment()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var dbOptions = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(dbOptions);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        var job = CreateJob();
        context.IngestionJobs.Add(job);
        await context.SaveChangesAsync(cancellationToken);
        context.ChangeTracker.Clear();

        const string workerKey = "test-worker-key-that-is-at-least-32-characters";
        var queue = new EnrichmentQueue();
        var controller = CreateController(context, workerKey, enrichmentQueue: queue);
        var claim = Assert.IsType<IngestionJobDto>(Assert.IsType<OkObjectResult>((await controller.Claim()).Result).Value);
        Assert.IsType<OkObjectResult>(
            (await controller.UpdateStatus(job.PublicId, Completion(job, claim.ClaimToken!))).Result);

        var location = await context.Locations.SingleAsync(cancellationToken);
        Assert.True(queue.Reader.TryRead(out var enqueuedId));
        Assert.Equal(location.Rowid, enqueuedId);
    }

    [Fact]
    public async Task Prepare_without_match_parks_then_resolve_requeues()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var dbOptions = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(dbOptions);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        var job = CreateJob();
        job.MatchResolved = 0; // exercise the auto-match path (no locations exist to match)
        context.IngestionJobs.Add(job);
        await context.SaveChangesAsync(cancellationToken);
        context.ChangeTracker.Clear();

        var controller = CreateController(context, "test-worker-key-that-is-at-least-32-characters");
        var claimed = Assert.IsType<IngestionJobDto>(
            Assert.IsType<OkObjectResult>((await controller.Claim()).Result).Value);

        var parked = Assert.IsType<IngestionJobDto>(Assert.IsType<OkObjectResult>(
            (await controller.UpdateStatus(job.PublicId, new IngestionJobUpdate
            {
                ClaimToken = claimed.ClaimToken!,
                Status = "running",
                Stage = "prepare",
                ProgressPercent = 25,
                Inspection = CreateInspection(),
            })).Result).Value);
        Assert.Equal("needs-match", parked.Status);

        // A parked job refuses completion until it is resolved.
        Assert.IsType<ConflictObjectResult>(
            (await controller.UpdateStatus(job.PublicId, Completion(job, claimed.ClaimToken!))).Result);

        // Resolving to create-new re-queues the job for rendering.
        var resolved = Assert.IsType<IngestionJobDto>(Assert.IsType<OkObjectResult>(
            (await controller.ResolveMatch(job.PublicId, new ResolveMatchRequest { LocationId = null })).Result).Value);
        Assert.Equal("queued", resolved.Status);
        var stored = await context.IngestionJobs.SingleAsync(cancellationToken);
        Assert.Equal(1, stored.MatchResolved);
        Assert.Null(stored.ExistingLocationId);
    }

    [Fact]
    public async Task Completion_refuses_stale_location_when_match_decision_is_still_review()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(options);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        var location = new _2b2tAtlas.Server.Models.Location
        {
            LocationUuid = Guid.NewGuid().ToString(), Name = "Protected Existing Base", Dimension = 0,
            X = 5_168_556, Y = 64, Z = 10_320_373, DateAddedUtc = DateTime.UtcNow.ToString("o"),
        };
        context.Locations.Add(location);
        await context.SaveChangesAsync(cancellationToken);
        var job = CreateJob();
        job.ExistingLocationId = location.Rowid;
        job.MatchResolved = 1;
        job.MatchDecision = "review";
        context.IngestionJobs.Add(job);
        await context.SaveChangesAsync(cancellationToken);
        context.ChangeTracker.Clear();

        var controller = CreateController(context, "test-worker-key-that-is-at-least-32-characters");
        var claimed = Assert.IsType<IngestionJobDto>(
            Assert.IsType<OkObjectResult>((await controller.Claim()).Result).Value);
        var response = await controller.UpdateStatus(job.PublicId, Completion(job, claimed.ClaimToken!));

        Assert.IsType<ConflictObjectResult>(response.Result);
        context.ChangeTracker.Clear();
        var protectedLocation = await context.Locations.SingleAsync(cancellationToken);
        Assert.Equal(5_168_556, protectedLocation.X);
        Assert.Equal(10_320_373, protectedLocation.Z);
        Assert.Empty(await context.Renders.ToListAsync(cancellationToken));
        Assert.Empty(await context.Warps.ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task Claim_recovers_only_abandoned_upload_match_preflight()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(options);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        var job = CreateJob();
        job.Status = "matching";
        job.RequestedUtc = DateTime.UtcNow.AddMinutes(-10).ToString("o");
        context.IngestionJobs.Add(job);
        await context.SaveChangesAsync(cancellationToken);
        context.ChangeTracker.Clear();

        var controller = CreateController(context, "test-worker-key-that-is-at-least-32-characters");
        var claimed = Assert.IsType<IngestionJobDto>(
            Assert.IsType<OkObjectResult>((await controller.Claim()).Result).Value);

        Assert.Equal(job.PublicId, claimed.Id);
        Assert.Equal("claimed", claimed.Status);
        Assert.Equal(1, claimed.AttemptCount);
    }

    [Fact]
    public async Task Upload_auto_detect_queues_every_recognized_dimension()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var intakeRoot = temporary.Resolve("intake");
        Directory.CreateDirectory(intakeRoot);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var dbOptions = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(dbOptions);
        await context.Database.EnsureCreatedAsync(cancellationToken);

        const string workerKey = "test-worker-key-that-is-at-least-32-characters";
        var controller = CreateController(context, workerKey, intakeRoot);
        var metadata = JsonSerializer.Serialize(new IngestionJobRequest
        {
            IntakeFileName = "pending.zip",
            Slug = "plus-z-wb",
            Name = "Plus Z WB",
            WorldDownloadDate = "2021-06-29",
            Source = "2b2t world download",
            Scale = "256k",
            Dimension = "auto",
        });
        // Carries all three dimensions; archive exports may bundle meaningful data in each.
        using var archive = BuildWorldZip(
            "world/level.dat",
            "world/region/r.0.0.mca",
            "world/DIM-1/region/r.0.0.mca",
            "world/DIM1/region/r.0.0.mca");
        var file = new FormFile(archive, 0, archive.Length, "archive", "plus-z-wb.zip")
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/zip",
        };

        var result = await controller.Upload(metadata, file, cancellationToken);
        Assert.IsType<CreatedAtActionResult>(result.Result);

        var jobs = await context.IngestionJobs.OrderBy(job => job.Dimension).ToListAsync(cancellationToken);
        Assert.Equal(new[] { "end", "nether", "overworld" }, jobs.Select(job => job.Dimension).ToArray());
        Assert.All(jobs, job => Assert.Equal("plus-z-wb", job.Slug));
        // Both dimension jobs share the one stored intake ZIP.
        Assert.Single(jobs.Select(job => job.IntakeFileName).Distinct());
        Assert.Single(Directory.GetFiles(intakeRoot, "*.zip"));
    }

    [Fact]
    public async Task Upload_auto_detect_recognizes_alpha_and_namespaced_vanilla_layouts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var intakeRoot = temporary.Resolve("intake");
        Directory.CreateDirectory(intakeRoot);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(options);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        var controller = CreateController(context, "test-worker-key-that-is-at-least-32-characters", intakeRoot);
        var metadata = JsonSerializer.Serialize(new IngestionJobRequest
        {
            IntakeFileName = "pending.zip", Slug = "historic-mixed", Name = "Historic Mixed",
            WorldDownloadDate = "2011-01-01", Source = "2b2t world download", Scale = "1", Dimension = "auto",
        });
        using var archive = BuildWorldZip(
            "world/level.dat",
            "world/0/0/c.-1.a.dat",
            "world/dimensions/minecraft/the_nether/region/r.1.-2.mca",
            "world/dimensions/minecraft/the_end/region/r.2.-3.mcr");
        var file = new FormFile(archive, 0, archive.Length, "archive", "historic-mixed.zip")
        {
            Headers = new HeaderDictionary(), ContentType = "application/zip",
        };

        Assert.IsType<CreatedAtActionResult>((await controller.Upload(metadata, file, cancellationToken)).Result);
        var jobs = await context.IngestionJobs.OrderBy(job => job.Dimension).ToListAsync(cancellationToken);
        Assert.Equal(new[] { "end", "nether", "overworld" }, jobs.Select(job => job.Dimension).ToArray());
    }

    [Fact]
    public async Task Upload_auto_detect_does_not_guess_custom_museum_dimension()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var intakeRoot = temporary.Resolve("intake");
        Directory.CreateDirectory(intakeRoot);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(options);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        var controller = CreateController(context, "test-worker-key-that-is-at-least-32-characters", intakeRoot);
        var metadata = JsonSerializer.Serialize(new IngestionJobRequest
        {
            IntakeFileName = "pending.zip", Slug = "museum-room", Name = "Museum Room",
            WorldDownloadDate = "2020-01-01", Source = "Archive exhibit", Scale = "1", Dimension = "auto",
        });
        using var archive = BuildWorldZip(
            "world/level.dat", "world/dimensions/thearchive/exhibit_42/region/r.0.0.mca");
        var file = new FormFile(archive, 0, archive.Length, "archive", "museum-room.zip")
        {
            Headers = new HeaderDictionary(), ContentType = "application/zip",
        };

        var result = await controller.Upload(metadata, file, cancellationToken);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(context.IngestionJobs);
    }

    [Fact]
    public async Task Chunked_upload_enforces_offsets_and_queues_after_complete()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var intakeRoot = temporary.Resolve("intake");
        Directory.CreateDirectory(intakeRoot);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(options);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        var controller = CreateController(context, "test-worker-key-that-is-at-least-32-characters", intakeRoot);
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "7")], "test"));
        var request = new IngestionJobRequest
        {
            IntakeFileName = "pending.zip",
            Slug = "chunked-world",
            Name = "Chunked World",
            WorldDownloadDate = "2026-01-01",
            Source = "test",
            Scale = "1k",
            Dimension = "auto",
        };
        using var archive = BuildWorldZip("world/level.dat", "world/region/r.0.0.mca");
        var bytes = archive.ToArray();
        var started = Assert.IsType<ChunkedUploadSession>(Assert.IsType<OkObjectResult>(
            (await controller.StartUploadSession(new ChunkedUploadStartRequest
            {
                Metadata = JsonSerializer.Serialize(request),
                FileName = "world.zip",
                TotalBytes = bytes.Length,
            }, cancellationToken)).Result).Value);

        controller.Request.Body = new MemoryStream(bytes);
        controller.Request.ContentLength = bytes.Length;
        Assert.IsType<ConflictObjectResult>(await controller.UploadChunk(started.Id, 1, cancellationToken));
        controller.Request.Body = new MemoryStream(bytes);
        controller.Request.ContentLength = bytes.Length;
        Assert.IsType<NoContentResult>(await controller.UploadChunk(started.Id, 0, cancellationToken));
        Assert.IsType<CreatedAtActionResult>(
            (await controller.CompleteUploadSession(started.Id, cancellationToken)).Result);

        var job = await context.IngestionJobs.SingleAsync(cancellationToken);
        Assert.Equal("chunked-world", job.Slug);
        Assert.True(Directory.EnumerateFiles(temporary.Resolve("archive"), "*.zip", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task Upload_auto_detect_queues_end_only_when_end_is_the_sole_dimension()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var intakeRoot = temporary.Resolve("intake");
        Directory.CreateDirectory(intakeRoot);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var dbOptions = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(dbOptions);
        await context.Database.EnsureCreatedAsync(cancellationToken);

        const string workerKey = "test-worker-key-that-is-at-least-32-characters";
        var controller = CreateController(context, workerKey, intakeRoot);
        var metadata = JsonSerializer.Serialize(new IngestionJobRequest
        {
            IntakeFileName = "pending.zip",
            Slug = "end-base",
            Name = "End Base",
            WorldDownloadDate = "2021-06-29",
            Source = "2b2t world download",
            Scale = "256k",
            Dimension = "auto",
        });
        using var archive = BuildWorldZip("world/level.dat", "world/DIM1/region/r.0.0.mca");
        var file = new FormFile(archive, 0, archive.Length, "archive", "end-base.zip")
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/zip",
        };

        Assert.IsType<CreatedAtActionResult>((await controller.Upload(metadata, file, cancellationToken)).Result);
        var job = await context.IngestionJobs.SingleAsync(cancellationToken);
        Assert.Equal("end", job.Dimension);
    }

    [Fact]
    public async Task CreateLocal_slug_uniqueness_is_per_dimension_and_rejects_auto()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var dbOptions = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(dbOptions);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        const string workerKey = "test-worker-key-that-is-at-least-32-characters";
        var controller = CreateController(context, workerKey);

        IngestionJobRequest Request(string dimension) => new()
        {
            IntakeFileName = "wb.zip",
            Slug = "shared-base",
            Name = "Shared Base",
            WorldDownloadDate = "2021-06-29",
            Source = "2b2t world download",
            Scale = "256k",
            Dimension = dimension,
        };

        Assert.IsType<CreatedAtActionResult>((await controller.CreateLocal(Request("overworld"))).Result);
        Assert.IsType<CreatedAtActionResult>((await controller.CreateLocal(Request("nether"))).Result);
        Assert.IsType<ConflictObjectResult>((await controller.CreateLocal(Request("overworld"))).Result);
        Assert.IsType<BadRequestObjectResult>((await controller.CreateLocal(Request("auto"))).Result);
        Assert.Equal(2, await context.IngestionJobs.CountAsync(cancellationToken));
    }

    [Fact]
    public async Task Upload_auto_matches_location_at_upload_time()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var intakeRoot = temporary.Resolve("intake");
        Directory.CreateDirectory(intakeRoot);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var dbOptions = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(dbOptions);
        await context.Database.EnsureCreatedAsync(cancellationToken);

        // Region r.0.0 => block bounds [0,512) x [0,512) => centroid (256, 256). Seed a location there.
        var location = new _2b2tAtlas.Server.Models.Location
        {
            LocationUuid = Guid.NewGuid().ToString(),
            Name = "Border Base",
            Dimension = 0,
            X = 256,
            Y = 64,
            Z = 256,
            DateAddedUtc = DateTime.UtcNow.ToString("o"),
        };
        context.Locations.Add(location);
        await context.SaveChangesAsync(cancellationToken);
        context.ChangeTracker.Clear();

        const string workerKey = "test-worker-key-that-is-at-least-32-characters";
        var controller = CreateController(context, workerKey, intakeRoot);
        var metadata = JsonSerializer.Serialize(new IngestionJobRequest
        {
            IntakeFileName = "pending.zip",
            Slug = "border-base",
            Name = "Border Base",
            WorldDownloadDate = "2021-06-29",
            Source = "2b2t world download",
            Scale = "256k",
            Dimension = "auto",
        });
        using var archive = BuildWorldZip("world/level.dat", "world/region/r.0.0.mca");
        var file = new FormFile(archive, 0, archive.Length, "archive", "border-base.zip")
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/zip",
        };

        Assert.IsType<CreatedAtActionResult>((await controller.Upload(metadata, file, cancellationToken)).Result);
        var job = await context.IngestionJobs.SingleAsync(cancellationToken);
        // Matched at upload time (before any worker render): attached and resolved, still queued for rendering.
        Assert.Equal(location.Rowid, job.ExistingLocationId);
        Assert.Equal(1, job.MatchResolved);
        Assert.Equal("queued", job.Status);
    }

    [Fact]
    public async Task Upload_defers_an_unattributed_no_location_decision_to_worker_inspection()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var intakeRoot = temporary.Resolve("intake");
        Directory.CreateDirectory(intakeRoot);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var dbOptions = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(dbOptions);
        await context.Database.EnsureCreatedAsync(cancellationToken);

        const string workerKey = "test-worker-key-that-is-at-least-32-characters";
        var controller = CreateController(context, workerKey, intakeRoot);
        var metadata = JsonSerializer.Serialize(new IngestionJobRequest
        {
            IntakeFileName = "pending.zip",
            Slug = "orphan-base",
            Name = "Orphan Base",
            WorldDownloadDate = "2021-06-29",
            Source = "2b2t world download",
            Scale = "256k",
            Dimension = "auto",
        });
        using var archive = BuildWorldZip("world/level.dat", "world/region/r.0.0.mca");
        var file = new FormFile(archive, 0, archive.Length, "archive", "orphan-base.zip")
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/zip",
        };

        Assert.IsType<CreatedAtActionResult>((await controller.Upload(metadata, file, cancellationToken)).Result);
        var job = await context.IngestionJobs.SingleAsync(cancellationToken);
        // Shallow ZIP inspection cannot prove existing vs new; authoritative worker inspection decides.
        Assert.Equal("queued", job.Status);
        Assert.Null(job.ExistingLocationId);
    }

    [Fact]
    public async Task MatchPreview_returns_confident_match_and_parks_when_none_near()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var dbOptions = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(dbOptions);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        context.Locations.Add(new _2b2tAtlas.Server.Models.Location
        {
            LocationUuid = Guid.NewGuid().ToString(),
            Name = "Border Base",
            Dimension = 0,
            X = 256,
            Y = 64,
            Z = 256,
            DateAddedUtc = DateTime.UtcNow.ToString("o"),
        });
        await context.SaveChangesAsync(cancellationToken);

        const string workerKey = "test-worker-key-that-is-at-least-32-characters";
        var controller = CreateController(context, workerKey);
        var result = await controller.MatchPreview(new MatchPreviewRequest
        {
            Name = "Border Base",
            Candidates = new[]
            {
                new MatchPreviewCandidate("overworld", 256, 256),
                new MatchPreviewCandidate("nether", 9_000_000, 9_000_000),
            },
        });
        var previews = Assert.IsAssignableFrom<IReadOnlyList<MatchPreviewResult>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("Border Base", previews.Single(p => p.Dimension == "overworld").AutoAttachName);
        Assert.Null(previews.Single(p => p.Dimension == "nether").AutoAttachName);
    }

    [Fact]
    public async Task Upload_density_centroid_ignores_spawn_and_matches_the_base()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var intakeRoot = temporary.Resolve("intake");
        Directory.CreateDirectory(intakeRoot);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var dbOptions = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(dbOptions);
        await context.Database.EnsureCreatedAsync(cancellationToken);

        // A small spawn portion at 0,0 plus a denser base cluster ~ (51541, 51541).
        context.Locations.Add(new _2b2tAtlas.Server.Models.Location
        {
            LocationUuid = Guid.NewGuid().ToString(), Name = "Spawn", Dimension = 0, X = 0, Y = 64, Z = 0,
            DateAddedUtc = DateTime.UtcNow.ToString("o"),
        });
        var baseLocation = new _2b2tAtlas.Server.Models.Location
        {
            LocationUuid = Guid.NewGuid().ToString(), Name = "Distant Base", Dimension = 0, X = 51541, Y = 64, Z = 51541,
            DateAddedUtc = DateTime.UtcNow.ToString("o"),
        };
        context.Locations.Add(baseLocation);
        await context.SaveChangesAsync(cancellationToken);
        context.ChangeTracker.Clear();

        const string workerKey = "test-worker-key-that-is-at-least-32-characters";
        var controller = CreateController(context, workerKey, intakeRoot);
        var metadata = JsonSerializer.Serialize(new IngestionJobRequest
        {
            IntakeFileName = "pending.zip", Slug = "distant-base", Name = "Distant Base",
            WorldDownloadDate = "2021-06-29", Source = "2b2t world download", Scale = "256k", Dimension = "overworld",
        });
        using var archive = BuildWorldZip(
            "world/level.dat",
            // spawn portion (sparse)
            "world/region/r.0.0.mca", "world/region/r.0.1.mca", "world/region/r.1.0.mca",
            // base cluster (dense)
            "world/region/r.99.100.mca", "world/region/r.100.99.mca", "world/region/r.100.100.mca",
            "world/region/r.100.101.mca", "world/region/r.101.100.mca", "world/region/r.101.101.mca");
        var file = new FormFile(archive, 0, archive.Length, "archive", "distant-base.zip")
        {
            Headers = new HeaderDictionary(), ContentType = "application/zip",
        };

        Assert.IsType<CreatedAtActionResult>((await controller.Upload(metadata, file, cancellationToken)).Result);
        var job = await context.IngestionJobs.SingleAsync(cancellationToken);
        Assert.Equal(baseLocation.Rowid, job.ExistingLocationId);
    }

    [Fact]
    public async Task Upload_auto_matches_unique_archive_warp_when_terrain_coordinates_agree()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var intakeRoot = temporary.Resolve("intake");
        Directory.CreateDirectory(intakeRoot);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(options);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        var location = new _2b2tAtlas.Server.Models.Location
        {
            LocationUuid = Guid.NewGuid().ToString(), Name = "Temple of the Talion", Dimension = 0,
            X = 51_456, Y = 64, Z = 51_456, DateAddedUtc = DateTime.UtcNow.ToString("o"),
        };
        context.Locations.Add(location);
        await context.SaveChangesAsync(cancellationToken);
        context.Warps.Add(new _2b2tAtlas.Server.Models.Warp
        {
            WarpUuid = Guid.NewGuid().ToString(), LocationUuidFk = location.LocationUuid,
            LocationRowid = location.Rowid, Name = "Temple_of_the_Talion_2017-03-06",
            TimeAdded = DateTime.UtcNow.ToString("o"),
        });
        await context.SaveChangesAsync(cancellationToken);
        context.ChangeTracker.Clear();

        var controller = CreateController(context, "test-worker-key-that-is-at-least-32-characters", intakeRoot);
        var metadata = JsonSerializer.Serialize(new IngestionJobRequest
        {
            IntakeFileName = "pending.zip", Slug = "archive-temple", Name = "Terbin capture",
            WorldDownloadDate = "2017-03-06", Source = "The Archive", Scale = "1", Dimension = "overworld",
            WorldRoot = "world",
        });
        var report = "{\"downloadName\":\"Temple_of_the_Talion_2017-03-06\",\"sourceAddress\":\"survival.thearchive.world\",\"dimensionName\":\"minecraft:overworld\"}";
        using var archive = BuildArchiveWdlZip(report, "world/level.dat", "world/region/r.100.100.mca");
        var file = new FormFile(archive, 0, archive.Length, "archive", "renamed.zip")
        {
            Headers = new HeaderDictionary(), ContentType = "application/zip",
        };

        var queued = Assert.IsType<IngestionJobDto>(Assert.IsType<CreatedAtActionResult>(
            (await controller.Upload(metadata, file, cancellationToken)).Result).Value);

        Assert.Equal(location.Rowid, queued.ExistingLocationId);
        Assert.True(queued.ArchiveEvidence?.IsArchiveSource);
        Assert.Contains("exact existing Archive warp", queued.Message);
    }

    [Fact]
    public async Task Upload_byte_identical_wdl_recovers_existing_warp_even_when_renamed_and_far_away()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var intakeRoot = temporary.Resolve("intake");
        Directory.CreateDirectory(intakeRoot);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(options);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        var location = new _2b2tAtlas.Server.Models.Location
        {
            LocationUuid = Guid.NewGuid().ToString(), Name = "Known Museum Build", Dimension = 0,
            X = -2_000_000, Y = 64, Z = 2_000_000, DateAddedUtc = DateTime.UtcNow.ToString("o"),
        };
        context.Locations.Add(location);
        await context.SaveChangesAsync(cancellationToken);

        using var archive = BuildWorldZip("world/level.dat", "world/region/r.100.100.mca");
        var sha = Convert.ToHexStringLower(SHA256.HashData(archive.ToArray()));
        context.Warps.Add(new _2b2tAtlas.Server.Models.Warp
        {
            WarpUuid = Guid.NewGuid().ToString(), LocationUuidFk = location.LocationUuid,
            LocationRowid = location.Rowid, Name = "Canonical_Archive_Warp_2018-07-04",
            TimeAdded = DateTime.UtcNow.ToString("o"), ArchiveSha256 = sha,
        });
        await context.SaveChangesAsync(cancellationToken);
        context.ChangeTracker.Clear();

        var controller = CreateController(context, "test-worker-key-that-is-at-least-32-characters", intakeRoot);
        var metadata = JsonSerializer.Serialize(new IngestionJobRequest
        {
            IntakeFileName = "pending.zip", Slug = "renamed-identical-capture", Name = "Unknown export",
            WorldDownloadDate = "2018-07-04", Source = "Unlabeled disk import", Scale = "1",
            Dimension = "overworld", WorldRoot = "world",
        });
        var file = new FormFile(archive, 0, archive.Length, "archive", "totally-renamed.zip")
        {
            Headers = new HeaderDictionary(), ContentType = "application/zip",
        };

        var queued = Assert.IsType<IngestionJobDto>(Assert.IsType<CreatedAtActionResult>(
            (await controller.Upload(metadata, file, cancellationToken)).Result).Value);

        Assert.Equal(location.Rowid, queued.ExistingLocationId);
        Assert.Equal("Canonical_Archive_Warp_2018-07-04", queued.ArchiveWarpName);
        Assert.Equal("existing-archive-sha", queued.ArchiveWarpSource);
        Assert.Equal(1, queued.MatchConfidence);
    }

    [Fact]
    public async Task Trusted_archive_wdl_with_no_plausible_match_creates_location_warp_and_render_unattended()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var intakeRoot = temporary.Resolve("intake");
        Directory.CreateDirectory(intakeRoot);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(options);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        var controller = CreateController(context, "test-worker-key-that-is-at-least-32-characters", intakeRoot);
        var metadata = JsonSerializer.Serialize(new IngestionJobRequest
        {
            IntakeFileName = "pending.zip", Slug = "never-seen-base", Name = "Never Seen Base",
            WorldDownloadDate = "2021-06-29", Source = "The Archive", Scale = "1", Dimension = "overworld",
            WorldRoot = "world",
        });
        var report = "{\"downloadName\":\"Never_Seen_Base_2021-06-29\",\"sourceAddress\":\"survival.thearchive.world\",\"dimensionName\":\"minecraft:overworld\"}";
        using var archive = BuildArchiveWdlZip(report, "world/level.dat", "world/region/r.100.100.mca");
        var file = new FormFile(archive, 0, archive.Length, "archive", "never-seen.zip")
        {
            Headers = new HeaderDictionary(), ContentType = "application/zip",
        };

        var queued = Assert.IsType<IngestionJobDto>(Assert.IsType<CreatedAtActionResult>(
            (await controller.Upload(metadata, file, cancellationToken)).Result).Value);
        Assert.Equal("queued", queued.Status);
        Assert.Equal("new", queued.MatchDecision);
        Assert.Null(queued.ExistingLocationId);
        Assert.Equal("Never_Seen_Base_2021-06-29", queued.ArchiveWarpName);

        var claim = Assert.IsType<IngestionJobDto>(Assert.IsType<OkObjectResult>((await controller.Claim()).Result).Value);
        context.ChangeTracker.Clear();
        var job = await context.IngestionJobs.SingleAsync(cancellationToken);
        var completion = Completion(job, claim.ClaimToken!);
        completion.ArchiveSha256 = job.ArchiveSha256;
        var completed = Assert.IsType<IngestionJobDto>(Assert.IsType<OkObjectResult>(
            (await controller.UpdateStatus(job.PublicId, completion)).Result).Value);

        var location = await context.Locations.SingleAsync(cancellationToken);
        Assert.Equal(location.Rowid, completed.ExistingLocationId);
        Assert.Equal(location.Rowid, (await context.Renders.SingleAsync(cancellationToken)).LocationRowid);
        var warp = await context.Warps.SingleAsync(cancellationToken);
        Assert.Equal("Never_Seen_Base_2021-06-29", warp.Name);
        Assert.Equal(location.Rowid, warp.LocationRowid);
        Assert.Equal(job.ArchiveSha256, warp.ArchiveSha256);
    }

    [Fact]
    public async Task Completion_persists_trusted_archive_landing_position_and_realigns_matching_existing_location()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(options);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        var location = new _2b2tAtlas.Server.Models.Location
        {
            LocationUuid = Guid.NewGuid().ToString(), Name = "Bedrock City 2", Dimension = 0,
            X = 2_871_075, Y = 65, Z = -1_732_843, DateAddedUtc = DateTime.UtcNow.ToString("o"),
        };
        context.Locations.Add(location);
        await context.SaveChangesAsync(cancellationToken);
        var warp = new _2b2tAtlas.Server.Models.Warp
        {
            WarpUuid = Guid.NewGuid().ToString(), LocationUuidFk = location.LocationUuid,
            LocationRowid = location.Rowid, Name = "Bedrock_City_2_2024-05-31",
            TimeAdded = DateTime.UtcNow.ToString("o"),
        };
        var job = CreateJob();
        job.ExistingLocationId = location.Rowid;
        job.ArchiveWarpName = warp.Name;
        job.ArchiveWarpSource = "operator";
        job.ArchiveWarpX = -3_827_249.03;
        job.ArchiveWarpY = 84.05;
        job.ArchiveWarpZ = -3_438_802.54;
        context.Warps.Add(warp);
        context.IngestionJobs.Add(job);
        await context.SaveChangesAsync(cancellationToken);
        context.ChangeTracker.Clear();

        const string workerKey = "test-worker-key-that-is-at-least-32-characters";
        var controller = CreateController(context, workerKey);
        var claim = Assert.IsType<IngestionJobDto>(
            Assert.IsType<OkObjectResult>((await controller.Claim()).Result).Value);
        var completion = Completion(job, claim.ClaimToken!);
        Assert.IsType<OkObjectResult>((await controller.UpdateStatus(job.PublicId, completion)).Result);

        context.ChangeTracker.Clear();
        var updatedLocation = await context.Locations.SingleAsync(cancellationToken);
        var updatedWarp = await context.Warps.SingleAsync(cancellationToken);
        Assert.Equal(-3_827_249, updatedLocation.X);
        Assert.Equal(84, updatedLocation.Y);
        Assert.Equal(-3_438_803, updatedLocation.Z);
        Assert.Equal(job.ArchiveWarpX, updatedWarp.ArchiveX);
        Assert.Equal(job.ArchiveWarpY, updatedWarp.ArchiveY);
        Assert.Equal(job.ArchiveWarpZ, updatedWarp.ArchiveZ);
    }

    [Fact]
    public async Task Distant_archive_variant_does_not_relocate_an_established_location()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(options);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        var location = new _2b2tAtlas.Server.Models.Location
        {
            LocationUuid = Guid.NewGuid().ToString(), Name = "Space Valkyria III (second site)", Dimension = 2,
            X = 76_933, Y = 64, Z = -112_211, DateAddedUtc = DateTime.UtcNow.ToString("o"),
        };
        context.Locations.Add(location);
        await context.SaveChangesAsync(cancellationToken);
        context.Renders.Add(new _2b2tAtlas.Server.Models.Render
        {
            LocationRowid = location.Rowid, Name = location.Name, Dimension = 2, Scale = "1",
            TilesPath = "https://tiles.atlas.example/AtlasTiles/space-valkyria-iii/end/{z}/{y}/{x}.png",
            IsPublic = 1, DateAddedUtc = DateTime.UtcNow.ToString("o"),
        });
        var job = CreateJob();
        job.Dimension = "end";
        job.ExistingLocationId = location.Rowid;
        job.ArchiveWarpName = "Space_Valkyria_III_rebuild_2025-11-01@End";
        job.ArchiveWarpSource = "operator";
        job.ArchiveWarpX = 273_586.25;
        job.ArchiveWarpY = 111;
        job.ArchiveWarpZ = 1_829_934.9;
        context.IngestionJobs.Add(job);
        await context.SaveChangesAsync(cancellationToken);
        context.ChangeTracker.Clear();

        var controller = CreateController(context, "test-worker-key-that-is-at-least-32-characters");
        var claim = Assert.IsType<IngestionJobDto>(
            Assert.IsType<OkObjectResult>((await controller.Claim()).Result).Value);
        var completion = Completion(job, claim.ClaimToken!);
        completion.LocationRender!.Dimension = 2;
        completion.LocationRender.TilesPath = completion.LocationRender.TilesPath.Replace(
            "/overworld/", "/end/", StringComparison.Ordinal);
        Assert.IsType<OkObjectResult>((await controller.UpdateStatus(job.PublicId, completion)).Result);

        context.ChangeTracker.Clear();
        var updatedLocation = await context.Locations.SingleAsync(cancellationToken);
        var warp = await context.Warps.SingleAsync(cancellationToken);
        Assert.Equal((76_933, 64, -112_211),
            (updatedLocation.X, updatedLocation.Y, updatedLocation.Z));
        Assert.Equal((273_586.25, 111d, 1_829_934.9),
            (warp.ArchiveX, warp.ArchiveY, warp.ArchiveZ));
    }

    [Fact]
    public async Task Different_sky_collection_leaf_is_published_as_its_own_location()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(options);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        var location = new _2b2tAtlas.Server.Models.Location
        {
            LocationUuid = Guid.NewGuid().ToString(), Name = "Sky Masons", Dimension = 0,
            X = 827_065, Y = 160, Z = 439_090, DateAddedUtc = DateTime.UtcNow.ToString("o"),
        };
        context.Locations.Add(location);
        await context.SaveChangesAsync(cancellationToken);
        var canonical = new _2b2tAtlas.Server.Models.Render
        {
            LocationRowid = location.Rowid, Name = "Sky Masons", Dimension = 0, Scale = "1",
            TilesPath = "https://tiles.atlas.example/AtlasTiles/sky-masons-canonical/overworld/{z}/{y}/{x}.png",
            WorldDownloadDate = "2022-10-03", MinX = -1024, MinZ = 2048,
            MaxXExclusive = 512, MaxZExclusive = 4096, IsPublic = 1,
            DateAddedUtc = DateTime.UtcNow.ToString("o"),
        };
        context.Renders.Add(canonical);
        var job = CreateJob();
        job.Slug = "sky-bird-asian-district";
        job.Name = "Bird Asian District";
        job.WorldDownloadDate = "2022-10-03";
        job.Source = "The Archive automated sync";
        job.ExistingLocationId = location.Rowid;
        job.ArchiveWarpName = "Sky:_Bird_Asian_District_2022-10-03@Spawnmasons";
        job.ArchiveWarpSource = "archive-download-report";
        job.ArchiveWarpX = 826_965;
        job.ArchiveWarpY = 191;
        job.ArchiveWarpZ = 438_873;
        context.IngestionJobs.Add(job);
        await context.SaveChangesAsync(cancellationToken);
        context.ChangeTracker.Clear();

        var controller = CreateController(context, "test-worker-key-that-is-at-least-32-characters");
        var claim = Assert.IsType<IngestionJobDto>(
            Assert.IsType<OkObjectResult>((await controller.Claim()).Result).Value);
        var completion = Completion(job, claim.ClaimToken!);
        Assert.IsType<OkObjectResult>((await controller.UpdateStatus(job.PublicId, completion)).Result);

        context.ChangeTracker.Clear();
        var updatedLocation = await context.Locations.SingleAsync(cancellationToken);
        Assert.Equal((826_965, 191, 438_873), (updatedLocation.X, updatedLocation.Y, updatedLocation.Z));
        var warp = await context.Warps.SingleAsync(cancellationToken);
        Assert.Equal((826_965d, 191d, 438_873d), (warp.ArchiveX, warp.ArchiveY, warp.ArchiveZ));
        var renders = await context.Renders.OrderBy(render => render.Id).ToListAsync(cancellationToken);
        Assert.Equal(2, renders.Count);
        Assert.Equal(1, renders[0].IsPublic);
        Assert.Equal(1, renders[1].IsPublic);
        Assert.Null(renders[1].EquivalentToRenderId);
    }

    [Fact]
    public async Task Completion_rechecks_new_coordinate_identity_after_parallel_location_creation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(options);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        var location = new _2b2tAtlas.Server.Models.Location
        {
            LocationUuid = Guid.NewGuid().ToString(), Name = "-X 1M Milestone", Dimension = 0,
            X = -1_000_002, Y = 64, Z = 0, DateAddedUtc = DateTime.UtcNow.ToString("o"),
        };
        context.Locations.Add(location);
        await context.SaveChangesAsync(cancellationToken);
        context.Warps.Add(new _2b2tAtlas.Server.Models.Warp
        {
            WarpUuid = Guid.NewGuid().ToString(), LocationUuidFk = location.LocationUuid,
            LocationRowid = location.Rowid, Name = "x-1.0m_2015-08-29", TimeAdded = DateTime.UtcNow.ToString("o"),
        });
        var job = CreateJob();
        job.Slug = "x-minus-one-million-later";
        job.Name = "x 1 0m";
        job.Source = "The Archive automated sync";
        job.ArchiveWarpName = "x-1.0m_2018-03-02";
        job.ArchiveWarpSource = "archive-download-report";
        job.MatchDecision = "new";
        job.MatchResolved = 1;
        context.IngestionJobs.Add(job);
        await context.SaveChangesAsync(cancellationToken);
        context.ChangeTracker.Clear();

        var controller = CreateController(context, "test-worker-key-that-is-at-least-32-characters");
        var claim = Assert.IsType<IngestionJobDto>(
            Assert.IsType<OkObjectResult>((await controller.Claim()).Result).Value);
        var completion = Completion(job, claim.ClaimToken!);
        var completed = Assert.IsType<IngestionJobDto>(Assert.IsType<OkObjectResult>(
            (await controller.UpdateStatus(job.PublicId, completion)).Result).Value);

        Assert.Equal(location.Rowid, completed.ExistingLocationId);
        Assert.Equal("existing-late", completed.MatchDecision);
        Assert.Equal(1, await context.Locations.CountAsync(cancellationToken));
        Assert.Equal(2, await context.Warps.CountAsync(cancellationToken));
        Assert.Equal(location.Rowid, (await context.Renders.SingleAsync(cancellationToken)).LocationRowid);
    }

    [Fact]
    public async Task Misowned_archive_warp_creates_and_rehomes_separate_numbered_iteration()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var intakeRoot = temporary.Resolve("intake");
        Directory.CreateDirectory(intakeRoot);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(options);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        var sequel = new _2b2tAtlas.Server.Models.Location
        {
            LocationUuid = Guid.NewGuid().ToString(), Name = "Bedrock City 2", Dimension = 0,
            X = 2_871_075, Y = 65, Z = -1_732_843, DateAddedUtc = DateTime.UtcNow.ToString("o"),
        };
        context.Locations.Add(sequel);
        await context.SaveChangesAsync(cancellationToken);
        context.Warps.AddRange(
            new _2b2tAtlas.Server.Models.Warp
            {
                WarpUuid = Guid.NewGuid().ToString(), LocationUuidFk = sequel.LocationUuid,
                LocationRowid = sequel.Rowid, Name = "Bedrock_City_2023-06-15",
                TimeAdded = DateTime.UtcNow.ToString("o"),
            },
            new _2b2tAtlas.Server.Models.Warp
            {
                WarpUuid = Guid.NewGuid().ToString(), LocationUuidFk = sequel.LocationUuid,
                LocationRowid = sequel.Rowid, Name = "Bedrock_City_2_2024-05-31",
                TimeAdded = DateTime.UtcNow.ToString("o"),
            });
        await context.SaveChangesAsync(cancellationToken);
        context.ChangeTracker.Clear();

        const string workerKey = "test-worker-key-that-is-at-least-32-characters";
        var controller = CreateController(context, workerKey, intakeRoot);
        var metadata = JsonSerializer.Serialize(new IngestionJobRequest
        {
            IntakeFileName = "pending.zip", Slug = "bedrock-city", Name = "Bedrock City",
            WorldDownloadDate = "2023-06-15", Source = "The Archive", Scale = "1", Dimension = "overworld",
            WorldRoot = "world", ArchiveWarpX = -3_827_249.03, ArchiveWarpY = 84.05,
            ArchiveWarpZ = -3_438_802.54,
        });
        var report = "{\"downloadName\":\"Bedrock_City_2023-06-15\",\"sourceAddress\":\"survival.thearchive.world\",\"dimensionName\":\"minecraft:overworld\"}";
        using var archive = BuildArchiveWdlZip(report, "world/level.dat", "world/region/r.0.0.mca");
        var file = new FormFile(archive, 0, archive.Length, "archive", "bedrock-city.zip")
        {
            Headers = new HeaderDictionary(), ContentType = "application/zip",
        };

        var queued = Assert.IsType<IngestionJobDto>(Assert.IsType<CreatedAtActionResult>(
            (await controller.Upload(metadata, file, cancellationToken)).Result).Value);
        Assert.Equal("new-rehome", queued.MatchDecision);
        Assert.Null(queued.ExistingLocationId);

        var claim = Assert.IsType<IngestionJobDto>(Assert.IsType<OkObjectResult>((await controller.Claim()).Result).Value);
        context.ChangeTracker.Clear();
        var job = await context.IngestionJobs.SingleAsync(cancellationToken);
        var completion = Completion(job, claim.ClaimToken!);
        completion.ArchiveSha256 = job.ArchiveSha256;
        Assert.IsType<OkObjectResult>((await controller.UpdateStatus(job.PublicId, completion)).Result);

        context.ChangeTracker.Clear();
        var locations = await context.Locations.OrderBy(location => location.Rowid).ToListAsync(cancellationToken);
        Assert.Equal(2, locations.Count);
        var original = Assert.Single(locations, location => location.Name == "Bedrock City");
        var unchangedSequel = Assert.Single(locations, location => location.Name == "Bedrock City 2");
        Assert.Equal((-3_827_249, 84, -3_438_803), (original.X, original.Y, original.Z));
        Assert.Equal((2_871_075, 65, -1_732_843), (unchangedSequel.X, unchangedSequel.Y, unchangedSequel.Z));
        var warps = await context.Warps.OrderBy(warp => warp.Id).ToListAsync(cancellationToken);
        Assert.Equal(original.Rowid, warps.Single(warp => warp.Name == "Bedrock_City_2023-06-15").LocationRowid);
        Assert.Equal(unchangedSequel.Rowid, warps.Single(warp => warp.Name == "Bedrock_City_2_2024-05-31").LocationRowid);
        Assert.Equal(original.Rowid, (await context.Renders.SingleAsync(cancellationToken)).LocationRowid);
    }

    [Fact]
    public async Task Trusted_far_away_archive_warp_is_rehomed_from_a_different_legacy_owner()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var intakeRoot = temporary.Resolve("intake");
        Directory.CreateDirectory(intakeRoot);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(options);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        var wrongOwner = new _2b2tAtlas.Server.Models.Location
        {
            LocationUuid = Guid.NewGuid().ToString(), Name = "Spawnfuer 16", Dimension = 0,
            X = 29_241, Y = 71, Z = 40_819, DateAddedUtc = DateTime.UtcNow.ToString("o"),
        };
        context.Locations.Add(wrongOwner);
        await context.SaveChangesAsync(cancellationToken);
        context.Warps.Add(new _2b2tAtlas.Server.Models.Warp
        {
            WarpUuid = Guid.NewGuid().ToString(), LocationUuidFk = wrongOwner.LocationUuid,
            LocationRowid = wrongOwner.Rowid, Name = "Spawnfuer_s1-b_2022-07-20",
            TimeAdded = DateTime.UtcNow.ToString("o"),
        });
        await context.SaveChangesAsync(cancellationToken);
        context.ChangeTracker.Clear();

        var controller = CreateController(context, "test-worker-key-that-is-at-least-32-characters", intakeRoot);
        var metadata = JsonSerializer.Serialize(new IngestionJobRequest
        {
            IntakeFileName = "pending.zip", Slug = "spawnfuer-s1-b", Name = "Spawnfuer S1-B",
            WorldDownloadDate = "2022-07-20", Source = "The Archive", Scale = "1", Dimension = "overworld",
            WorldRoot = "world", ArchiveWarpX = 276_729.08, ArchiveWarpY = 65,
            ArchiveWarpZ = 117_174.62,
        });
        const string report = "{\"downloadName\":\"Spawnfuer_s1-b_2022-07-20\",\"sourceAddress\":\"survival.thearchive.world\",\"dimensionName\":\"minecraft:overworld\"}";
        using var archive = BuildArchiveWdlZip(report, "world/level.dat", "world/region/r.0.0.mca");
        var file = new FormFile(archive, 0, archive.Length, "archive", "spawnfuer-s1-b.zip")
        {
            Headers = new HeaderDictionary(), ContentType = "application/zip",
        };

        var queued = Assert.IsType<IngestionJobDto>(Assert.IsType<CreatedAtActionResult>(
            (await controller.Upload(metadata, file, cancellationToken)).Result).Value);

        Assert.Equal("new-rehome", queued.MatchDecision);
        Assert.Null(queued.ExistingLocationId);
        Assert.Null(queued.RenderTopY);
    }

    [Fact]
    public async Task Spawnmason_sky_child_is_queued_with_y254_cutaway()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var intakeRoot = temporary.Resolve("intake");
        Directory.CreateDirectory(intakeRoot);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(options);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        var controller = CreateController(context, "test-worker-key-that-is-at-least-32-characters", intakeRoot);
        var metadata = JsonSerializer.Serialize(new IngestionJobRequest
        {
            IntakeFileName = "pending.zip", Slug = "sky-hotel-ukraina", Name = "Hotel Ukraina",
            WorldDownloadDate = "2022-10-03", Source = "The Archive", Scale = "1", Dimension = "overworld",
            WorldRoot = "world", ArchiveWarpX = 826_764, ArchiveWarpY = 191, ArchiveWarpZ = 439_161,
        });
        const string report = "{\"downloadName\":\"Sky:_Hotel_Ukraina_2022-10-03@Spawnmasons\",\"sourceAddress\":\"survival.thearchive.world\",\"dimensionName\":\"minecraft:overworld\"}";
        using var archive = BuildArchiveWdlZip(report, "world/level.dat", "world/region/r.0.0.mca");
        var file = new FormFile(archive, 0, archive.Length, "archive", "sky-hotel-ukraina.zip")
        {
            Headers = new HeaderDictionary(), ContentType = "application/zip",
        };

        var queued = Assert.IsType<IngestionJobDto>(Assert.IsType<CreatedAtActionResult>(
            (await controller.Upload(metadata, file, cancellationToken)).Result).Value);

        Assert.Equal(254, queued.RenderTopY);
    }

    [Fact]
    public async Task New_rehome_completion_converges_after_parallel_job_already_rehomed_exact_warp()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(options);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        var location = new _2b2tAtlas.Server.Models.Location
        {
            LocationUuid = Guid.NewGuid().ToString(), Name = "Novus", Dimension = 0,
            X = 133_383, Y = 70, Z = 25_648, DateAddedUtc = DateTime.UtcNow.ToString("o"),
        };
        context.Locations.Add(location);
        await context.SaveChangesAsync(cancellationToken);
        var warp = new _2b2tAtlas.Server.Models.Warp
        {
            WarpUuid = Guid.NewGuid().ToString(), LocationUuidFk = location.LocationUuid,
            LocationRowid = location.Rowid, Name = "Novus_X_2022-07-06",
            ArchiveSha256 = new string('b', 64), TimeAdded = DateTime.UtcNow.ToString("o"),
        };
        context.Warps.Add(warp);
        await context.SaveChangesAsync(cancellationToken);
        context.Renders.Add(new _2b2tAtlas.Server.Models.Render
        {
            LocationRowid = location.Rowid, Name = "Novus", Dimension = 0, Scale = "1",
            TilesPath = "https://tiles.atlas.example/AtlasTiles/novus-existing/overworld/{z}/{y}/{x}.png",
            ArchiveWarpId = warp.Id, IsPublic = 1, DateAddedUtc = DateTime.UtcNow.ToString("o"),
        });
        var job = CreateJob();
        job.Slug = "novus-x-parallel";
        job.Name = "Novus X";
        job.Source = "The Archive automated sync";
        job.ArchiveWarpName = "Novus_X_2022-07-06";
        job.ArchiveWarpSource = "archive-download-report";
        job.ArchiveSha256 = new string('b', 64);
        job.MatchDecision = "new-rehome";
        job.MatchResolved = 1;
        context.IngestionJobs.Add(job);
        await context.SaveChangesAsync(cancellationToken);
        context.ChangeTracker.Clear();

        var controller = CreateController(context, "test-worker-key-that-is-at-least-32-characters");
        var claim = Assert.IsType<IngestionJobDto>(
            Assert.IsType<OkObjectResult>((await controller.Claim()).Result).Value);
        var completion = Completion(job, claim.ClaimToken!);
        completion.ArchiveSha256 = job.ArchiveSha256;
        var completed = Assert.IsType<IngestionJobDto>(Assert.IsType<OkObjectResult>(
            (await controller.UpdateStatus(job.PublicId, completion)).Result).Value);

        Assert.Equal(location.Rowid, completed.ExistingLocationId);
        Assert.Equal("existing-late", completed.MatchDecision);
        Assert.Equal(1, await context.Locations.CountAsync(cancellationToken));
        Assert.Equal(1, await context.Warps.CountAsync(cancellationToken));
        var renders = await context.Renders.ToListAsync(cancellationToken);
        var updatedRender = Assert.Single(renders);
        Assert.Equal(location.Rowid, updatedRender.LocationRowid);
        Assert.Equal(completion.LocationRender!.TilesPath, updatedRender.TilesPath);
    }

    [Fact]
    public async Task Upload_nether_render_matches_the_overworld_location_via_projection()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var intakeRoot = temporary.Resolve("intake");
        Directory.CreateDirectory(intakeRoot);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var dbOptions = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(dbOptions);
        await context.Database.EnsureCreatedAsync(cancellationToken);

        // Nether region r.0.0 => nether centroid (256, 256) => overworld projection (2048, 2048).
        var overworld = new _2b2tAtlas.Server.Models.Location
        {
            LocationUuid = Guid.NewGuid().ToString(), Name = "Nether Base", Dimension = 0, X = 2048, Y = 64, Z = 2048,
            DateAddedUtc = DateTime.UtcNow.ToString("o"),
        };
        context.Locations.Add(overworld);
        await context.SaveChangesAsync(cancellationToken);
        context.ChangeTracker.Clear();

        const string workerKey = "test-worker-key-that-is-at-least-32-characters";
        var controller = CreateController(context, workerKey, intakeRoot);
        var metadata = JsonSerializer.Serialize(new IngestionJobRequest
        {
            IntakeFileName = "pending.zip", Slug = "nether-base", Name = "Nether Base",
            WorldDownloadDate = "2021-06-29", Source = "2b2t world download", Scale = "256k", Dimension = "auto",
        });
        using var archive = BuildWorldZip("world/level.dat", "world/DIM-1/region/r.0.0.mca");
        var file = new FormFile(archive, 0, archive.Length, "archive", "nether-base.zip")
        {
            Headers = new HeaderDictionary(), ContentType = "application/zip",
        };

        Assert.IsType<CreatedAtActionResult>((await controller.Upload(metadata, file, cancellationToken)).Result);
        var job = await context.IngestionJobs.SingleAsync(cancellationToken);
        Assert.Equal("nether", job.Dimension);
        // Nether render attaches to the overworld location (no separate nether locations exist).
        Assert.Equal(overworld.Rowid, job.ExistingLocationId);
    }

    [Fact]
    public async Task Upload_auto_rejects_custom_dimension_instead_of_silently_dropping_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var intakeRoot = temporary.Resolve("intake");
        Directory.CreateDirectory(intakeRoot);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(options);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        var controller = CreateController(context, "test-worker-key-that-is-at-least-32-characters", intakeRoot);
        var metadata = JsonSerializer.Serialize(new IngestionJobRequest
        {
            IntakeFileName = "pending.zip", Slug = "museum-base", Name = "Museum Base",
            WorldDownloadDate = "2017-03-06", Source = "The Archive", Scale = "1", Dimension = "auto",
        });
        using var archive = BuildWorldZip(
            "world/level.dat",
            "world/dimensions/thearchive/museum_2017/region/r.0.0.mca");
        var file = new FormFile(archive, 0, archive.Length, "archive", "museum-base.zip")
        {
            Headers = new HeaderDictionary(), ContentType = "application/zip",
        };

        var result = Assert.IsType<BadRequestObjectResult>(
            (await controller.Upload(metadata, file, cancellationToken)).Result);

        Assert.Contains("Choose its original 2b2t dimension", JsonSerializer.Serialize(result.Value));
        Assert.Empty(context.IngestionJobs);
    }

    [Fact]
    public async Task Upload_auto_accepts_one_custom_dimension_with_explicit_nether_identity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var intakeRoot = temporary.Resolve("intake");
        Directory.CreateDirectory(intakeRoot);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(options);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        var controller = CreateController(context, "test-worker-key-that-is-at-least-32-characters", intakeRoot);
        var metadata = JsonSerializer.Serialize(new IngestionJobRequest
        {
            IntakeFileName = "pending.zip", Slug = "custom-nether-base", Name = "Custom Nether Base",
            WorldDownloadDate = "2019-08-04", Source = "The Archive", Scale = "1", Dimension = "auto",
        });
        using var archive = BuildWorldZip(
            "world/level.dat",
            "world/dimensions/thearchive/the_nether/region/r.2.-3.mca");
        var file = new FormFile(archive, 0, archive.Length, "archive", "custom-nether-base.zip")
        {
            Headers = new HeaderDictionary(), ContentType = "application/zip",
        };

        Assert.IsType<CreatedAtActionResult>((await controller.Upload(metadata, file, cancellationToken)).Result);

        var job = await context.IngestionJobs.SingleAsync(cancellationToken);
        Assert.Equal("nether", job.Dimension);
    }

    [Fact]
    public async Task Upload_auto_rejects_custom_and_canonical_storage_claiming_the_same_dimension()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var intakeRoot = temporary.Resolve("intake");
        Directory.CreateDirectory(intakeRoot);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(options);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        var controller = CreateController(context, "test-worker-key-that-is-at-least-32-characters", intakeRoot);
        var metadata = JsonSerializer.Serialize(new IngestionJobRequest
        {
            IntakeFileName = "pending.zip", Slug = "ambiguous-nether-roots", Name = "Ambiguous Nether Roots",
            WorldDownloadDate = "2019-08-04", Source = "The Archive", Scale = "1", Dimension = "auto",
        });
        using var archive = BuildWorldZip(
            "world/level.dat",
            "world/DIM-1/region/r.0.0.mca",
            "world/dimensions/thearchive/the_nether/region/r.2.-3.mca");
        var file = new FormFile(archive, 0, archive.Length, "archive", "ambiguous-nether-roots.zip")
        {
            Headers = new HeaderDictionary(), ContentType = "application/zip",
        };

        Assert.IsType<BadRequestObjectResult>((await controller.Upload(metadata, file, cancellationToken)).Result);
        Assert.Empty(context.IngestionJobs);
    }

    [Fact]
    public async Task Upload_auto_rejects_multiple_custom_roots_even_when_their_names_look_familiar()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var intakeRoot = temporary.Resolve("intake");
        Directory.CreateDirectory(intakeRoot);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(options);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        var controller = CreateController(context, "test-worker-key-that-is-at-least-32-characters", intakeRoot);
        var metadata = JsonSerializer.Serialize(new IngestionJobRequest
        {
            IntakeFileName = "pending.zip", Slug = "custom-multi", Name = "Custom Multi",
            WorldDownloadDate = "2019-08-04", Source = "The Archive", Scale = "1", Dimension = "auto",
        });
        using var archive = BuildWorldZip(
            "world/level.dat",
            "world/dimensions/thearchive/the_nether/region/r.2.-3.mca",
            "world/dimensions/thearchive/the_end/region/r.4.-5.mca");
        var file = new FormFile(archive, 0, archive.Length, "archive", "custom-multi.zip")
        {
            Headers = new HeaderDictionary(), ContentType = "application/zip",
        };

        Assert.IsType<BadRequestObjectResult>((await controller.Upload(metadata, file, cancellationToken)).Result);
        Assert.Empty(context.IngestionJobs);
    }

    [Fact]
    public async Task Upload_explicit_dimension_queues_single_custom_dimension_for_worker_normalization()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var intakeRoot = temporary.Resolve("intake");
        Directory.CreateDirectory(intakeRoot);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(options);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        var controller = CreateController(context, "test-worker-key-that-is-at-least-32-characters", intakeRoot);
        var metadata = JsonSerializer.Serialize(new IngestionJobRequest
        {
            IntakeFileName = "pending.zip", Slug = "museum-end-base", Name = "Museum End Base",
            WorldDownloadDate = "2017-03-06", Source = "The Archive", Scale = "1", Dimension = "end",
        });
        using var archive = BuildWorldZip(
            "world/level.dat",
            "world/dimensions/thearchive/museum_2017/region/r.0.0.mca");
        var file = new FormFile(archive, 0, archive.Length, "archive", "museum-end-base.zip")
        {
            Headers = new HeaderDictionary(), ContentType = "application/zip",
        };

        Assert.IsType<CreatedAtActionResult>((await controller.Upload(metadata, file, cancellationToken)).Result);

        var job = await context.IngestionJobs.SingleAsync(cancellationToken);
        Assert.Equal("end", job.Dimension);
    }

    [Fact]
    public async Task CreateLocal_allows_reusing_a_cancelled_slug()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var dbOptions = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(dbOptions);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        const string workerKey = "test-worker-key-that-is-at-least-32-characters";
        var controller = CreateController(context, workerKey);

        IngestionJobRequest Request() => new()
        {
            IntakeFileName = "wb.zip", Slug = "shared-base", Name = "Shared Base",
            WorldDownloadDate = "2021-06-29", Source = "2b2t world download", Scale = "256k", Dimension = "overworld",
        };

        Assert.IsType<CreatedAtActionResult>((await controller.CreateLocal(Request())).Result);
        // A live job still reserves the slug for this dimension.
        Assert.IsType<ConflictObjectResult>((await controller.CreateLocal(Request())).Result);

        var job = await context.IngestionJobs.SingleAsync(cancellationToken);
        job.Status = "cancelled";
        await context.SaveChangesAsync(cancellationToken);
        context.ChangeTracker.Clear();

        // After cancellation the slug is free to re-upload.
        Assert.IsType<CreatedAtActionResult>((await controller.CreateLocal(Request())).Result);
        Assert.Equal(2, await context.IngestionJobs.CountAsync(cancellationToken));
    }

    [Fact]
    public async Task QueueLocalIntake_archives_inspects_and_queues_an_atomic_local_zip()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var intakeRoot = temporary.Resolve("intake");
        Directory.CreateDirectory(intakeRoot);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(options);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        const string workerKey = "test-worker-key-that-is-at-least-32-characters";
        var controller = CreateController(context, workerKey, intakeRoot);
        const string fileName = "archive-ready.zip";
        await using (var destination = File.Create(Path.Combine(intakeRoot, fileName)))
        using (var archive = BuildWorldZip("world/level.dat", "world/region/r.4.-5.mca"))
            await archive.CopyToAsync(destination, cancellationToken);

        var request = new LocalIntakeQueueRequest
        {
            OriginalFileName = "archive-ready-original.zip",
            Metadata = new IngestionJobRequest
            {
                IntakeFileName = fileName, Slug = "archive-ready", Name = "Archive Ready",
                WorldDownloadDate = "2020-01-02", Source = "The Archive automated sync", Scale = "1", Dimension = "auto",
            },
        };

        Assert.IsType<CreatedAtActionResult>(
            (await controller.QueueLocalIntake(request, cancellationToken)).Result);
        var job = await context.IngestionJobs.SingleAsync(cancellationToken);
        Assert.Equal("overworld", job.Dimension);
        Assert.Matches("^[0-9a-f]{64}$", job.ArchiveSha256);
        Assert.Equal("archive-ready-original", job.ArchiveWarpName);
    }

    [Fact]
    public async Task QueueLocalIntake_exact_archive_retry_returns_the_existing_job()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var intakeRoot = temporary.Resolve("intake");
        Directory.CreateDirectory(intakeRoot);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(options);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        const string workerKey = "test-worker-key-that-is-at-least-32-characters";
        var controller = CreateController(context, workerKey, intakeRoot);
        const string fileName = "archive-idempotent.zip";
        await using (var destination = File.Create(Path.Combine(intakeRoot, fileName)))
        using (var archive = BuildWorldZip("world/level.dat", "world/region/r.4.-5.mca"))
            await archive.CopyToAsync(destination, cancellationToken);

        var request = new LocalIntakeQueueRequest
        {
            OriginalFileName = "archive-idempotent-original.zip",
            Metadata = new IngestionJobRequest
            {
                IntakeFileName = fileName, Slug = "archive-idempotent", Name = "Archive Idempotent",
                WorldDownloadDate = "2020-01-02", Source = "The Archive automated sync", Scale = "1", Dimension = "auto",
            },
        };

        var first = Assert.IsType<IngestionJobDto>(Assert.IsType<CreatedAtActionResult>(
            (await controller.QueueLocalIntake(request, cancellationToken)).Result).Value);
        context.ChangeTracker.Clear();
        var retry = Assert.IsType<IngestionJobDto>(Assert.IsType<CreatedAtActionResult>(
            (await controller.QueueLocalIntake(request, cancellationToken)).Result).Value);

        Assert.Equal(first.Id, retry.Id);
        Assert.Single(await context.IngestionJobs.ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task QueueLocalIntake_rejects_paths_outside_the_configured_intake_root()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var intakeRoot = temporary.Resolve("intake");
        Directory.CreateDirectory(intakeRoot);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(options);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        var controller = CreateController(context, "test-worker-key-that-is-at-least-32-characters", intakeRoot);
        var request = new LocalIntakeQueueRequest
        {
            OriginalFileName = "outside.zip",
            Metadata = new IngestionJobRequest
            {
                IntakeFileName = "..\\outside.zip", Slug = "outside", Name = "Outside",
                WorldDownloadDate = "2020-01-02", Source = "The Archive automated sync", Scale = "1", Dimension = "auto",
            },
        };

        Assert.IsType<BadRequestObjectResult>(
            (await controller.QueueLocalIntake(request, cancellationToken)).Result);
        Assert.Empty(context.IngestionJobs);
    }

    [Fact]
    public async Task Upload_confirmed_location_attaches_to_overworld_and_nether_jobs()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var intakeRoot = temporary.Resolve("intake");
        Directory.CreateDirectory(intakeRoot);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var dbOptions = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(dbOptions);
        await context.Database.EnsureCreatedAsync(cancellationToken);

        var overworld = new _2b2tAtlas.Server.Models.Location
        {
            LocationUuid = Guid.NewGuid().ToString(), Name = "+Z Border", Dimension = 0, X = 0, Y = 64, Z = 30_000_000,
            DateAddedUtc = DateTime.UtcNow.ToString("o"),
        };
        context.Locations.Add(overworld);
        await context.SaveChangesAsync(cancellationToken);
        context.ChangeTracker.Clear();

        const string workerKey = "test-worker-key-that-is-at-least-32-characters";
        var controller = CreateController(context, workerKey, intakeRoot);
        var metadata = JsonSerializer.Serialize(new IngestionJobRequest
        {
            IntakeFileName = "pending.zip", Slug = "z-border", Name = "+Z Border",
            WorldDownloadDate = "2019-04-20", Source = "2b2t world download", Scale = "256k",
            Dimension = "auto", ExistingLocationId = overworld.Rowid,
        });
        using var archive = BuildWorldZip("world/level.dat", "world/region/r.0.0.mca", "world/DIM-1/region/r.0.0.mca");
        var file = new FormFile(archive, 0, archive.Length, "archive", "z-border.zip")
        {
            Headers = new HeaderDictionary(), ContentType = "application/zip",
        };

        Assert.IsType<CreatedAtActionResult>((await controller.Upload(metadata, file, cancellationToken)).Result);
        var jobs = await context.IngestionJobs.OrderBy(job => job.Dimension).ToListAsync(cancellationToken);
        Assert.Equal(new[] { "nether", "overworld" }, jobs.Select(job => job.Dimension).ToArray());
        // The confirmed overworld location attaches to BOTH the overworld and nether jobs.
        Assert.All(jobs, job => Assert.Equal(overworld.Rowid, job.ExistingLocationId));
        Assert.All(jobs, job => Assert.Equal("queued", job.Status));
    }

    [Fact]
    public async Task Upload_multi_dimension_archive_assigns_its_single_warp_only_to_the_primary_location_family()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var intakeRoot = temporary.Resolve("intake");
        Directory.CreateDirectory(intakeRoot);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(options);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        var controller = CreateController(
            context, "test-worker-key-that-is-at-least-32-characters", intakeRoot);
        var metadata = JsonSerializer.Serialize(new IngestionJobRequest
        {
            IntakeFileName = "pending.zip", Slug = "z-border-bundle", Name = "+Z Border",
            WorldDownloadDate = "2019-04-20", Source = "The Archive", Scale = "1", Dimension = "auto",
        });
        using var archive = BuildWorldZip(
            "world/level.dat",
            "world/region/r.0.0.mca",
            "world/region/r.0.1.mca",
            "world/DIM-1/region/r.0.0.mca",
            "world/DIM1/region/r.0.0.mca");
        var file = new FormFile(archive, 0, archive.Length, "archive", "z-border.zip")
        {
            Headers = new HeaderDictionary(), ContentType = "application/zip",
        };

        Assert.IsType<CreatedAtActionResult>((await controller.Upload(metadata, file, cancellationToken)).Result);
        var jobs = await context.IngestionJobs.OrderBy(job => job.Dimension).ToListAsync(cancellationToken);

        Assert.Equal("z-border", jobs.Single(job => job.Dimension == "overworld").ArchiveWarpName);
        Assert.Equal("z-border", jobs.Single(job => job.Dimension == "nether").ArchiveWarpName);
        Assert.Null(jobs.Single(job => job.Dimension == "end").ArchiveWarpName);
    }

    private static MemoryStream BuildWorldZip(params string[] entryPaths)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var path in entryPaths)
            {
                var entry = zip.CreateEntry(path);
                using var writer = entry.Open();
                writer.Write(new byte[64], 0, 64);
            }
        }
        stream.Position = 0;
        return stream;
    }

    private static MemoryStream BuildArchiveWdlZip(string report, params string[] entryPaths)
    {
        var stream = BuildWorldZip(entryPaths);
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = zip.CreateEntry("world/wdl/download.jsonl");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(report);
        }
        stream.Position = 0;
        return stream;
    }

    private static IngestionJobsController CreateController(AtlasContext context, string workerKey, string? intakeRoot = null, EnrichmentQueue? enrichmentQueue = null)
    {
        var keyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(workerKey)));
        var settings = new Dictionary<string, string?>
        {
            ["IngestionWorker:ApiKeySha256"] = keyHash,
            ["MapRenders:AllowedUrlPrefixes:0"] = "https://tiles.atlas.example/AtlasTiles/",
        };
        if (intakeRoot is not null) settings["IngestionWorker:IntakeRoot"] = intakeRoot;
        if (intakeRoot is not null)
        {
            var archiveRoot = Path.Combine(Path.GetDirectoryName(intakeRoot)!, "archive");
            Directory.CreateDirectory(archiveRoot);
            settings["WdlArchive:Root"] = archiveRoot;
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var controller = new IngestionJobsController(context, new AuditService(context), enrichmentQueue ?? new EnrichmentQueue(), configuration)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        controller.Request.Headers["X-Atlas-Worker-Key"] = workerKey;
        return controller;
    }

    [Theory]
    [InlineData("Space Base", "SBA_44:_Space_Base_2022-12-04@End", "Spawn Builders Association")]
    [InlineData("Yellow Brick PATH", "SBA_48:_Yellow_Brick_PATH_2023-02-09", "Spawn Builders Association")]
    [InlineData("Underground", "l18w08_Underground_2018-02-24@Spawnmason_lodge", "SpawnMasons")]
    [InlineData("Mothra Cube", "l21w11_Mothra_Cube_2021-03-13@1.16_test_server@Spawnmason_lodge", "SpawnMasons")]
    [InlineData("Future base", "Future_base@Nerds_Inc.@End", "Nerds Inc")]
    public async Task Completion_assigns_explicit_groups_before_any_AI_worker_runs(string name, string warpName, string groupName)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        await using var context = new AtlasContext(new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync(ct);
        context.Groups.Add(new _2b2tAtlas.Server.Models.Group { Name = groupName, Type = "Build" });
        var job = CreateJob();
        job.Name = name;
        job.Source = "The Archive automated sync";
        job.ArchiveWarpName = warpName;
        job.ArchiveWarpSource = "archive-download-report";
        job.ArchiveWarpX = 123;
        job.ArchiveWarpY = 64;
        job.ArchiveWarpZ = -456;
        context.IngestionJobs.Add(job);
        await context.SaveChangesAsync(ct);
        context.ChangeTracker.Clear();
        var controller = CreateController(context, "test-worker-key-that-is-at-least-32-characters");
        var claim = Assert.IsType<IngestionJobDto>(Assert.IsType<OkObjectResult>((await controller.Claim()).Result).Value);
        var completion = Completion(job, claim.ClaimToken!);
        Assert.IsType<OkObjectResult>((await controller.UpdateStatus(job.PublicId, completion)).Result);
        context.ChangeTracker.Clear();
        var link = await context.LocationGroups.Include(link => link.Group).SingleAsync(ct);
        Assert.Equal(groupName, link.Group.Name);
        Assert.Equal("Builder", link.Role);
        Assert.Equal((await context.Locations.SingleAsync(ct)).Rowid, link.LocationRowid);
        Assert.Contains(warpName, (await context.AuditLogs.SingleAsync(audit => audit.Action == "location.group.archive", ct)).DetailsJson);
        Assert.IsType<OkObjectResult>((await controller.UpdateStatus(job.PublicId, completion)).Result);
        Assert.Equal(1, await context.LocationGroups.CountAsync(ct));
        Assert.Equal(1, await context.AuditLogs.CountAsync(audit => audit.Action == "location.group.archive", ct));
    }

    private static IngestionJob CreateJob() => new()
    {
        PublicId = Guid.NewGuid().ToString("N"),
        IntakeFileName = "TGG_wdl.zip",
        Slug = "tgg-controller-test",
        Name = "TGG Controller Test",
        WorldDownloadDate = "2021-06-29",
        Source = "TGG world download",
        Scale = "256k",
        Status = "queued",
        MatchResolved = 1,
    };

    private static IngestionJobUpdate Completion(IngestionJob job, string claimToken) => new()
    {
        ClaimToken = claimToken,
        Status = "completed",
        Stage = "registered",
        ArchiveSha256 = new string('a', 64),
        LocationRender = CreateRender(job),
    };

    private static LocationRenderCompletion CreateRender(IngestionJob job) => new()
    {
        TilesPath = $"https://tiles.atlas.example/AtlasTiles/{job.Slug}/overworld/{{z}}/{{y}}/{{x}}.png",
        Dimension = 0,
        MinX = -1024,
        MinZ = 2048,
        MaxXExclusive = 512,
        MaxZExclusive = 4096,
        MaxNativeZoom = 9,
        CoordinateScheme = "atlas-sparse-v1",
    };

    private static IngestionWorldInspection CreateInspection() => new()
    {
        LevelName = "2b2t_org",
        DataVersion = 1343,
        VersionName = "1.12.2",
        StorageEra = "anvil",
        ProvenanceStatus = "unverified",
        ProvenanceMessage = "Structurally valid Java world; 2b2t origin requires source and overlap review.",
        Dimensions =
        [
            new IngestionDimensionInspection
            {
                Key = "overworld",
                StorageEra = "anvil",
                StorageFileCount = 43,
                ChunkCount = 23754,
                NativeTileCount = 116,
                MinX = -192,
                MinZ = -3248,
                MaxXExclusive = 7488,
                MaxZExclusive = 208,
            },
        ],
    };
}
