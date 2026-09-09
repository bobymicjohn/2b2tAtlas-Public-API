using Atlas.Enrichment;

namespace _2b2tAtlas.Server.Services.AiEnrichment;

/// <summary>
/// Owns the singleton state and lifetime of a catalog-wide enrichment run. The coordinator guarantees that
/// only one run can exist per API process and exposes immutable snapshots to every administrator.
/// </summary>
public sealed class EnrichmentRunCoordinator
{
    private readonly object _gate = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ILogger<EnrichmentRunCoordinator> _logger;
    private EnrichmentRunStatusDto _status = new();

    /// <summary>Initializes the server-owned run coordinator.</summary>
    public EnrichmentRunCoordinator(
        IServiceScopeFactory scopeFactory,
        IHostApplicationLifetime applicationLifetime,
        ILogger<EnrichmentRunCoordinator> logger)
    {
        _scopeFactory = scopeFactory;
        _applicationLifetime = applicationLifetime;
        _logger = logger;
    }

    /// <summary>Gets a detached snapshot safe to serialize or render.</summary>
    public EnrichmentRunStatusDto GetStatus()
    {
        lock (_gate)
            return Snapshot(_status);
    }

    /// <summary>
    /// Atomically starts a server-owned run. Returns false and the active status when another operator has
    /// already started one.
    /// </summary>
    public bool TryStart(bool autoApply, int? userId, string? username, out EnrichmentRunStatusDto status)
    {
        lock (_gate)
        {
            if (_status.IsRunning)
            {
                status = Snapshot(_status);
                return false;
            }

            var now = DateTime.UtcNow;
            _status = new EnrichmentRunStatusDto
            {
                RunId = Guid.NewGuid().ToString("N"),
                State = "running",
                IsRunning = true,
                AutoApply = autoApply,
                StartedBy = string.IsNullOrWhiteSpace(username) ? "Atlas administrator" : username,
                StartedUtc = now,
                UpdatedUtc = now,
                Message = "Discovering eligible locations...",
            };
            status = Snapshot(_status);
            _ = ExecuteAsync(autoApply, userId, username, _applicationLifetime.ApplicationStopping);
            return true;
        }
    }

    private async Task ExecuteAsync(bool autoApply, int? userId, string? username, CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<IEnrichmentBatchRunner>();
            var summary = await runner.RunAsync(autoApply, userId, username, UpdateProgress, stoppingToken);
            lock (_gate)
            {
                var now = DateTime.UtcNow;
                _status.State = "completed";
                _status.IsRunning = false;
                _status.Scanned = summary.Scanned;
                _status.Matched = summary.Matched;
                _status.AutoApplied = summary.AutoApplied;
                _status.Queued = summary.Queued;
                _status.Skipped = summary.Skipped;
                _status.CurrentLocationId = null;
                _status.CurrentLocationName = null;
                _status.UpdatedUtc = now;
                _status.CompletedUtc = now;
                _status.ElapsedSeconds = ElapsedSeconds(_status, now);
                _status.Message = $"Finished: {summary.Scanned} scanned, {summary.AutoApplied} applied, " +
                                  $"{summary.Queued} queued, {summary.Skipped} skipped.";
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            Finish("cancelled", "The API stopped before the enrichment run completed.");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Catalog-wide AI enrichment run failed.");
            Finish("failed", $"Run failed: {exception.GetBaseException().Message}");
        }
    }

    private void UpdateProgress(EnrichmentRunStatusDto progress)
    {
        lock (_gate)
        {
            if (!_status.IsRunning) return;
            var now = DateTime.UtcNow;
            _status.Total = progress.Total;
            _status.Scanned = progress.Scanned;
            _status.Matched = progress.Matched;
            _status.AutoApplied = progress.AutoApplied;
            _status.Queued = progress.Queued;
            _status.Skipped = progress.Skipped;
            _status.Errors = progress.Errors;
            _status.CurrentLocationId = progress.CurrentLocationId;
            _status.CurrentLocationName = progress.CurrentLocationName;
            _status.UpdatedUtc = now;
            _status.ElapsedSeconds = ElapsedSeconds(_status, now);
            _status.Message = progress.Total == 0
                ? "No eligible locations were found."
                : $"Processing {Math.Min(progress.Scanned + 1, progress.Total):N0} of {progress.Total:N0} locations.";
        }
    }

    private void Finish(string state, string message)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            _status.State = state;
            _status.IsRunning = false;
            _status.CurrentLocationId = null;
            _status.CurrentLocationName = null;
            _status.UpdatedUtc = now;
            _status.CompletedUtc = now;
            _status.ElapsedSeconds = ElapsedSeconds(_status, now);
            _status.Message = message;
        }
    }

    private static int ElapsedSeconds(EnrichmentRunStatusDto status, DateTime now) =>
        status.StartedUtc is null ? 0 : Math.Max(0, (int)(now - status.StartedUtc.Value).TotalSeconds);

    private static EnrichmentRunStatusDto Snapshot(EnrichmentRunStatusDto source)
    {
        var now = DateTime.UtcNow;
        return new EnrichmentRunStatusDto
        {
            RunId = source.RunId,
            State = source.State,
            IsRunning = source.IsRunning,
            AutoApply = source.AutoApply,
            StartedBy = source.StartedBy,
            StartedUtc = source.StartedUtc,
            UpdatedUtc = source.UpdatedUtc,
            CompletedUtc = source.CompletedUtc,
            Total = source.Total,
            Scanned = source.Scanned,
            Matched = source.Matched,
            AutoApplied = source.AutoApplied,
            Queued = source.Queued,
            Skipped = source.Skipped,
            Errors = source.Errors,
            CurrentLocationId = source.CurrentLocationId,
            CurrentLocationName = source.CurrentLocationName,
            Message = source.Message,
            ElapsedSeconds = source.IsRunning ? ElapsedSeconds(source, now) : source.ElapsedSeconds,
        };
    }
}
