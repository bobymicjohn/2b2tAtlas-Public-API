using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Atlas;
using Atlas.Enrichment;
using _2b2tAtlas.Server.Models;
using _2b2tAtlas.Server.Services;
using ServerLocation = _2b2tAtlas.Server.Models.Location;

namespace _2b2tAtlas.Server.Services.AiEnrichment;

/// <summary>The result of enriching a single location.</summary>
public enum EnrichmentOutcome
{
    /// <summary>The engine was disabled or paused, so nothing ran.</summary>
    Unavailable,

    /// <summary>The location no longer exists.</summary>
    NotFound,

    /// <summary>No usable match, nothing new to add, or a suggestion is already awaiting review.</summary>
    Skipped,

    /// <summary>A suggestion was queued for manual review without changing the location.</summary>
    Queued,

    /// <summary>A confident, coordinate-confirmed suggestion was auto-applied (and left in the review queue).</summary>
    AutoApplied,
}

/// <summary>
/// Enriches a single location and records the result as an AI revision. This is the shared core used by both
/// the admin batch run and the post-render background pipeline, so both paths behave identically: only blank
/// wiki links or descriptions are filled, coordinates and human-entered text are never touched, an existing
/// open suggestion for the location suppresses duplicates, and confident coordinate-confirmed matches are
/// auto-applied while weaker ones are queued for review.
/// </summary>
public sealed class EnrichmentPipeline
{
    private const string TypeLocation = "Location";
    private const string SourceAi = "AI";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly AtlasContext _context;
    private readonly AiEnrichmentService _enrichment;
    private readonly GroupEvidenceIndexService _groupEvidence;
    private readonly AuditService _audit;

    /// <summary>Initializes the shared enrichment pipeline.</summary>
    /// <param name="context">The Atlas context for locations and revisions.</param>
    /// <param name="enrichment">The local AI enrichment engine.</param>
    /// <param name="groupEvidence">The revision-pinned group/build evidence index.</param>
    /// <param name="audit">The append-only audit recorder.</param>
    public EnrichmentPipeline(
        AtlasContext context,
        AiEnrichmentService enrichment,
        GroupEvidenceIndexService groupEvidence,
        AuditService audit)
    {
        _context = context;
        _enrichment = enrichment;
        _groupEvidence = groupEvidence;
        _audit = audit;
    }

    /// <summary>
    /// Enriches one location, recording a suggestion as an AI revision. Persists its own changes.
    /// </summary>
    /// <param name="locationId">The location row id to enrich.</param>
    /// <param name="autoApply">Whether a confident, coordinate-confirmed match may be auto-applied.</param>
    /// <param name="userId">The acting user id recorded on the revision, or null for automated runs.</param>
    /// <param name="username">The actor name recorded on the revision (e.g. the operator or a pipeline label).</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>The outcome describing what happened.</returns>
    public async Task<EnrichmentOutcome> EnrichLocationAsync(
        int locationId,
        bool autoApply,
        int? userId,
        string? username,
        CancellationToken cancellationToken)
    {
        // Deterministic provenance is independent of model availability, wiki-index freshness,
        // an existing AI revision, and whether description edits qualify for auto-application.
        var archiveCredits = autoApply
            ? await new ArchiveGroupAttributionService(_context).StageAsync(locationId, cancellationToken)
            : 0;
        if (archiveCredits > 0) await _context.SaveChangesAsync(cancellationToken);

        if (!_enrichment.IsAvailable)
            return archiveCredits > 0 ? EnrichmentOutcome.AutoApplied : EnrichmentOutcome.Unavailable;

        var row = await _context.Locations
            .AsSplitQuery()
            .Include(location => location.Warps)
            .Include(location => location.Attachments)
            .Include(location => location.LocationGroups).ThenInclude(link => link.Group)
            .FirstOrDefaultAsync(location => location.Rowid == locationId, cancellationToken);
        if (row is null)
            return EnrichmentOutcome.NotFound;

        var alreadyOpen = await _context.Revisions.AnyAsync(
            r => r.Source == SourceAi && r.EntityType == TypeLocation && r.EntityId == locationId
                 && (r.Status == "Applied" || r.Status == "Pending"),
            cancellationToken);
        if (alreadyOpen)
            return archiveCredits > 0 ? EnrichmentOutcome.AutoApplied : EnrichmentOutcome.Skipped;

        var atlas = ToAtlas(row);
        var knownGroups = await _context.Groups.AsNoTracking().ToListAsync(cancellationToken);
        // Include already-reviewed relationships as revision-pinned drafting context. They are filtered
        // from groupsToAdd below, so this cannot duplicate or silently change an attribution.
        var groupSuggestions = _groupEvidence.FindMatches(atlas, knownGroups, includeExisting: true);
        var suggestion = await _enrichment.EnrichAsync(
            atlas, regenerateDescription: false, cancellationToken, groupSuggestions);

        var newWiki = string.IsNullOrWhiteSpace(row.Wiki) ? suggestion.WikiUrl : row.Wiki;
        var newDescription = string.IsNullOrWhiteSpace(row.Description) ? suggestion.SuggestedDescription : row.Description;
        var wikiChanged = !string.IsNullOrWhiteSpace(newWiki) && newWiki != row.Wiki;
        var descriptionChanged = !string.IsNullOrWhiteSpace(newDescription) && newDescription != row.Description;
        var existingGroupIds = row.LocationGroups.Select(link => link.GroupId).ToHashSet();
        var groupsToAdd = groupSuggestions.Where(group => !existingGroupIds.Contains(group.GroupId)).ToArray();
        if (!wikiChanged && !descriptionChanged && groupsToAdd.Length == 0)
            return archiveCredits > 0 ? EnrichmentOutcome.AutoApplied : EnrichmentOutcome.Skipped;

        var previousJson = JsonSerializer.Serialize(atlas, JsonOptions);
        var proposed = ToAtlas(row);
        if (wikiChanged) proposed.Wiki = newWiki;
        if (descriptionChanged) proposed.Description = newDescription;
        proposed.Groups ??= [];
        foreach (var group in groupsToAdd)
        {
            proposed.Groups.Add(new LocationGroupAttribution
            {
                GroupId = group.GroupId,
                GroupName = group.GroupName,
                Role = group.Role,
            });
        }

        var metadataEligible = !wikiChanged && !descriptionChanged || suggestion.AutoApplyEligible;
        var groupsEligible = groupsToAdd.Length == 0 || groupsToAdd.All(group => group.AutoApplyEligible);
        var eligible = autoApply && metadataEligible && groupsEligible;
        var effectiveConfidence = new[]
            {
                wikiChanged || descriptionChanged ? suggestion.Confidence : 1.0,
                groupsToAdd.Length > 0 ? groupsToAdd.Min(group => group.Confidence) : 1.0,
            }
            .Min();
        var evidenceNote = BuildEvidenceNote(suggestion.MatchReason, groupsToAdd);
        var revision = new Revision
        {
            EntityType = TypeLocation,
            EntityId = locationId,
            Source = SourceAi,
            Confidence = Math.Round(effectiveConfidence, 3),
            ProposedJson = JsonSerializer.Serialize(proposed, JsonOptions),
            PreviousJson = previousJson,
            Note = evidenceNote,
            Status = eligible ? "Applied" : "Pending",
            SubmittedByUserId = userId,
            SubmittedByUsername = string.IsNullOrWhiteSpace(username) ? "AI Enrichment" : username,
            SubmittedUtc = DateTime.UtcNow.ToString("o"),
        };
        _context.Revisions.Add(revision);

        if (eligible)
        {
            var before = Snapshot(row);
            if (wikiChanged) row.Wiki = newWiki;
            if (descriptionChanged) row.Description = newDescription;
            var now = DateTime.UtcNow.ToString("o");
            foreach (var group in groupsToAdd)
            {
                row.LocationGroups.Add(new LocationGroup
                {
                    LocationRowid = locationId,
                    GroupId = group.GroupId,
                    Role = group.Role,
                    DateAddedUtc = now,
                });
            }
            row.ModifiedUtc = DateTime.UtcNow.ToString("o");
            revision.ReviewedByUserId = userId;
            revision.ReviewedUtc = DateTime.UtcNow.ToString("o");
            await _audit.LogDiffAsync("location.enrich", TypeLocation, locationId, userId, revision.SubmittedByUsername,
                $"AI enrichment auto-applied to '{row.Name}' (confidence {suggestion.Confidence:0.00})",
                before, Snapshot(row), save: false);
        }

        await _context.SaveChangesAsync(cancellationToken);
        return eligible ? EnrichmentOutcome.AutoApplied : EnrichmentOutcome.Queued;
    }

