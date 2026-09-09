namespace Atlas.Enrichment;

/// <summary>A review-only proposal for a wiki group not yet represented in Atlas.</summary>
public sealed class GroupDiscoverySuggestion
{
    /// <summary>Source MediaWiki page identifier.</summary>
    public int PageId { get; set; }
    /// <summary>Canonical proposed group name.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Evidence-derived group classification.</summary>
    public GroupType Type { get; set; } = GroupType.Other;
    /// <summary>Public human-readable evidence page.</summary>
    public string WikiUrl { get; set; } = string.Empty;
    /// <summary>Pinned MediaWiki revision used to produce the proposal.</summary>
    public long? SourceRevisionId { get; set; }
    /// <summary>Plain-text source introduction retained for moderator context.</summary>
    public string? Intro { get; set; }
    /// <summary>Optional model-drafted summary, never published without review.</summary>
    public string? SuggestedDescription { get; set; }
    /// <summary>Reported founding date or period.</summary>
    public string? Founded { get; set; }
    /// <summary>Reported activity status.</summary>
    public string? Status { get; set; }
    /// <summary>Candidate public logo asset.</summary>
    public string? LogoUrl { get; set; }
    /// <summary>Evidence page for the candidate logo.</summary>
    public string? LogoSourceUrl { get; set; }
    /// <summary>Candidate official website; unverified values remain unset.</summary>
    public string? WebsiteUrl { get; set; }
    /// <summary>Candidate official Discord invite; unverified values remain unset.</summary>
    public string? DiscordUrl { get; set; }
    /// <summary>Explicit existing Atlas builds requiring relationship review.</summary>
    public List<GroupDiscoveryLocation> Locations { get; set; } = [];
}

/// <summary>An explicit Atlas build candidate attached to a group-discovery proposal.</summary>
public sealed class GroupDiscoveryLocation
{
    /// <summary>Existing Atlas location identifier.</summary>
    public int LocationId { get; set; }
    /// <summary>Existing Atlas location name.</summary>
    public string LocationName { get; set; } = string.Empty;
    /// <summary>Proposed relationship role.</summary>
    public string Role { get; set; } = "Builder";
    /// <summary>Human-readable deterministic evidence for the candidate edge.</summary>
    public string Evidence { get; set; } = string.Empty;
}

/// <summary>The atomic moderation payload for creating one group and its explicit build links.</summary>
public sealed class GroupDiscoveryProposal
{
    /// <summary>Reviewed group record to create.</summary>
    public Group Group { get; set; } = new();
    /// <summary>Reviewed build relationships to create atomically with the group.</summary>
    public List<GroupDiscoveryLocation> Locations { get; set; } = [];
    /// <summary>Public evidence URL retained in the moderation audit.</summary>
    public string EvidenceUrl { get; set; } = string.Empty;
    /// <summary>Pinned source revision retained in the moderation audit.</summary>
    public long? SourceRevisionId { get; set; }
}

/// <summary>Options for generating review-only group discovery revisions.</summary>
public sealed class GroupDiscoveryRunRequest
{
    /// <summary>Maximum number of new group proposals to enqueue.</summary>
    public int MaxGroups { get; set; } = 10;
    /// <summary>Whether the local model may draft review-only summaries.</summary>
    public bool DraftDescriptions { get; set; } = true;
}

/// <summary>Summary of a group discovery run.</summary>
public sealed class GroupDiscoveryRunSummary
{
    /// <summary>Whether discovery was executed.</summary>
    public bool Ran { get; set; }
    /// <summary>Optional execution or validation message.</summary>
    public string? Message { get; set; }
    /// <summary>Number of eligible candidates found.</summary>
    public int Candidates { get; set; }
    /// <summary>Number of moderation revisions queued.</summary>
    public int Queued { get; set; }
    /// <summary>Number of candidates skipped by policy or deduplication.</summary>
    public int Skipped { get; set; }
}
