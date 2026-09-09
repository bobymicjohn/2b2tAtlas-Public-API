using Atlas.Ingestor.Configuration;
using Atlas.Ingestor.Models;
using Atlas.Ingestor.Pipeline;
using Atlas.Ingestor.Publishing;
using Atlas.Ingestor.Rendering;
using Atlas.Ingestor.Security;

namespace Atlas.Ingestor.Tests;

public sealed class TilePublisherTests
{
    [Fact]
    public async Task Copy_publication_verifies_bytes_and_preserves_scratch_source()
    {
        using var temporary = new TempDirectory();
        var source = temporary.Resolve("scratch");
        WriteTile(Path.Combine(source, "0", "0", "0.png"));
        var report = TilePublisher.Verify(source, new IngestLimits());
        var destination = temporary.Resolve("served");
        await PublicationService.CopyVerifiedAtomicallyAsync(source, destination, report, new IngestLimits(), CancellationToken.None);
        Assert.True(Directory.Exists(source));
        Assert.True(TilePublisher.ReportsMatch(report, TilePublisher.Verify(destination, new IngestLimits())));
        await Assert.ThrowsAsync<InputValidationException>(() =>
            PublicationService.CopyVerifiedAtomicallyAsync(source, destination, report, new IngestLimits(), CancellationToken.None));
    }

    [Fact]
    public async Task Copy_publication_rejects_changed_bytes_and_cleans_only_partial_output()
    {
        using var temporary = new TempDirectory();
        var source = temporary.Resolve("scratch");
        var tile = Path.Combine(source, "0", "0", "0.png");
        WriteTile(tile);
        var report = TilePublisher.Verify(source, new IngestLimits());
        await File.WriteAllBytesAsync(tile, [9], TestContext.Current.CancellationToken);
        var destination = temporary.Resolve("served");
        await Assert.ThrowsAsync<InputSecurityException>(() =>
            PublicationService.CopyVerifiedAtomicallyAsync(source, destination, report, new IngestLimits(), CancellationToken.None));
        Assert.False(Directory.Exists(destination));
        Assert.True(File.Exists(tile));
        Assert.Empty(Directory.EnumerateDirectories(temporary.Path, "*.partial"));
    }