    private static Atlas.Location ToAtlas(ServerLocation l) => new()
    {
        Rowid = l.Rowid,
        LocationUuid = l.LocationUuid,
        Name = l.Name ?? string.Empty,
        Description = l.Description,
        Tags = l.Tags,
        Wiki = l.Wiki,
        VideoUrl = l.VideoUrl,
        X = l.X,
        Y = l.Y,
        Z = l.Z,
        Dimension = l.Dimension,
        Warps = l.Warps.Select(warp => new Atlas.Locations.Warp
        {
            Id = warp.Id,
            LocationRowid = warp.LocationRowid,
            Name = warp.Name,
            Source = warp.Source,
            ArchiveX = warp.ArchiveX,
            ArchiveY = warp.ArchiveY,
            ArchiveZ = warp.ArchiveZ,
        }).ToList(),
        Groups = l.LocationGroups.Select(link => new LocationGroupAttribution
        {
            GroupId = link.GroupId,
            GroupName = link.Group.Name,
            Role = link.Role,
        }).ToList(),
        Attachments = l.Attachments.Select(attachment => new Atlas.Locations.Attachment
        {
            Id = attachment.Id,
            LocationRowid = attachment.LocationRowid,
            FileName = attachment.FileName,
            Path = attachment.FilePath,
            MediaType = attachment.MediaType,
            SourceUrl = attachment.SourceUrl,
            Caption = attachment.Caption,
            Attribution = attachment.Attribution,
            DateAddedUtc = attachment.DateAddedUtc,
        }).ToList(),
    };

    private static Dictionary<string, object?> Snapshot(ServerLocation l) => new()
    {
        ["Wiki"] = l.Wiki,
        ["Description"] = l.Description,
        ["Groups"] = l.LocationGroups
            .OrderBy(link => link.GroupId)
            .Select(link => new { link.GroupId, link.Role })
            .ToArray(),
    };

    private static string BuildEvidenceNote(
        string? wikiReason,
        IReadOnlyCollection<GroupAttributionSuggestion> groupSuggestions)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(wikiReason))
            parts.Add(wikiReason);
        foreach (var group in groupSuggestions)
        {
            var revision = group.SourceRevisionId is null ? "unknown revision" : $"revision {group.SourceRevisionId}";
            parts.Add($"Group: {group.GroupName} ({group.Role}) from {group.Evidence}; {revision}; {group.EvidenceUrl}");
        }
        return string.Join(" | ", parts);
    }
}
