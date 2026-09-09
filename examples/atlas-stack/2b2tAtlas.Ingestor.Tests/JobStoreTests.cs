using Atlas.Ingestor.Configuration;
using Atlas.Ingestor.Pipeline;
using Atlas.Ingestor.Security;

namespace Atlas.Ingestor.Tests;

public sealed class JobStoreTests
{
    [Fact]
    public void State_policy_enforces_render_and_publish_order()
    {
        JobStatePolicy.RequireRender(new JobState("prepared", DateTimeOffset.UtcNow, null), false);
        JobStatePolicy.RequireRender(new JobState("failed", DateTimeOffset.UtcNow, null, "render"), true);
        JobStatePolicy.RequireAdapt(new JobState("rendered", DateTimeOffset.UtcNow, null));
        JobStatePolicy.RequireAdapt(new JobState("failed", DateTimeOffset.UtcNow, null, "adapt"));
        JobStatePolicy.RequirePublish(new JobState("adapted", DateTimeOffset.UtcNow, null));
        JobStatePolicy.RequirePublish(new JobState("registered-unpublished", DateTimeOffset.UtcNow, null));

        Assert.Throws<InputValidationException>(() =>
            JobStatePolicy.RequireRender(new JobState("failed", DateTimeOffset.UtcNow, null, "prepare"), true));
        Assert.Throws<InputValidationException>(() =>
            JobStatePolicy.RequirePublish(new JobState("prepared", DateTimeOffset.UtcNow, null)));
        Assert.Throws<InputValidationException>(() =>
            JobStatePolicy.RequirePublish(new JobState("rendered", DateTimeOffset.UtcNow, null)));
    }

    [Fact]
    public async Task Prepare_resume_revalidates_snapshot_and_preserves_prior_artifacts()
    {
        using var temporary = new TempDirectory();
        var archive = ZipFixture.Create(temporary, ("world/level.dat", ZipFixture.MinimalLevelDat()));
        var store = new JobStore(temporary.Resolve("jobs"), new IngestLimits());
        var (paths, _) = await store.CreateAsync(archive, TestContext.Current.CancellationToken);
        Directory.CreateDirectory(paths.Extracted);
        await File.WriteAllTextAsync(
            Path.Combine(paths.Extracted, "partial.txt"), "evidence", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(paths.RenderPlan, "old plan", TestContext.Current.CancellationToken);
        await JobStore.WriteJsonAsync(
            paths.State,
            new JobState("failed", DateTimeOffset.UtcNow, "test", "prepare"),
            TestContext.Current.CancellationToken);

        var (resumedPaths, report) = await store.ResumePrepareAsync(archive, TestContext.Current.CancellationToken);

        Assert.Equal(paths.Root, resumedPaths.Root);
        Assert.Equal(paths.Snapshot, report.ArchivePath);
        Assert.False(Directory.Exists(paths.Extracted));
        Assert.False(File.Exists(paths.RenderPlan));
        var attempt = Assert.Single(Directory.GetDirectories(Path.Combine(paths.Root, "attempts")));
        Assert.Equal("evidence", await File.ReadAllTextAsync(
            Path.Combine(attempt, "worlds", "partial.txt"), TestContext.Current.CancellationToken));
        Assert.Equal("old plan", await File.ReadAllTextAsync(
            Path.Combine(attempt, "render-plan.json"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Prepare_resume_rejects_a_render_failure()
    {
        using var temporary = new TempDirectory();
        var archive = ZipFixture.Create(temporary, ("level.dat", ZipFixture.MinimalLevelDat()));
        var store = new JobStore(temporary.Resolve("jobs"), new IngestLimits());
        var (paths, _) = await store.CreateAsync(archive, TestContext.Current.CancellationToken);
        await JobStore.WriteJsonAsync(
            paths.State,
            new JobState("failed", DateTimeOffset.UtcNow, "test", "render"),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InputValidationException>(() =>
            store.ResumePrepareAsync(archive, TestContext.Current.CancellationToken));
    }
}