using System.Text.Json;
using _2b2tAtlas.Server.Models;

namespace _2b2tAtlas.Server.Services;

/// <summary>
/// Writes immutable audit-log entries for canonical mutations (GAMEPLAN §15).
/// </summary>
public class AuditService
{
    private readonly AtlasContext _context;

    /// <summary>Initializes the audit writer over the current Atlas unit of work.</summary>
    /// <param name="context">The context that receives immutable audit entities and optionally persists them.</param>
    public AuditService(AtlasContext context)
    {
        _context = context;
    }

    /// <summary>Records an action. Does not call SaveChanges when <paramref name="save"/> is false.</summary>
    public async Task LogAsync(string action, string entityType, int entityId, int? userId, string? username,
        string? summary = null, string? detailsJson = null, bool save = true)
    {
        _context.AuditLogs.Add(new AuditLog
        {
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            UserId = userId,
            Username = username,
            Summary = summary,
            DetailsJson = detailsJson,
            CreatedUtc = DateTime.UtcNow.ToString("o"),
        });
        if (save) await _context.SaveChangesAsync();
    }

    /// <summary>
    /// Records a mutation with a field-level before→after diff. Only fields whose
    /// value changed are persisted, as <c>{ field: { before, after } }</c> JSON.
    /// </summary>
    public async Task LogDiffAsync(string action, string entityType, int entityId, int? userId, string? username,
        string? summary, IReadOnlyDictionary<string, object?> before, IReadOnlyDictionary<string, object?> after,
        bool save = true)
    {
        var changes = new Dictionary<string, object?>();
        foreach (var key in before.Keys.Union(after.Keys))
        {
            before.TryGetValue(key, out var oldValue);
            after.TryGetValue(key, out var newValue);
            if (!Equals(oldValue, newValue))
                changes[key] = new { before = oldValue, after = newValue };
        }

        var detailsJson = changes.Count > 0 ? JsonSerializer.Serialize(changes) : null;
        await LogAsync(action, entityType, entityId, userId, username, summary, detailsJson, save);
    }
}
