using System.Text.Json;
using Atlas;
using ServerHighway = _2b2tAtlas.Server.Models.Highway;

namespace _2b2tAtlas.Server.Services;

/// <summary>
/// Shared mapping between the <see cref="Atlas.Highway"/> DTO and the persisted
/// <see cref="ServerHighway"/> entity. Used by both direct edits (HighwaysController)
/// and approved revisions (RevisionsController) so the two paths never drift.
/// </summary>
public static class HighwayMapping
{
    /// <summary>Copies editable DTO fields onto the entity (never touches Id/audit/created).</summary>
    public static void Apply(Atlas.Highway dto, ServerHighway row)
    {
        row.Name = dto.Name;
        if (!string.IsNullOrWhiteSpace(dto.Slug)) row.Slug = dto.Slug;
        row.Dimension = (int)dto.Dimension;
        row.Category = dto.Category.ToString();
        row.PointsJson = SerializePoints(dto.Points);
        row.RingRadius = dto.RingRadius;
        row.Width = dto.Width;
        row.Height = dto.Height;
        row.YLevel = dto.YLevel;
        row.Paved = dto.Paved ? 1 : 0;
        row.PavingMaterial = dto.PavingMaterial.ToString();
        row.Walls = dto.Walls ? 1 : 0;
        row.Enclosed = dto.Enclosed ? 1 : 0;
        row.IsRoofHighway = dto.IsRoofHighway ? 1 : 0;
        row.Lit = dto.Lit ? 1 : 0;
        row.Status = dto.Status.ToString();
        row.BuilderGroupId = dto.BuilderGroupId;
        row.LengthBlocks = dto.LengthBlocks;
        row.Description = dto.Description;
        row.WikiUrl = dto.WikiUrl;
        row.VideoUrl = dto.VideoUrl;
        row.Color = dto.Color;
        row.DisplayWeight = dto.DisplayWeight;
        row.Visibility = dto.Visibility.ToString();
        row.LastVerifiedUtc = dto.LastVerifiedUtc?.ToString("o");
    }

    /// <summary>Field snapshot used to compute before→after audit diffs.</summary>
    public static Dictionary<string, object?> Snapshot(ServerHighway row) => new()
    {
        ["Name"] = row.Name,
        ["Slug"] = row.Slug,
        ["Dimension"] = row.Dimension,
        ["LengthBlocks"] = row.LengthBlocks,
        ["DisplayWeight"] = row.DisplayWeight,
        ["LastVerifiedUtc"] = row.LastVerifiedUtc,
        ["ReviewStatus"] = row.ReviewStatus,
        ["DateAddedUtc"] = row.DateAddedUtc,
        ["CreatedByUserId"] = row.CreatedByUserId,
        ["LastEditedByUserId"] = row.LastEditedByUserId,
        ["BuilderGroups"] = JsonSerializer.Serialize(row.HighwayGroups.OrderBy(g => g.GroupId).Select(g => new { g.GroupId, g.Role, g.Evidence, g.DateAddedUtc })),
        ["Category"] = row.Category,
        ["PointsJson"] = row.PointsJson,
        ["RingRadius"] = row.RingRadius,
        ["Width"] = row.Width,
        ["Height"] = row.Height,
        ["YLevel"] = row.YLevel,
        ["Paved"] = row.Paved,
        ["PavingMaterial"] = row.PavingMaterial,
        ["Walls"] = row.Walls,
        ["Enclosed"] = row.Enclosed,
        ["IsRoofHighway"] = row.IsRoofHighway,
        ["Lit"] = row.Lit,
        ["Status"] = row.Status,
        ["BuilderGroupId"] = row.BuilderGroupId,
        ["Description"] = row.Description,
        ["WikiUrl"] = row.WikiUrl,
        ["VideoUrl"] = row.VideoUrl,
        ["Color"] = row.Color,
        ["Visibility"] = row.Visibility,
    };