    [Fact]
    public async Task Cancelled_copy_does_not_publish_or_remove_scratch()
    {
        using var temporary = new TempDirectory();
        var source = temporary.Resolve("scratch");
        WriteTile(Path.Combine(source, "0", "0", "0.png"));
        var report = TilePublisher.Verify(source, new IngestLimits());
        var destination = temporary.Resolve("served");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PublicationService.CopyVerifiedAtomicallyAsync(source, destination, report, new IngestLimits(), new CancellationToken(true)));
        Assert.False(Directory.Exists(destination));
        Assert.True(Directory.Exists(source));
        Assert.Empty(Directory.EnumerateDirectories(temporary.Path, "*.partial"));
    }

    [Fact]
    public void Verify_rejects_prefixed_tile_subtrees()
    {
        using var temporary = new TempDirectory();
        WriteTile(temporary.Resolve("tiles", "extra", "0", "0", "0.png"));

        Assert.Throws<InputValidationException>(() =>
            TilePublisher.Verify(temporary.Resolve("tiles"), new IngestLimits()));
    }

    [Fact]
    public void Verify_rejects_mixed_image_extensions()
    {
        using var temporary = new TempDirectory();
        WriteTile(temporary.Resolve("tiles", "0", "0", "0.png"));
        WriteTile(temporary.Resolve("tiles", "1", "0", "0.webp"));

        Assert.Throws<InputValidationException>(() =>
            TilePublisher.Verify(temporary.Resolve("tiles"), new IngestLimits()));
    }

    [Fact]
    public void Verify_accepts_xyz_tiles_and_publish_is_atomic()
    {
        using var temporary = new TempDirectory();
        var staging = temporary.Resolve("staging");
        Directory.CreateDirectory(System.IO.Path.Combine(staging, "0", "0"));
        File.WriteAllBytes(System.IO.Path.Combine(staging, "0", "0", "0.png"), [1, 2]);
        var destination = temporary.Resolve("published", "immutable-id");

        var report = TilePublisher.Verify(staging, new IngestLimits());
        TilePublisher.PublishAtomically(staging, destination);

        Assert.Equal(1, report.TileCount);
        Assert.False(Directory.Exists(staging));
        Assert.True(File.Exists(System.IO.Path.Combine(destination, "0", "0", "0.png")));
    }

    [Fact]
    public void Verify_rejects_out_of_bounds_xyz_tile()
    {
        using var temporary = new TempDirectory();
        Directory.CreateDirectory(System.IO.Path.Combine(temporary.Path, "2", "4"));
        File.WriteAllBytes(System.IO.Path.Combine(temporary.Path, "2", "4", "0.png"), [1]);

        Assert.Throws<InputValidationException>(() => TilePublisher.Verify(temporary.Path, new IngestLimits()));
    }

    [Fact]
    public void Verify_rejects_non_tile_files_in_publication_root()
    {
        using var temporary = new TempDirectory();
        Directory.CreateDirectory(System.IO.Path.Combine(temporary.Path, "0", "0"));
        File.WriteAllBytes(System.IO.Path.Combine(temporary.Path, "0", "0", "0.png"), [1]);
        File.WriteAllText(System.IO.Path.Combine(temporary.Path, "unmined.index.html"), "<html></html>");

        Assert.Throws<InputValidationException>(() => TilePublisher.Verify(temporary.Path, new IngestLimits()));
    }

    [Fact]
    public async Task Publication_receipt_binds_job_plan_dimension_and_inventory()
    {
        using var temporary = new TempDirectory();
        const string jobId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var paths = JobStore.BuildPaths(temporary.Resolve("work", jobId));
        Directory.CreateDirectory(System.IO.Path.Combine(paths.Render, "overworld", "atlas-tiles", "0", "0"));
        await File.WriteAllBytesAsync(
            System.IO.Path.Combine(paths.Render, "overworld", "atlas-tiles", "0", "0", "0.png"),
            [1, 2, 3],
            TestContext.Current.CancellationToken);
        var dimension = new DimensionInfo("overworld", 0, ".", 1, "anvil");
        var world = new WorldInfo(temporary.Resolve("world"), "Test", 3955, "1.21.1", "anvil", [dimension]);
        var plan = new RenderPlan("test", "Test", new DateOnly(2026, 1, 1), "fixture", world, [dimension], false, "5k", false);
        await JobStore.WriteJsonAsync(paths.RenderPlan, plan, TestContext.Current.CancellationToken);
        await JobStore.WriteJsonAsync(
            paths.RenderProvenance("overworld"),
            new RenderProvenance(
                await JobStore.ComputeSha256Async(paths.RenderPlan, TestContext.Current.CancellationToken),
                "overworld",
                world.RootPath,
                "test",
                new string('a', 64),
                ["test"],
                "completed",
                DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);
        var adaptedReport = TilePublisher.Verify(
            System.IO.Path.Combine(paths.Render, "overworld", "atlas-tiles"), new IngestLimits());
        await JobStore.WriteJsonAsync(
            paths.AdaptationReceipt("overworld"),
            new AdaptationReceipt(
                jobId,
                await JobStore.ComputeSha256Async(paths.RenderPlan, TestContext.Current.CancellationToken),
                await JobStore.ComputeSha256Async(paths.RenderProvenance("overworld"), TestContext.Current.CancellationToken),
                "overworld",
                "atlas-overworld-256k-v1",
                "atlas-tiles",
                adaptedReport,
                DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);
        // Optional host canary exercises the real D -> F boundary without any
        // production API writes or public tile paths.
        var canaryRoot = Environment.GetEnvironmentVariable("ATLAS_TEST_PUBLISH_ROOT");
        var destination = string.IsNullOrWhiteSpace(canaryRoot)
            ? temporary.Resolve("published", "test", "overworld")
            : Path.Combine(canaryRoot, Guid.NewGuid().ToString("N"), "overworld");

        var receipt = await PublicationService.PublishAsync(
            paths, jobId, "overworld", "atlas-tiles", destination, new IngestLimits(), TestContext.Current.CancellationToken);
        var verified = await PublicationService.ReadAndVerifyAsync(
            paths, jobId, "overworld", new IngestLimits(), TestContext.Current.CancellationToken);

        Assert.Equal(receipt.JobId, verified.JobId);
        Assert.Equal(receipt.PlanSha256, verified.PlanSha256);
        Assert.Equal(receipt.Dimension, verified.Dimension);
        Assert.Equal(receipt.Destination, verified.Destination);
        Assert.Equal(receipt.Report.TileCount, verified.Report.TileCount);
        Assert.Equal(receipt.Report.TilesPerZoom, verified.Report.TilesPerZoom);
        Assert.Equal(0, receipt.Report.MaxZoom);
        Assert.True(File.Exists(paths.PublicationReceipt("overworld")));

        await File.WriteAllBytesAsync(
            System.IO.Path.Combine(destination, "0", "0", "0.png"),
            [3, 2, 1],
            TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InputSecurityException>(() => PublicationService.ReadAndVerifyAsync(
            paths, jobId, "overworld", new IngestLimits(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Verify_enforces_output_entry_limit()
    {
        using var temporary = new TempDirectory();
        WriteTile(temporary.Resolve("tiles", "0", "0", "0.png"));

        Assert.Throws<InputSecurityException>(() => TilePublisher.Verify(
            temporary.Resolve("tiles"),
            new IngestLimits { MaxOutputEntries = 2 }));
    }

    private static void WriteTile(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1]);
    }
}
