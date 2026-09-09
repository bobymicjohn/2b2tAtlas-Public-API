namespace _2b2tAtlas.Server.Models;

/// <summary>
/// An immutable record of a canonical mutation (GAMEPLAN §15) — who did what,
/// when, and an optional before→after detail payload. Created by the
/// <c>SchemaUpgrader</c> table; never updated after insert.
/// </summary>
public class AuditLog
{
    /// <summary>Gets or sets the database-generated sequence identifier used for newest-first ordering.</summary>
    public int Id { get; set; }

    /// <summary>Dotted action key, e.g. "highway.create", "highway.approve".</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Entity type affected, e.g. "Highway".</summary>
    public string EntityType { get; set; } = string.Empty;

    /// <summary>Affected entity id.</summary>
    public int EntityId { get; set; }

    /// <summary>Acting user id (null for system).</summary>
    public int? UserId { get; set; }

    /// <summary>Acting username snapshot.</summary>
    public string? Username { get; set; }

    /// <summary>Short human-readable summary.</summary>
    public string? Summary { get; set; }

    /// <summary>Optional JSON detail (before/after or payload).</summary>
    public string? DetailsJson { get; set; }

    /// <summary>ISO 8601 timestamp.</summary>
    public string CreatedUtc { get; set; } = DateTime.UtcNow.ToString("o");
}
