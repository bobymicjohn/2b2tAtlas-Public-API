using Atlas.Enrichment;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using _2b2tAtlas.Server.Services.AiEnrichment;

namespace Atlas.Ingestor.Tests;

public sealed class EnrichmentRunCoordinatorTests
{
    [Fact]
    public async Task Run_is_server_owned_reports_progress_and_rejects_overlap()
    {
        var executor = new ControlledRunner();
        var services = new ServiceCollection();
        services.AddSingleton<IEnrichmentBatchRunner>(executor);
        using var provider = services.BuildServiceProvider();
        using var lifetime = new TestLifetime();
        var coordinator = new EnrichmentRunCoordinator(
            provider.GetRequiredService<IServiceScopeFactory>(),
            lifetime,
            NullLogger<EnrichmentRunCoordinator>.Instance);

        Assert.True(coordinator.TryStart(autoApply: true, userId: 7, username: "operator", out var started));
        Assert.True(started.IsRunning);
        Assert.Equal("operator", started.StartedBy);

        await executor.ProgressReported.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        var progress = coordinator.GetStatus();
        Assert.True(progress.IsRunning);
        Assert.Equal(10, progress.Total);
        Assert.Equal(3, progress.Scanned);
        Assert.Equal("Example Base", progress.CurrentLocationName);
        Assert.False(coordinator.TryStart(autoApply: false, userId: 8, username: "other", out var duplicate));
        Assert.Equal(started.RunId, duplicate.RunId);

        executor.Complete.SetResult(new EnrichmentRunSummary
        {
            Ran = true,
            Scanned = 10,
            Matched = 2,
            AutoApplied = 1,
            Queued = 1,
            Skipped = 8,
        });

        EnrichmentRunStatusDto completed;
        var timeout = DateTime.UtcNow.AddSeconds(2);
        do
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
            completed = coordinator.GetStatus();
        } while (completed.IsRunning && DateTime.UtcNow < timeout);

        Assert.False(completed.IsRunning);
        Assert.Equal("completed", completed.State);
        Assert.Equal(10, completed.Scanned);
        Assert.Equal(1, completed.AutoApplied);
        Assert.Equal(1, completed.Queued);
        Assert.Equal(100, completed.ProgressPercent);
    }

    private sealed class ControlledRunner : IEnrichmentBatchRunner
    {
        public TaskCompletionSource<bool> ProgressReported { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<EnrichmentRunSummary> Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<EnrichmentRunSummary> RunAsync(
            bool autoApply,
            int? userId,
            string? username,
            Action<EnrichmentRunStatusDto> report,
            CancellationToken cancellationToken)
        {
            report(new EnrichmentRunStatusDto
            {
                Total = 10,
                Scanned = 3,
                Matched = 1,
                Queued = 1,
                CurrentLocationId = 42,
                CurrentLocationName = "Example Base",
            });
            ProgressReported.TrySetResult(true);
            cancellationToken.Register(() => Complete.TrySetCanceled(cancellationToken));
            return Complete.Task;
        }
    }

    private sealed class TestLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;
        public void StopApplication() => _stopping.Cancel();

        public void Dispose()
        {
            _started.Dispose();
            _stopping.Dispose();
            _stopped.Dispose();
        }
    }
}
