using Atlas.Enrichment;
using Microsoft.EntityFrameworkCore;
using _2b2tAtlas.Server.Models;
using _2b2tAtlas.Server.Services;

namespace _2b2tAtlas.Server.Services.AiEnrichment;

/// <summary>A lightweight location identity used by a catalog-wide enrichment pass.</summary>
public sealed record EnrichmentBatchCandidate(int Id, string Name);

/// <summary>Abstraction used by the singleton coordinator to resolve a fresh scoped batch executor.</summary>
public interface IEnrichmentBatchRunner
{
    /// <summary>Runs every eligible location and reports progress.</summary>
    Task<EnrichmentRunSummary> RunAsync(
        bool autoApply,
        int? userId,
        string? username,
        Action<EnrichmentRunStatusDto> report,
        CancellationToken cancellationToken);
}

/// <summary>
/// Executes one complete catalog enrichment pass inside a server-owned dependency-injection scope.
/// The runner is deliberately independent of an HTTP request so a proxy timeout, navigation, or browser
/// refresh cannot cancel the work.
/// </summary>
public sealed class EnrichmentBatchRunner : IEnrichmentBatchRunner
{
    private const string TypeLocation = "Location";
    private const string SourceAi = "AI";

    private readonly AtlasContext _context;
    private readonly EnrichmentPipeline _pipeline;
    private readonly GroupEvidenceIndexService _groupEvidence;
    private readonly AuditService _audit;
    private readonly ILogger<EnrichmentBatchRunner> _logger;

    /// <summary>Initializes a scoped batch runner.</summary>
    public EnrichmentBatchRunner(
        AtlasContext context,
        EnrichmentPipeline pipeline,
        GroupEvidenceIndexService groupEvidence,
        AuditService audit,
        ILogger<EnrichmentBatchRunner> logger)
    {
        _context = context;
        _pipeline = pipeline;
        _groupEvidence = groupEvidence;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>Runs every eligible location and reports progress after candidate discovery and each row.</summary>
    public async Task<EnrichmentRunSummary> RunAsync(
        bool autoApply,
        int? userId,
        string? username,
        Action<EnrichmentRunStatusDto> report,
        CancellationToken cancellationToken)
    {
        var candidates = await GetCandidatesAsync(cancellationToken);
        var status = new EnrichmentRunStatusDto { Total = candidates.Count, AutoApply = autoApply };
        report(status);

        var summary = new EnrichmentRunSummary { Ran = true };
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            status.CurrentLocationId = candidate.Id;
            status.CurrentLocationName = candidate.Name;
            report(status);

            EnrichmentOutcome outcome;
            try
            {
                outcome = await _pipeline.EnrichLocationAsync(
                    candidate.Id, autoApply, userId, username ?? "AI Enrichment", cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                status.Errors++;
                summary.Scanned++;
                summary.Skipped++;
                _logger.LogError(exception, "AI enrichment failed for location {LocationId} ({LocationName}); continuing batch.",
                    candidate.Id, candidate.Name);
                status.Scanned = summary.Scanned;
                status.Skipped = summary.Skipped;
                report(status);
                continue;
            }

            summary.Scanned++;
            switch (outcome)
            {
                case EnrichmentOutcome.AutoApplied:
                    summary.Matched++;
                    summary.AutoApplied++;
                    break;
                case EnrichmentOutcome.Queued:
                    summary.Matched++;
                    summary.Queued++;
                    break;
                default:
                    summary.Skipped++;
                    break;
            }

            status.Scanned = summary.Scanned;
            status.Matched = summary.Matched;
            status.AutoApplied = summary.AutoApplied;
            status.Queued = summary.Queued;
            status.Skipped = summary.Skipped;
            report(status);
        }

        status.CurrentLocationId = null;
        status.CurrentLocationName = null;
        report(status);
        await _audit.LogAsync("enrichment.run", TypeLocation, 0, userId, username,
            $"Enrichment run: scanned {summary.Scanned}, applied {summary.AutoApplied}, queued {summary.Queued}, " +
            $"skipped {summary.Skipped}, errors {status.Errors}");
        return summary;
    }

    private async Task<List<EnrichmentBatchCandidate>> GetCandidatesAsync(CancellationToken cancellationToken)
    {
        // Open review work is excluded before materialization. At Atlas scale, running the complete eligible
        // catalog avoids operator-sized batches permanently starving older imports.
        var candidateQuery = _context.Locations.AsNoTracking().Where(location => !_context.Revisions.Any(revision =>
            revision.Source == SourceAi && revision.EntityType == TypeLocation &&
            revision.EntityId == location.Rowid &&
            (revision.Status == "Applied" || revision.Status == "Pending")));
        var groups = await _context.Groups.AsNoTracking().ToListAsync(cancellationToken);
        var indexedPairs = _groupEvidence.GetCandidatePairs(groups);
        var evidenceBackedIds = indexedPairs.Select(pair => pair.LocationId).Distinct().ToArray();
        var linkedPairs = (await _context.LocationGroups.AsNoTracking()
                .Select(link => new { link.LocationRowid, link.GroupId }).ToListAsync(cancellationToken))
            .Select(link => new GroupEvidenceCandidate(link.LocationRowid, link.GroupId))
            .ToHashSet();
        var missingGroupIds = indexedPairs.Except(linkedPairs)
            .Select(pair => pair.LocationId).Distinct().ToArray();

        return await candidateQuery
            .OrderByDescending(location => evidenceBackedIds.Contains(location.Rowid))
            .ThenByDescending(location => missingGroupIds.Contains(location.Rowid))
            .ThenByDescending(location => location.Rowid)
            .Select(location => new EnrichmentBatchCandidate(location.Rowid, location.Name ?? $"Location {location.Rowid}"))
            .ToListAsync(cancellationToken);
    }
}
