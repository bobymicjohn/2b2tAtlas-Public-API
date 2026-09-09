namespace Atlas.Enrichment;

/// <summary>
/// An AI-assisted wiki match and draft description for a single Atlas location. It is produced by the
/// server's local enrichment engine for an operator to review — either before it is saved (interactive
/// "Generate description") or after it is auto-applied (the review queue). It carries the evidence
/// (coordinate agreement, confidence, source attribution) needed to trust or revert the suggestion.
/// </summary>
public sealed class WikiEnrichmentSuggestion
{
    /// <summary>Gets or sets the target location row id.</summary>
    public int LocationId { get; set; }

    /// <summary>Gets or sets the target location name at generation time.</summary>
    public string LocationName { get; set; } = string.Empty;

    /// <summary>Gets or sets the matched wiki page title, or null when no confident page was found.</summary>
    public string? WikiTitle { get; set; }

    /// <summary>Gets or sets the matched wiki page URL, or null when no page was found.</summary>
    public string? WikiUrl { get; set; }

    /// <summary>Gets or sets the overall match confidence from 0 (none) to 1 (name and coordinates agree).</summary>
    public double Confidence { get; set; }

    /// <summary>Gets or sets whether the wiki page's coordinates matched the location within tolerance.</summary>
    public bool CoordinatesAgree { get; set; }

    /// <summary>Gets or sets the closest Overworld-scale distance between wiki and location coordinates, or -1.</summary>
    public long CoordinateDistanceBlocks { get; set; }

    /// <summary>Gets or sets a short, human-readable explanation of why this page was matched.</summary>
    public string? MatchReason { get; set; }

    /// <summary>Gets or sets the drafted description, or null when generation was skipped or empty.</summary>
    public string? SuggestedDescription { get; set; }

    /// <summary>Gets or sets the source attribution line for the description (wiki URL + license).</summary>
    public string? Attribution { get; set; }

    /// <summary>Gets or sets whether this suggestion cleared the auto-apply confidence and coordinate gates.</summary>
    public bool AutoApplyEligible { get; set; }

    /// <summary>
    /// Gets or sets evidence-backed group/build relationships found in the revision-pinned group audit.
    /// Incidental wiki mentions are never included.
    /// </summary>
    public List<GroupAttributionSuggestion> SuggestedGroups { get; set; } = [];

    /// <summary>Gets or sets the ISO-8601 UTC time the suggestion was generated.</summary>
    public string GeneratedUtc { get; set; } = string.Empty;
}

/// <summary>A reviewable group/build relationship derived from explicit, revision-pinned source evidence.</summary>
public sealed class GroupAttributionSuggestion
{
    /// <summary>Gets or sets the existing Atlas group identifier.</summary>
    public int GroupId { get; set; }

    /// <summary>Gets or sets the canonical Atlas group name.</summary>
    public string GroupName { get; set; } = string.Empty;

    /// <summary>Gets or sets the proposed relationship role.</summary>
    public string Role { get; set; } = "Builder";

    /// <summary>Gets or sets the source article URL.</summary>
    public string EvidenceUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the pinned MediaWiki revision identifier.</summary>
    public long? SourceRevisionId { get; set; }

    /// <summary>Gets or sets the explicit evidence type, such as an infobox base list.</summary>
    public string Evidence { get; set; } = string.Empty;

    /// <summary>Gets or sets the deterministic relationship confidence.</summary>
    public double Confidence { get; set; }

    /// <summary>Gets or sets whether the relationship meets the strict automatic-application policy.</summary>
    public bool AutoApplyEligible { get; set; }
}