    /// <summary>Serializes ordered highway vertices to the compact SQLite JSON coordinate representation.</summary>
    /// <param name="points">Vertices expressed as native-dimension block X/Z pairs.</param>
    /// <returns>A JSON array of <c>[x,z]</c> arrays preserving path order.</returns>
    public static string SerializePoints(List<HighwayPoint> points) =>
        JsonSerializer.Serialize(points.Select(p => new[] { p.X, p.Z }).ToArray());

    /// <summary>Creates a stable lowercase URL key from a highway display name.</summary>
    /// <param name="name">The human-readable Atlas highway name.</param>
    /// <returns>An alphanumeric, hyphen-separated slug with repeated separators collapsed.</returns>
    public static string Slugify(string name)
    {
        var chars = name.ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray();
        var slug = new string(chars);
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Trim('-');
    }
    /// <summary>Maps persisted highway state and group credits to the client contract.</summary>
    public static Atlas.Highway ToDto(ServerHighway row)
    {
        var attributions = row.HighwayGroups
            .OrderBy(link => link.Role.Contains("Primary", StringComparison.OrdinalIgnoreCase) ||
                             link.Role.Contains("Current", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(link => link.Group?.Name)
            .Select(link => new HighwayGroupAttribution
            {
                GroupId = link.GroupId,
                GroupUrl = PublicAtlasUrls.Group(link.GroupId),
                GroupApiUrl = PublicAtlasUrls.GroupApi(link.GroupId),
                GroupName = link.Group?.Name ?? "",
                Role = link.Role,
                Evidence = link.Evidence,
            })
            .ToList();
        var primary = attributions.FirstOrDefault(link => link.GroupId == row.BuilderGroupId) ?? attributions.FirstOrDefault();
        return new Atlas.Highway
        {
        Id = row.Id,
        EditVersion = HighwayEditing.Version(row),
        ApiUrl = PublicAtlasUrls.HighwayApi(row.Id),
        MapUrl = PublicAtlasUrls.HighwayMap(row.Dimension),
        Name = row.Name,
        Slug = row.Slug,
        Dimension = (Dimension)row.Dimension,
        Category = ParseEnum(row.Category, HighwayCategory.Custom),
        Points = ParsePoints(row.PointsJson),
        RingRadius = row.RingRadius,
        Width = row.Width,
        Height = row.Height,
        YLevel = row.YLevel,
        Paved = row.Paved == 1,
        PavingMaterial = ParseEnum(row.PavingMaterial, PavingMaterial.Unknown),
        Walls = row.Walls == 1,
        Enclosed = row.Enclosed == 1,
        IsRoofHighway = row.IsRoofHighway == 1,
        Lit = row.Lit == 1,
        Status = ParseEnum(row.Status, HighwayStatus.Unknown),
        BuilderGroupId = row.BuilderGroupId,
        BuilderGroupName = primary?.GroupName,
        BuilderGroups = attributions,
        LengthBlocks = row.LengthBlocks,
        Description = row.Description,
        WikiUrl = row.WikiUrl,
        VideoUrl = row.VideoUrl,
        Color = row.Color,
        DisplayWeight = row.DisplayWeight,
        Visibility = ParseEnum(row.Visibility, HighwayVisibility.Public),
        ReviewStatus = ParseEnum(row.ReviewStatus, ReviewStatus.Approved),
        CreatedByUserId = row.CreatedByUserId,
        LastEditedByUserId = row.LastEditedByUserId,
        DateAddedUtc = DateTime.TryParse(row.DateAddedUtc, out var added) ? added : DateTime.MinValue,
        LastVerifiedUtc = DateTime.TryParse(row.LastVerifiedUtc, out var verified) ? verified : null,
        };
    }

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback) where TEnum : struct =>
        Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) ? parsed : fallback;


    private static List<HighwayPoint> ParsePoints(string json)
    {
        try
        {
            var raw = JsonSerializer.Deserialize<int[][]>(json);
            if (raw == null) return new();
            return raw.Where(p => p.Length >= 2).Select(p => new HighwayPoint(p[0], p[1])).ToList();
        }
        catch
        {
            return new();
        }
    }


}
