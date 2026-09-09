using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using _2b2tAtlas.Server.Models;

namespace _2b2tAtlas.Server.Services;

/// <summary>Applies reviewed build naming conventions independently of AI and wiki availability.</summary>
public sealed partial class ArchiveGroupAttributionService(AtlasContext context)
{
    /// <summary>A group attribution and the exact catalog evidence supporting it.</summary>
    public sealed record Match(string GroupName, string Evidence);

    /// <summary>
    /// Matches complete owner tags and anchored build families. Never infers ownership from a
    /// description, uploader host, a player's membership, or an incidental substring.
    /// </summary>
    public static IReadOnlyList<Match> FindMatches(string? locationName, IEnumerable<string?> warpNames)
    {
        var matches = new Dictionary<string, Match>(StringComparer.OrdinalIgnoreCase);
        void Add(string group, string evidence) => matches.TryAdd(group, new(group, evidence));
        void Family(string? value, string source)
        {
            var name = Normalize(value);
            if (SbaPrefix().IsMatch(name)) Add("Spawn Builders Association", $"{source}: {value}");
            if (SpawnMasonPrefix().IsMatch(name)) Add("SpawnMasons", $"{source}: {value}");
            if (DonFuerPrefix().IsMatch(name)) Add("DonFuer", $"{source}: {value}");
            if (NerdsPrefix().IsMatch(name)) Add("Nerds Inc", $"{source}: {value}");
            if (name == "krizz sba initial base" || name.StartsWith("krizz sba initial base ", StringComparison.Ordinal))
                Add("Spawn Builders Association", $"reviewed named SBA build: {value}");
        }
        foreach (var warp in warpNames.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var parts = warp!.Trim().Split('@');
            Family(warp, "warp name family");
            // Dimension and capture annotations may follow the owner: @Spawnmason_lodge@End.
            // Match an entire tag; @NotSpawnmasons and @SBA_Fan are not ownership evidence.
            foreach (var tag in parts.Skip(1).Select(Normalize))
            {
                var owner = tag switch
                {
                    "spawnmason" or "spawnmasons" or "spawn mason" or "spawn masons" or
                    "spawnmason lodge" or "spawnmasons lodge" or "spawn mason lodge" or "spawn masons lodge" => "SpawnMasons",
                    "sba" or "spawn builders association" => "Spawn Builders Association",
                    "emperium" => "The Emperium",
                    "imperator's base" => "Imperator's Group",
                    "nerds inc" or "nerds inc." or "nerdsinc" => "Nerds Inc",
                    _ => null
                };
                if (owner is not null) Add(owner, $"explicit Archive owner tag @{tag}: {warp}");
            }
        }
        Family(locationName, "location name family");
        return matches.Values.OrderBy(match => match.GroupName, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Stages additive relationships and their audit evidence for one persisted location or the whole
    /// catalog. The caller saves/commits them with ingestion, enrichment, or startup backfill.
    /// Existing roles and other group credits remain authoritative. Repeated calls are idempotent.
    /// </summary>
    public async Task<int> StageAsync(int? locationId = null, CancellationToken cancellationToken = default)
    {
        var groups = (await context.Groups.AsNoTracking().ToListAsync(cancellationToken))
            .GroupBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);
        var locations = await context.Locations.Where(row => !locationId.HasValue || row.Rowid == locationId.Value)
            .ToListAsync(cancellationToken);
        var warps = (await context.Warps.AsNoTracking()
            .Where(warp => warp.LocationRowid.HasValue && (!locationId.HasValue || warp.LocationRowid == locationId.Value))
            .Select(warp => new { warp.LocationRowid, warp.Name }).ToListAsync(cancellationToken))
            .ToLookup(warp => warp.LocationRowid!.Value, warp => warp.Name);
        var existing = (await context.LocationGroups.AsNoTracking()
            .Where(link => !locationId.HasValue || link.LocationRowid == locationId.Value)
            .ToListAsync(cancellationToken)).Select(link => (link.LocationRowid, link.GroupId)).ToHashSet();
        existing.UnionWith(context.LocationGroups.Local.Select(link => (link.LocationRowid, link.GroupId)));
        var now = DateTime.UtcNow.ToString("o");
        var added = 0;
        foreach (var location in locations)
        {
            var evidence = new List<object>();
            foreach (var match in FindMatches(location.Name, warps[location.Rowid]))
            {
                if (!groups.TryGetValue(match.GroupName, out var group) || !existing.Add((location.Rowid, group.Id))) continue;
                context.LocationGroups.Add(new LocationGroup
                {
                    LocationRowid = location.Rowid, GroupId = group.Id, Role = "Builder", DateAddedUtc = now
                });
                evidence.Add(new { groupId = group.Id, groupName = group.Name, role = "Builder", match.Evidence });
                added++;
            }
            if (evidence.Count == 0) continue;
            location.ModifiedUtc = now;
            context.AuditLogs.Add(new AuditLog
            {
                Action = "location.group.archive", EntityType = "Location", EntityId = location.Rowid,
                Username = "Archive group attribution", CreatedUtc = now,
                Summary = $"Added {evidence.Count} group credit(s) from reviewed naming evidence for '{location.Name}'",
                DetailsJson = JsonSerializer.Serialize(new { ruleVersion = "2026-09-07.1", added = evidence })
            });
        }
        return added;
    }

    private static string Normalize(string? value) => Whitespace().Replace(
        (value ?? "").Trim().ToLowerInvariant().Replace('_', ' ').Replace('-', ' '), " ");

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Whitespace();
    [GeneratedRegex(@"^sba(?:\s|:|$)", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SbaPrefix();
    [GeneratedRegex(@"^spawn\s?masons?(?:\s|:|$)", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SpawnMasonPrefix();
    [GeneratedRegex(@"^(?:trost gate )?don\s?fuer(?:\s|[0-9]|:|$)", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex DonFuerPrefix();
    [GeneratedRegex(@"^nerds\s?inc\.?(?:\s|:|$)", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex NerdsPrefix();
}
