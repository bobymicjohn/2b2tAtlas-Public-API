namespace _2b2tAtlas.Server.Models;

/// <summary>
/// A proposed edit to an existing Location or Highway (GAMEPLAN §15). Created by
/// contributors without direct edit rights; a moderator approves (applies the
/// proposed payload + audits) or rejects. The physical table is created by
/// <c>SchemaUpgrader</c> (EnsureCreated won't add it to an existing database).
/// </summary>
public class Revision
{
    /// <summary>Gets or sets the database-generated proposal identifier.</summary>
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

    /// <summary>Pending / Approved / Rejected / Applied / Reverted.</summary>
    public string Status { get; set; } = "Pending";

    /// <summary>Gets or sets the authenticated contributor who submitted the proposal.</summary>
    public int? SubmittedByUserId { get; set; }

    /// <summary>Gets or sets the submitter username snapshot retained for moderation history.</summary>
    public string? SubmittedByUsername { get; set; }

    /// <summary>Gets or sets the trusted moderator who approved or rejected the proposal.</summary>
    public int? ReviewedByUserId { get; set; }

    /// <summary>Gets or sets an optional moderator explanation, primarily for rejected proposals.</summary>
    public string? ReviewNote { get; set; }

    /// <summary>Gets or sets the UTC submission timestamp in round-trip text form.</summary>
    public string SubmittedUtc { get; set; } = DateTime.UtcNow.ToString("o");

    /// <summary>Gets or sets the UTC timestamp at which a moderator made the terminal decision.</summary>
    public string? ReviewedUtc { get; set; }
}
