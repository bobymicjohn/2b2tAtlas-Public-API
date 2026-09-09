using System.Text.Json;
using System.Reflection;
using Atlas.Ingestor.Security;
using Atlas.Ingestor.Worker;

namespace Atlas.Ingestor.Tests;

public sealed class WorkerOptionsTests
{
    [Fact]
    public async Task Load_accepts_separate_same_volume_roots()
    {
        using var temporary = new TempDirectory();
        var config = await WriteConfigAsync(temporary);

        var options = await WorkerOptions.LoadAsync(config, CancellationToken.None);

        Assert.Equal("atlas-overworld-sparse-v1", options.TileScheme);
    }

    [Fact]
    public async Task Load_rejects_insecure_remote_api()
    {
        using var temporary = new TempDirectory();
        var config = await WriteConfigAsync(temporary, apiBase: "http://example.com/");

        await Assert.ThrowsAsync<InputValidationException>(
            () => WorkerOptions.LoadAsync(config, CancellationToken.None));
    }

    [Fact]
    public async Task Load_rejects_overlapping_work_and_publish_roots()
    {
        using var temporary = new TempDirectory();
        var work = temporary.Resolve("work");
        var config = await WriteConfigAsync(temporary, workRoot: work, publishRoot: Path.Combine(work, "public"));

        await Assert.ThrowsAsync<InputValidationException>(
            () => WorkerOptions.LoadAsync(config, CancellationToken.None));
    }

    [Fact]
    public void Cross_volume_paths_do_not_overlap()
    {
        if (!OperatingSystem.IsWindows()) return;
        var method = typeof(WorkerOptions).GetMethod("IsWithin", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        Assert.False((bool)method.Invoke(null, new object[] { @"C:\AtlasExample\Ingest\intake", @"F:\AtlasIngest\work" })!);
        Assert.False((bool)method.Invoke(null, new object[] { @"F:\AtlasIngest\work", @"C:\AtlasExample\Ingest\intake" })!);
    }

    private static async Task<string> WriteConfigAsync(
        TempDirectory temporary,
        string apiBase = "https://api.example.com/",
        string? workRoot = null,
        string? publishRoot = null)
    {
        var intake = temporary.Resolve("intake");
        var archive = temporary.Resolve("archive");
        var profile = temporary.Resolve("renderer.json");
        Directory.CreateDirectory(intake);
        Directory.CreateDirectory(archive);
        await File.WriteAllTextAsync(profile, "{}");
        var config = temporary.Resolve("worker.json");
        await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            apiBase,
            apiKeyEnvironment = "ATLAS_TEST_WORKER_KEY",
            intakeRoot = intake,
            archiveRoot = archive,
            workRoot = workRoot ?? temporary.Resolve("work"),
            rendererProfile = profile,
            publishRoot = publishRoot ?? temporary.Resolve("published"),
            publicTileRoot = "https://tiles.example.com/AtlasTiles",
            tileScheme = "atlas-overworld-sparse-v1",
            pollSeconds = 15,
        }));
        return config;
    }
}