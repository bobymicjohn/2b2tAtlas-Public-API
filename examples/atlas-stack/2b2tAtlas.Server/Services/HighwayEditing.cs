using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Atlas;
using Microsoft.EntityFrameworkCore;
using _2b2tAtlas.Server.Models;
using Highway = Atlas.Highway;

namespace _2b2tAtlas.Server.Services;

/// <summary>Validation, attribution and complete history shared by direct and reviewed highway edits.</summary>
public static class HighwayEditing
{
    /// <summary>Hash the persisted state, including relationships, for optimistic concurrency.</summary>
    public static string Version(Models.Highway row) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(HighwayMapping.Snapshot(row)))));

    /// <summary>Reject malformed geometry and values before touching the catalog.</summary>
    public static string? Validate(Highway dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name) || dto.Name.Length > 200) return "Name must contain 1–200 characters.";
        if (!Enum.IsDefined(dto.Dimension) || !Enum.IsDefined(dto.Category) || !Enum.IsDefined(dto.Status) ||
            !Enum.IsDefined(dto.PavingMaterial) || !Enum.IsDefined(dto.Visibility) || !Enum.IsDefined(dto.ReviewStatus))
            return "Unknown highway dimension, category, status or visibility.";
        if (dto.Points is null || dto.Points.Count > 4096 || dto.Points.Any(p => p is null || Math.Abs((long)p.X) > 30_000_000 || Math.Abs((long)p.Z) > 30_000_000))
            return "Use at most 4,096 points within the world's ±30,000,000 block boundary.";
        if (dto.Category is HighwayCategory.Ring or HighwayCategory.DiamondRing)
        {
            if (dto.RingRadius is null or <= 0 or > 30_000_000) return "Ring roads require a positive radius within the world border.";
        }
        else if (dto.Points.Select(p => (p.X, p.Z)).Distinct().Count() < 2)
            return "A highway needs at least two different points.";
        if (dto.Width is < 1 or > 256 || dto.Height is < 1 or > 1024 || dto.YLevel is < -64 or > 320 ||
            dto.DisplayWeight is < 1 or > 20 || dto.LengthBlocks is < 0)
            return "Check width (1–256), clearance (1–1,024), height (-64–320), map weight (1–20) and length.";
        if (dto.Color is not null && !Regex.IsMatch(dto.Color, "^#[0-9a-fA-F]{6}$")) return "Map color must be a six-digit hex color, such as #4da3ff.";
        if (dto.Description?.Length > 20000 || dto.Slug?.Length > 240) return "Highway description or slug is too long.";
        foreach (var url in new[] { dto.WikiUrl, dto.VideoUrl })
            if (!string.IsNullOrWhiteSpace(url) && (url.Length > 2048 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")))
                return "Reference links must be HTTP or HTTPS URLs.";
        if (dto.BuilderGroups is null || dto.BuilderGroups.Count > 50 || dto.BuilderGroups.Any(g => g is null || g.GroupId <= 0 || g.Role?.Length > 100 || g.Evidence?.Length > 2000) ||
            dto.BuilderGroups.Select(g => g.GroupId).Distinct().Count() != dto.BuilderGroups.Count)
            return "Use up to 50 distinct, valid group attributions.";
        return null;
    }

    /// <summary>Identify large or concealment changes; non-owners must ask the owner to apply them.</summary>
    public static List<string> Risks(Highway? before, Highway after)
    {
        List<string> risks = [];
        if (after.Visibility != HighwayVisibility.Public && before?.Visibility != after.Visibility) risks.Add("Hide a highway from the public map");
        if (before is null) return risks;
        if (before.Dimension != after.Dimension) risks.Add("Move a highway to another dimension");
        var oldLength = GeometryLength(before);
        var newLength = GeometryLength(after);
        if (oldLength > 0 && newLength < oldLength * 0.75) risks.Add("Remove more than 25% of the route's geometry");
        if (oldLength > 0 && newLength > oldLength * 4) risks.Add("Expand route length by more than four times");
        if (before.Points.Count > 0 && after.Points.Count > 0)
        {
            var oldX = before.Points.Average(p => (double)p.X); var oldZ = before.Points.Average(p => (double)p.Z);
            var newX = after.Points.Average(p => (double)p.X); var newZ = after.Points.Average(p => (double)p.Z);
            if (Math.Sqrt(Math.Pow(oldX - newX, 2) + Math.Pow(oldZ - newZ, 2)) > Math.Max(4096, oldLength * 0.25))
                risks.Add("Substantially relocate the route");
        }
        if (before.BuilderGroups.Any(g => after.BuilderGroups.All(a => a.GroupId != g.GroupId))) risks.Add("Remove existing group credits");
        if (before.BuilderGroupId.HasValue && after.BuilderGroupId != before.BuilderGroupId) risks.Add("Replace the primary builder credit");
        return risks;
    }

    /// <summary>Also compare against this actor's first edit in the last day, preventing gradual shrink/move bypasses.</summary>
    public static async Task<List<string>> RisksSinceFirstEdit(AtlasContext db, int id, int? userId, Highway before, Highway after)
    {
        var risks = Risks(before, after);
        var since = DateTime.UtcNow.AddDays(-1).ToString("o");
        var baseline = await db.AuditLogs.AsNoTracking().Where(a => a.EntityType == "Highway" && a.EntityId == id &&
            a.UserId == userId && a.Action == "highway.update" && string.Compare(a.CreatedUtc, since) >= 0)
            .OrderBy(a => a.Id).Select(a => a.DetailsJson).FirstOrDefaultAsync();
        if (baseline is not null)
        {
            var first = JsonSerializer.Deserialize<HighwayChange>(baseline)?.Before;
            if (first is not null) risks.AddRange(Risks(first, after));
        }
        return risks.Distinct().ToList();
    }

    private static double GeometryLength(Highway highway) => highway.Category switch
    {
        HighwayCategory.Ring => (highway.RingRadius ?? 0) * 8d,
        HighwayCategory.DiamondRing => (highway.RingRadius ?? 0) * Math.Sqrt(2) * 4d,
        _ => highway.Points.Zip(highway.Points.Skip(1), (a, b) => Math.Sqrt(Math.Pow((double)b.X - a.X, 2) + Math.Pow((double)b.Z - a.Z, 2))).Sum()
    };

    /// <summary>Check every attribution before saving; invalid group IDs never silently disappear.</summary>
    public static async Task<bool> GroupsExist(AtlasContext db, Highway dto)
    {
        var ids = dto.BuilderGroups.Select(g => g.GroupId).Concat(dto.BuilderGroupId is int id ? [id] : Array.Empty<int>()).Distinct().ToArray();
        return await db.Groups.CountAsync(g => ids.Contains(g.Id)) == ids.Length;
    }

    /// <summary>Replace group links exactly, preserving attribution dates on surviving links.</summary>
    public static void ApplyGroups(AtlasContext db, Models.Highway row, Highway dto)
    {
        var requested = dto.BuilderGroups.ToList();
        if (dto.BuilderGroupId is int id && requested.All(g => g.GroupId != id))
            requested.Add(new HighwayGroupAttribution { GroupId = id, Role = "Primary builder" });
        foreach (var old in row.HighwayGroups.Where(g => requested.All(r => r.GroupId != g.GroupId)).ToList())
        { db.HighwayGroups.Remove(old); row.HighwayGroups.Remove(old); }
        foreach (var group in requested)
        {
            var link = row.HighwayGroups.FirstOrDefault(g => g.GroupId == group.GroupId);
            if (link is null)
            {
                link = new HighwayGroup { Highway = row, GroupId = group.GroupId, DateAddedUtc = DateTime.UtcNow.ToString("o") };
                row.HighwayGroups.Add(link);
            }
            link.Role = string.IsNullOrWhiteSpace(group.Role) ? "Contributor" : group.Role.Trim();
            link.Evidence = string.IsNullOrWhiteSpace(group.Evidence) ? null : group.Evidence.Trim();
        }
    }

    /// <summary>Write full before/after state within the caller's transaction.</summary>
    public static async Task Record(AuditService audit, string action, int id, int? userId, string? username,
        Highway? before, Highway? after, List<string>? warnings = null, Highway? proposed = null)
    {
        var oldJson = JsonSerializer.SerializeToElement(before); var newJson = JsonSerializer.SerializeToElement(after);
        var fields = typeof(Highway).GetProperties().Where(p => p.Name is not ("EditVersion" or "ApiUrl" or "MapUrl" or "BuilderGroupName" or "LastEditedByUserId"))
            .Where(p => before is null || after is null || oldJson.GetProperty(p.Name).GetRawText() != newJson.GetProperty(p.Name).GetRawText()).Select(p => p.Name).ToList();
        var summary = $"{after?.Name ?? before?.Name}: {string.Join(", ", fields)}";
        if (warnings?.Count > 0) summary = "ATTENTION: " + string.Join("; ", warnings) + ". " + summary;
        await audit.LogAsync(action, "Highway", id, userId, username, summary, JsonSerializer.Serialize(new HighwayChange
        { Before = before, After = after, Proposed = proposed, Fields = fields, Warnings = warnings ?? [] }));
    }
}
