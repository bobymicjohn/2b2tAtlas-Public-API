using System.Text;
using System.Text.Json;
using Atlas.Ingestion;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using _2b2tAtlas.Server.Services;

namespace Atlas.Ingestor.Tests;

public sealed class ArchiveCollectorStatusServiceTests
{
    [Fact]
    public async Task GetAsync_returns_sanitized_live_queue_and_worker_progress()
    {
        using var temp = new TempDirectory();
        var runRoot = temp.Resolve("run");
        var primaryRoot = temp.Resolve("primary");
        var workerRoot = temp.Resolve("profiles", "collector_two");
        Directory.CreateDirectory(runRoot);
        Directory.CreateDirectory(Path.Combine(primaryRoot, "game", "logs"));
        Directory.CreateDirectory(Path.Combine(workerRoot, "game", "logs"));
        var stdout = Path.Combine(runRoot, "worker-2.out.log");
        await File.WriteAllTextAsync(stdout, "starting\nWARP Test_Base_2026-09-04\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(workerRoot, "game", "logs", "latest.log"),
            "ATLAS_COVER received=1234 missing=12 waypoint=3/8\n",
            TestContext.Current.CancellationToken);

        await WriteJson(Path.Combine(runRoot, "capture-queue.json"), new
        {
            entries = new[] { new { normalizedWarp = "saved" }, new { normalizedWarp = "gone" }, new { normalizedWarp = "pending" } }
        });
        await WriteJson(Path.Combine(runRoot, "collector-state.json"), new
        {
            updatedUtc = DateTimeOffset.UtcNow,
            entries = new object[]
            {
                new { normalizedWarp = "saved", status = "captured", adaptive = new { standardVersion = 2, componentSelection = new { method = "seed" } } },
                new { normalizedWarp = "gone", status = "missing" },
                new { normalizedWarp = "retry", status = "retryable" }
            }
        });
        await WriteJson(Path.Combine(runRoot, "parallel-collector-status.json"), new
        {
            updatedUtc = DateTimeOffset.UtcNow,
            deferredToLongRunning = 3,
            workers = new[]
            {
                new
                {
                    id = 2, profile = "collector_two", processId = Environment.ProcessId, running = true,
                    restarting = false, restartCount = 1, assigned = 10, completed = 4,
                    currentOrLastWarp = "fallback", minecraftVersion = "fabric-loader-0.19.3-1.21.10", stdout,
                    lane = "throughput", adaptiveMaximumRuntimeSeconds = 1800, deferredIn = 0, deferredOut = 3
                }
            }
        });
        await WriteJson(Path.Combine(runRoot, "final-standard-handoff-status.json"), new { stage = "waiting-for-collector" });
        Directory.CreateDirectory(Path.Combine(runRoot, "rolling-handoff"));
        await WriteJson(Path.Combine(runRoot, "rolling-handoff", "status.json"), new { stage = "submitting", submitted = 7 });

        var service = new ArchiveCollectorStatusService(
            Options.Create(new ArchiveCollectorStatusOptions
            {
                RunRoot = runRoot,
                PauseSignalPath = Path.Combine(runRoot, "pause-collector"),
                PrimaryProfileRoot = primaryRoot,
                ProfilesRoot = temp.Resolve("profiles"),
                CacheSeconds = 2,
                StaleAfterSeconds = 90
            }),
            NullLogger<ArchiveCollectorStatusService>.Instance);

        var result = await service.GetAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Available);
        Assert.True(result.Active);
        Assert.Equal(3, result.QueueTotal);
        Assert.Equal(2, result.Checked);
        Assert.Equal(1, result.Saved);
        Assert.Equal(1, result.Unavailable);
        Assert.Equal(1, result.Remaining);
        Assert.Equal(1, result.Retryable);
        Assert.Equal("Submitting", result.HandoffStage);
        Assert.Equal(7, result.HandoffSubmitted);
        Assert.Equal(1, result.FastLaneWorkers);
        Assert.Equal(0, result.LongRunningWorkers);
        Assert.Equal(3, result.DeferredToLongRunning);
        var worker = Assert.Single(result.Workers);
        Assert.Equal("fabric-loader-0.19.3-1.21.10", worker.MinecraftVersion);
        Assert.Equal("Test_Base_2026-09-04", worker.Warp);
        Assert.Equal(1234, worker.ChunksReceived);
        Assert.Equal(12, worker.MissingChunks);
        Assert.Equal(3, worker.Waypoint);
        Assert.Equal(8, worker.WaypointTotal);
        Assert.Equal("live", worker.Outcome);
        Assert.Equal("throughput", worker.Lane);
        Assert.Equal(1800, worker.AdaptiveMaximumRuntimeSeconds);
        Assert.Equal(3, worker.DeferredOut);
    }

    [Fact]
    public async Task GetAsync_reports_connecting_until_live_coverage_telemetry_exists()
    {
        using var temp = new TempDirectory();
        var runRoot = temp.Resolve("run");
        var primaryRoot = temp.Resolve("primary");
        Directory.CreateDirectory(runRoot);
        Directory.CreateDirectory(Path.Combine(primaryRoot, "game", "logs"));
        await File.WriteAllTextAsync(
            Path.Combine(primaryRoot, "game", "logs", "latest.log"),
            "Connecting to thearchive.world...\n",
            TestContext.Current.CancellationToken);
        await WriteJson(Path.Combine(runRoot, "capture-queue.json"), new { entries = new[] { new { normalizedWarp = "pending" } } });
        await WriteJson(Path.Combine(runRoot, "collector-state.json"), new { updatedUtc = DateTimeOffset.UtcNow, entries = Array.Empty<object>() });
        await WriteJson(Path.Combine(runRoot, "parallel-collector-status.json"), new
        {
            updatedUtc = DateTimeOffset.UtcNow,
            workers = new[]
            {
                new
                {
                    id = 1, profile = "primary", processId = Environment.ProcessId, running = true,
                    restarting = false, restartCount = 0, assigned = 1, completed = 0,
                    currentOrLastWarp = "pending", stdout = ""
                }
            }
        });

        var service = new ArchiveCollectorStatusService(
            Options.Create(new ArchiveCollectorStatusOptions
            {
                RunRoot = runRoot,
                PauseSignalPath = Path.Combine(runRoot, "pause-collector"),
                PrimaryProfileRoot = primaryRoot,
                ProfilesRoot = temp.Resolve("profiles")
            }),
            NullLogger<ArchiveCollectorStatusService>.Instance);

        var result = await service.GetAsync(TestContext.Current.CancellationToken);
        var worker = Assert.Single(result.Workers);
        Assert.Equal("Recovering", result.Status);
        Assert.False(result.Active);
        Assert.Equal(0, result.ActiveWorkers);
        Assert.Equal("Connecting", worker.Status);
        Assert.Equal("connecting", worker.Outcome);
        Assert.Null(worker.ChunksReceived);
        Assert.Null(worker.MissingChunks);
        Assert.Null(worker.Waypoint);
        Assert.Null(worker.WaypointTotal);
    }

    [Fact]
    public async Task GetAsync_fails_closed_when_checkpoint_files_are_absent()
    {
        using var temp = new TempDirectory();
        var service = new ArchiveCollectorStatusService(
            Options.Create(new ArchiveCollectorStatusOptions { RunRoot = temp.Resolve("missing") }),
            NullLogger<ArchiveCollectorStatusService>.Instance);

        var result = await service.GetAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Available);
        Assert.False(result.Active);
        Assert.Empty(result.Workers);
        Assert.NotNull(result.NextScheduledRun);
    }

    [Fact]
    public async Task GetAsync_prefers_live_teleport_over_stale_supervisor_stdout()
    {
        using var temp = new TempDirectory();
        var runRoot = temp.Resolve("run");
        var primaryRoot = temp.Resolve("primary");
        Directory.CreateDirectory(runRoot);
        Directory.CreateDirectory(Path.Combine(primaryRoot, "game", "logs"));
        var stdout = Path.Combine(runRoot, "worker-1.out.log");
        await File.WriteAllTextAsync(stdout, "WARP Previous_Warp\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(primaryRoot, "game", "logs", "latest.log"),
            "[System] [CHAT] \u001b[m\u001b[90m[\u001b[33mWarps\u001b[90m] \u001b[37mYou were teleported to '\u001b[96mCurrent_Large_Base_2021-06-05\u001b[37m'.\u001b[0m\n" +
            "ATLAS_COVER settled waypoint=12/74 received=48401 missing=2669 quiet=true\n",
            TestContext.Current.CancellationToken);
        await WriteJson(Path.Combine(runRoot, "capture-queue.json"), new { entries = new[] { new { normalizedWarp = "pending" } } });
        await WriteJson(Path.Combine(runRoot, "collector-state.json"), new { updatedUtc = DateTimeOffset.UtcNow, entries = Array.Empty<object>() });
        await WriteJson(Path.Combine(runRoot, "parallel-collector-status.json"), new
        {
            updatedUtc = DateTimeOffset.UtcNow,
            workers = new[]
            {
                new
                {
                    id = 1, profile = "atlas-owner", processId = Environment.ProcessId, running = true,
                    restarting = false, restartCount = 0, assigned = 1, completed = 0,
                    currentOrLastWarp = "checkpoint_fallback", stdout
                }
            }
        });

        var service = new ArchiveCollectorStatusService(
            Options.Create(new ArchiveCollectorStatusOptions
            {
                RunRoot = runRoot,
                PauseSignalPath = Path.Combine(runRoot, "pause-collector"),
                PrimaryProfileRoot = primaryRoot,
                ProfilesRoot = temp.Resolve("profiles"),
                CacheSeconds = 2,
                StaleAfterSeconds = 90
            }),
            NullLogger<ArchiveCollectorStatusService>.Instance);

        var worker = Assert.Single((await service.GetAsync(TestContext.Current.CancellationToken)).Workers);
        Assert.Equal("Current_Large_Base_2021-06-05", worker.Warp);
        Assert.Equal(48401, worker.ChunksReceived);
        Assert.Equal(12, worker.Waypoint);
        Assert.Equal(74, worker.WaypointTotal);
    }

    [Fact]
    public async Task GetAsync_reads_current_collector_log_beyond_one_megabyte_tail()
    {
        using var temp = new TempDirectory();
        var runRoot = temp.Resolve("run");
        var primaryRoot = temp.Resolve("primary");
        var collectorLogs = Path.Combine(primaryRoot, "logs");
        Directory.CreateDirectory(runRoot);
        Directory.CreateDirectory(collectorLogs);
        Directory.CreateDirectory(Path.Combine(primaryRoot, "game", "logs"));
        var stdout = Path.Combine(runRoot, "worker-1.out.log");
        await File.WriteAllTextAsync(stdout, "WARP Stale_Avalon_City\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(primaryRoot, "game", "logs", "latest.log"),
            "[Warps] You were teleported to 'Stale_Game_Warp'.\n",
            TestContext.Current.CancellationToken);
        var collectorLog = new StringBuilder()
            .AppendLine("[STDOUT] [Warps] You were teleported to 'La_Rosa_2016-08-05'.")
            .Append('x', 1_200_000)
            .AppendLine()
            .AppendLine("[STDOUT] ATLAS_COVER settled waypoint=26/26 received=79910 missing=5 quiet=true")
            .ToString();
        await File.WriteAllTextAsync(
            Path.Combine(collectorLogs, "collector-20260904-092600.log"),
            collectorLog,
            TestContext.Current.CancellationToken);
        await WriteJson(Path.Combine(runRoot, "capture-queue.json"), new { entries = new[] { new { normalizedWarp = "pending" } } });
        await WriteJson(Path.Combine(runRoot, "collector-state.json"), new { updatedUtc = DateTimeOffset.UtcNow, entries = Array.Empty<object>() });
        await WriteJson(Path.Combine(runRoot, "parallel-collector-status.json"), new
        {
            updatedUtc = DateTimeOffset.UtcNow,
            workers = new[]
            {
                new
                {
                    id = 1, profile = "atlas-owner", processId = Environment.ProcessId, running = true,
                    restarting = false, restartCount = 0, assigned = 1, completed = 0,
                    currentOrLastWarp = "checkpoint_fallback", stdout
                }
            }
        });

        var service = new ArchiveCollectorStatusService(
            Options.Create(new ArchiveCollectorStatusOptions
            {
                RunRoot = runRoot,
                PauseSignalPath = Path.Combine(runRoot, "pause-collector"),
                PrimaryProfileRoot = primaryRoot,
                ProfilesRoot = temp.Resolve("profiles"),
                CacheSeconds = 2,
                StaleAfterSeconds = 90
            }),
            NullLogger<ArchiveCollectorStatusService>.Instance);

        var worker = Assert.Single((await service.GetAsync(TestContext.Current.CancellationToken)).Workers);
        Assert.Equal("La_Rosa_2016-08-05", worker.Warp);
        Assert.Equal(79910, worker.ChunksReceived);
        Assert.Equal(26, worker.Waypoint);
        Assert.Equal(26, worker.WaypointTotal);
    }

    [Fact]
    public async Task GetAsync_reports_current_backend_rejection_without_exposing_log_details()
    {
        using var temp = new TempDirectory();
        var runRoot = temp.Resolve("run");
        var primaryRoot = temp.Resolve("primary");
        var collectorLogs = Path.Combine(primaryRoot, "logs");
        Directory.CreateDirectory(runRoot);
        Directory.CreateDirectory(collectorLogs);
        var stdout = Path.Combine(runRoot, "worker-1.out.log");
        await File.WriteAllTextAsync(stdout, "Starting current launch\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(collectorLogs, "collector-current.log"),
            "Client disconnected with reason: Unable to connect to archive:\n",
            TestContext.Current.CancellationToken);
        await WriteMinimalStatus(runRoot, new
        {
            id = 1, profile = "atlas-owner", processId = Environment.ProcessId, running = true,
            restarting = false, restartCount = 12, assigned = 10, completed = 3,
            currentOrLastWarp = "Current_Warp", minecraftVersion = "fabric-loader-1.21.11", stdout
        });

        var service = NewService(runRoot, primaryRoot, temp.Resolve("profiles"));
        var worker = Assert.Single((await service.GetAsync(TestContext.Current.CancellationToken)).Workers);

        Assert.Equal("Archive unavailable", worker.Status);
        Assert.Equal("archive-backend-rejected", worker.Outcome);
        Assert.Null(worker.ChunksReceived);
        Assert.DoesNotContain("archive:", worker.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetAsync_uses_isolated_compatibility_profile_for_worker_five()
    {
        using var temp = new TempDirectory();
        var runRoot = temp.Resolve("run");
        var profilesRoot = temp.Resolve("profiles");
        var normalRoot = temp.Resolve("profiles", "collector-five");
        var compatibilityRoot = temp.Resolve("compatibility");
        Directory.CreateDirectory(runRoot);
        Directory.CreateDirectory(Path.Combine(normalRoot, "logs"));
        Directory.CreateDirectory(Path.Combine(compatibilityRoot, "logs"));
        await File.WriteAllTextAsync(
            Path.Combine(normalRoot, "logs", "collector-stale.log"),
            "ATLAS_COVER settled received=99999 missing=0 waypoint=1/1\n",
            TestContext.Current.CancellationToken);
        var stdout = Path.Combine(runRoot, "worker-5.out.log");
        await File.WriteAllTextAsync(stdout, "Starting 1.21.10\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(compatibilityRoot, "logs", "collector-current.log"),
            "Unable to connect to archive:\n",
            TestContext.Current.CancellationToken);
        await WriteMinimalStatus(runRoot, new
        {
            id = 5, profile = "collector-five", processId = Environment.ProcessId, running = true,
            restarting = false, restartCount = 4, assigned = 10, completed = 2,
            currentOrLastWarp = "Compat_Warp", minecraftVersion = "fabric-loader-1.21.10", stdout
        });

        var service = NewService(runRoot, temp.Resolve("primary"), profilesRoot, compatibilityRoot);
        var worker = Assert.Single((await service.GetAsync(TestContext.Current.CancellationToken)).Workers);

        Assert.Equal("archive-backend-rejected", worker.Outcome);
        Assert.Null(worker.ChunksReceived);
    }

    [Fact]
    public async Task GetAsync_does_not_attribute_previous_launch_rejection_to_fresh_attempt()
    {
        using var temp = new TempDirectory();
        var runRoot = temp.Resolve("run");
        var primaryRoot = temp.Resolve("primary");
        var collectorLogs = Path.Combine(primaryRoot, "logs");
        Directory.CreateDirectory(runRoot);
        Directory.CreateDirectory(collectorLogs);
        var oldLog = Path.Combine(collectorLogs, "collector-old.log");
        await File.WriteAllTextAsync(oldLog, "Unable to connect to archive:\n", TestContext.Current.CancellationToken);
        File.SetCreationTimeUtc(oldLog, DateTime.UtcNow.AddMinutes(-2));
        var stdout = Path.Combine(runRoot, "worker-1.out.log");
        await File.WriteAllTextAsync(stdout, "Starting fresh attempt\n", TestContext.Current.CancellationToken);
        await WriteMinimalStatus(runRoot, new
        {
            id = 1, profile = "atlas-owner", processId = Environment.ProcessId, running = true,
            restarting = false, restartCount = 13, assigned = 10, completed = 3,
            currentOrLastWarp = "Fresh_Warp", minecraftVersion = "fabric-loader-1.21.11", stdout
        });

        var service = NewService(runRoot, primaryRoot, temp.Resolve("profiles"));
        var worker = Assert.Single((await service.GetAsync(TestContext.Current.CancellationToken)).Workers);

        Assert.Equal("Connecting", worker.Status);
        Assert.Equal("connecting", worker.Outcome);
    }

    [Fact]
    public async Task GetAsync_does_not_count_stopped_worker_with_last_progress_as_active()
    {
        using var temp = new TempDirectory();
        var runRoot = temp.Resolve("run");
        var primaryRoot = temp.Resolve("primary");
        var collectorLogs = Path.Combine(primaryRoot, "logs");
        Directory.CreateDirectory(runRoot);
        Directory.CreateDirectory(collectorLogs);
        var stdout = Path.Combine(runRoot, "worker-1.out.log");
        await File.WriteAllTextAsync(stdout, "Previous launch\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(collectorLogs, "collector-current.log"),
            "ATLAS_COVER settled received=12000 missing=4 waypoint=8/8\n",
            TestContext.Current.CancellationToken);
        await WriteMinimalStatus(runRoot, new
        {
            id = 1, profile = "atlas-owner", processId = 0, running = false,
            restarting = true, restartCount = 14, assigned = 10, completed = 3,
            currentOrLastWarp = "Previous_Warp", minecraftVersion = "fabric-loader-1.21.11", stdout
        });

        var service = NewService(runRoot, primaryRoot, temp.Resolve("profiles"));
        var result = await service.GetAsync(TestContext.Current.CancellationToken);
        var worker = Assert.Single(result.Workers);

        Assert.Equal("Restarting", worker.Status);
        Assert.Equal("backoff", worker.Outcome);
        Assert.Equal(0, result.ActiveWorkers);
        Assert.False(result.Active);
    }

    [Fact]
    public async Task GetAsync_exposes_all_six_workers_and_lane_counts()
    {
        using var temp = new TempDirectory();
        var runRoot = temp.Resolve("run");
        Directory.CreateDirectory(runRoot);
        await WriteJson(Path.Combine(runRoot, "capture-queue.json"), new { entries = Array.Empty<object>() });
        await WriteJson(Path.Combine(runRoot, "collector-state.json"), new { updatedUtc = DateTimeOffset.UtcNow, entries = Array.Empty<object>() });
        var workers = Enumerable.Range(1, 6).Select(id => new
        {
            id,
            profile = $"worker{id}",
            processId = 0,
            running = false,
            restarting = true,
            restartCount = 0,
            assigned = 10,
            completed = 0,
            currentOrLastWarp = $"warp{id}",
            lane = id >= 5 ? "throughput" : "large-wdl",
            adaptiveMaximumRuntimeSeconds = id >= 5 ? 1800 : 43200,
            deferredIn = id == 1 ? 1 : 0,
            deferredOut = id == 5 ? 1 : 0,
            stdout = string.Empty
        }).ToArray();
        await WriteJson(Path.Combine(runRoot, "parallel-collector-status.json"), new
        {
            updatedUtc = DateTimeOffset.UtcNow,
            deferredToLongRunning = 1,
            workers
        });

        var result = await NewService(runRoot, temp.Resolve("primary"), temp.Resolve("profiles"))
            .GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(6, result.Workers.Count);
        Assert.Equal(2, result.FastLaneWorkers);
        Assert.Equal(4, result.LongRunningWorkers);
        Assert.Equal(1, result.DeferredToLongRunning);
        Assert.Equal("throughput", result.Workers.Single(worker => worker.Id == 6).Lane);
    }

    [Theory]
    [InlineData("temporary-long", false, "Done", 43200)]
    [InlineData("throughput", true, "Awaiting refill", 1800)]
    public async Task Fast_lane_modes_and_refill_waits_remain_visible(string lane, bool pending, string status, int budget)
    {
        using var temp = new TempDirectory();
        var runRoot = temp.Resolve("run");
        Directory.CreateDirectory(runRoot);
        await WriteMinimalStatus(runRoot, new
        {
            id = 5, profile = "collector-five", processId = 0, running = false, restarting = false,
            assigned = 71, completed = 71, lane, refillPending = pending,
            adaptiveMaximumRuntimeSeconds = budget
        });
        var result = await NewService(runRoot, temp.Resolve("primary"), temp.Resolve("profiles"))
            .GetAsync(TestContext.Current.CancellationToken);
        var worker = Assert.Single(result.Workers);
        Assert.Equal(status, worker.Status);
        Assert.Equal(budget, worker.AdaptiveMaximumRuntimeSeconds);
        Assert.Equal(1, result.FastLaneWorkers);
        Assert.Equal(0, result.LongRunningWorkers);
    }

    [Fact]
    public async Task Operator_pause_remains_paused_without_a_running_supervisor()
    {
        using var temp = new TempDirectory();
        var runRoot = temp.Resolve("run");
        Directory.CreateDirectory(runRoot);
        await WriteMinimalStatus(runRoot, new { id = 1, profile = "fixture", running = false });
        await WriteJson(Path.Combine(runRoot, "parallel-collector-status.json"), new
        {
            stage = "operator-paused", updatedUtc = DateTimeOffset.UtcNow.AddDays(-1),
            workers = new[] { new { id = 1, profile = "fixture", running = false, restarting = false, phase = "paused", assigned = 10 } }
        });
        var result = await NewService(runRoot, temp.Resolve("primary"), temp.Resolve("profiles"))
            .GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Paused", result.Status);
        Assert.False(result.Stale);
        Assert.False(result.Active);
        Assert.Equal("Paused", Assert.Single(result.Workers).Status);
    }

    [Fact]
    public async Task Disk_pause_latch_overrides_old_supervisor_restart_status()
    {
        using var temp = new TempDirectory();
        var runRoot = temp.Resolve("run");
        Directory.CreateDirectory(runRoot);
        await WriteMinimalStatus(runRoot, new { id = 1, profile = "fixture", running = false, restarting = true, assigned = 10 });
        await File.WriteAllTextAsync(Path.Combine(runRoot, "pause-collector"), "low disk", TestContext.Current.CancellationToken);
        var result = await NewService(runRoot, temp.Resolve("primary"), temp.Resolve("profiles"))
            .GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Paused", result.Status);
        Assert.False(result.Active);
        Assert.False(result.Stale);
        Assert.Equal("Paused", Assert.Single(result.Workers).Status);
    }

    private static ArchiveCollectorStatusService NewService(
        string runRoot,
        string primaryRoot,
        string profilesRoot,
        string? compatibilityRoot = null) => new(
            Options.Create(new ArchiveCollectorStatusOptions
            {
                RunRoot = runRoot,
                PauseSignalPath = Path.Combine(runRoot, "pause-collector"),
                PrimaryProfileRoot = primaryRoot,
                ProfilesRoot = profilesRoot,
                CompatibilityProfileRoot = compatibilityRoot ?? Path.Combine(profilesRoot, "compatibility"),
                CacheSeconds = 0,
                StaleAfterSeconds = 90
            }),
            NullLogger<ArchiveCollectorStatusService>.Instance);

    private static async Task WriteMinimalStatus(string runRoot, object worker)
    {
        await WriteJson(Path.Combine(runRoot, "capture-queue.json"), new { entries = new[] { new { normalizedWarp = "pending" } } });
        await WriteJson(Path.Combine(runRoot, "collector-state.json"), new { updatedUtc = DateTimeOffset.UtcNow, entries = Array.Empty<object>() });
        await WriteJson(Path.Combine(runRoot, "parallel-collector-status.json"), new { updatedUtc = DateTimeOffset.UtcNow, workers = new[] { worker } });
    }

    private static Task WriteJson(string path, object value)
    {
        var json = JsonSerializer.Serialize(value);
        return File.WriteAllTextAsync(path, json, TestContext.Current.CancellationToken);
    }
}
