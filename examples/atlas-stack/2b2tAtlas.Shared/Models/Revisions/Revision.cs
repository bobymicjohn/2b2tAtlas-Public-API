namespace Atlas;

/// <summary>
/// Review state of a proposed edit to an existing entity (GAMEPLAN §15).
/// </summary>
public enum RevisionStatus
{
    /// <summary>The proposal is awaiting moderator review.</summary>
    Pending = 0,

    /// <summary>The proposal was accepted and applied to its target entity.</summary>
    Approved = 1,

    /// <summary>The proposal was rejected without changing its target entity.</summary>
    Rejected = 2,

    /// <summary>The change was auto-applied (e.g. by AI enrichment) and awaits reviewer confirmation.</summary>
    Applied = 3,

    /// <summary>A previously auto-applied change was undone, restoring the prior values.</summary>
    Reverted = 4,
}

/// <summary>
/// A proposed edit to an existing Location or Highway, submitted by a contributor
/// who lacks direct edit rights. Moderators approve (apply + audit) or reject.
/// </summary>
public class RevisionDto
{
    /// <summary>Gets or sets the revision's persistent identifier.</summary>
    public int Id { get; set; }

    /// <summary>Target entity type: "Location" or "Highway".</summary>
    public string EntityType { get; set; } = string.Empty;

    /// <summary>Target entity id.</summary>
    public int EntityId { get; set; }

    /// <summary>Proposed full entity payload (JSON of the Atlas DTO).</summary>
    public string ProposedJson { get; set; } = string.Empty;

    /// <summary>Optional contributor note explaining the change.</summary>
    public string? Note { get; set; }

    /// <summary>Origin of the proposal: "User" for human submissions or "AI" for enrichment-generated ones.</summary>
    public string? Source { get; set; }

    /// <summary>For AI proposals, the match confidence (0..1) that produced this change; null for human edits.</summary>
    public double? Confidence { get; set; }

    /// <summary>The target entity payload (JSON) captured before an auto-applied change, enabling revert.</summary>
    public string? PreviousJson { get; set; }

    /// <summary>Gets or sets the revision's moderation state.</summary>
    public RevisionStatus Status { get; set; } = RevisionStatus.Pending;

    /// <summary>Gets or sets the identifier of the contributor who submitted the revision.</summary>
    public int? SubmittedByUserId { get; set; }

    /// <summary>Gets or sets the contributor name captured for moderation display and audit history.</summary>
    public string? SubmittedByUsername { get; set; }

    /// <summary>Gets or sets the identifier of the moderator who reviewed the revision.</summary>
    public int? ReviewedByUserId { get; set; }

    /// <summary>Moderator reason when rejected.</summary>
    public string? ReviewNote { get; set; }

    /// <summary>Gets or sets the UTC time at which the revision was submitted.</summary>
    public DateTime SubmittedUtc { get; set; }

    /// <summary>Gets or sets the UTC time at which the revision was approved or rejected.</summary>
    public DateTime? ReviewedUtc { get; set; }
}
