namespace _2b2tAtlas.Server.Services.AiEnrichment;

/// <summary>
/// Drains the <see cref="EnrichmentQueue"/> and enriches each queued location in the background, one at a
/// time, in its own DI scope. It runs enrichment after a render is attached so a fresh base automatically
/// gets a wiki match and drafted description in the review queue, without blocking the ingestion request.
/// Failures are logged and swallowed so a single bad location never stops the worker, and it stays idle when
/// the engine is disabled or paused.
/// </summary>
public sealed class EnrichmentBackgroundService : BackgroundService
{
    private readonly EnrichmentQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EnrichmentBackgroundService> _logger;

    /// <summary>Initializes the background enrichment worker.</summary>
    /// <param name="queue">The queue of location ids to process.</param>
    /// <param name="scopeFactory">The factory used to create a scope per location for scoped services.</param>
    /// <param name="logger">The diagnostic logger.</param>
    public EnrichmentBackgroundService(
        EnrichmentQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<EnrichmentBackgroundService> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var locationId in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var pipeline = scope.ServiceProvider.GetRequiredService<EnrichmentPipeline>();
                var outcome = await pipeline.EnrichLocationAsync(
                    locationId, autoApply: true, userId: null, username: "AI Enrichment (pipeline)", stoppingToken);
                _logger.LogInformation("Pipeline enrichment for location {LocationId}: {Outcome}", locationId, outcome);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Pipeline enrichment failed for location {LocationId}.", locationId);
            }
        }
    }
}
