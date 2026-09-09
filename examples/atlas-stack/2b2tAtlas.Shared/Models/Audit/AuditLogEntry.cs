namespace Atlas;

/// <summary>
/// A read-only audit-log entry (GAMEPLAN §15) exposed via the API.
/// </summary>
public class AuditLogEntry
{
    /// <summary>Gets or sets the audit entry's persistent identifier.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the machine-readable action name, such as <c>highway.update</c>.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Gets or sets the kind of entity affected by the action.</summary>
    public string EntityType { get; set; } = string.Empty;

    /// <summary>Gets or sets the persistent identifier of the affected entity.</summary>
    public int EntityId { get; set; }

    /// <summary>Gets or sets the acting user's identifier, or <see langword="null"/> for a system action.</summary>
    public int? UserId { get; set; }

    /// <summary>Gets or sets the acting user's display name when available.</summary>
    public string? Username { get; set; }

    /// <summary>Gets or sets a human-readable summary of the action.</summary>
    public string? Summary { get; set; }

    /// <summary>Gets or sets optional action-specific details serialized as a JSON value.</summary>
    public string? DetailsJson { get; set; }

    /// <summary>Gets or sets the UTC time at which the action was recorded.</summary>
    public DateTime CreatedUtc { get; set; }
}
