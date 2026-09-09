using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using _2b2tAtlas.Server.Models;

namespace _2b2tAtlas.Server.Services;

/// <summary>
/// Seeds the canonical 2b2t highway network (axes, diagonals, ring + diamond-ring
/// roads, the Nether Star, and the 50k grid) into the <c>Highways</c> table on an
/// empty table. Geometry is generated the same way the client renders it
/// (Nether-coordinate units), so the DB matches the map. Attributes can then be
/// enriched via the editor (GAMEPLAN §14). Idempotent: skips if any rows exist.
/// </summary>
public class HighwaySeeder
{
    private const int DimNether = (int)Atlas.Dimension.Nether;

    private readonly AtlasContext _context;
    private readonly ILogger<HighwaySeeder> _logger;

    /// <summary>Initializes canonical highway synchronization.</summary>
    /// <param name="context">The Atlas context containing persisted highway geometry.</param>
    /// <param name="logger">The logger for newly inserted canonical routes.</param>
    public HighwaySeeder(AtlasContext context, ILogger<HighwaySeeder> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>Inserts canonical highways whose unique slugs are not already present.</summary>
    /// <returns>A task that completes after missing routes are persisted.</returns>
    /// <remarks>
    /// Generated Nether vertices use Nether block coordinates; End axes use End block coordinates.
    /// Existing rows, including operator enrichment, are never overwritten.
    /// </remarks>
    public async Task SeedAsync()
    {
        var groups = await _context.Groups.ToDictionaryAsync(group => group.Name, StringComparer.OrdinalIgnoreCase);
        var waterWayUnionId = groups.GetValueOrDefault("WaterWay Union")?.Id;
        var hwuId = groups.GetValueOrDefault("Highway Workers Union (HWU)")?.Id;
        var highways = BuildCanonicalHighways(waterWayUnionId, hwuId);
        var existingRows = await _context.Highways.ToDictionaryAsync(highway => highway.Slug, StringComparer.OrdinalIgnoreCase);
        var missing = highways.Where(highway => !existingRows.ContainsKey(highway.Slug)).ToList();
        if (missing.Count > 0)
        {
            await _context.Highways.AddRangeAsync(missing);
            await _context.SaveChangesAsync();
        }

        // Correct the two reviewed Southern Canal facts without overwriting unrelated
        // administrator edits on canonical highways. The previous seed was 1,000 blocks
        // and a thin display weight; both values are now known to be wrong.
        var borderCanal = await _context.Highways.FirstOrDefaultAsync(highway =>
            highway.Slug == "southern-canal-world-border-segment");
        if (borderCanal is not null)
        {
            borderCanal.DisplayWeight = Math.Max(borderCanal.DisplayWeight ?? 0, 12);
            if (borderCanal.LengthBlocks is null or <= 1_000)
            {
                borderCanal.PointsJson = JsonSerializer.Serialize(new[] { new[] { 0, 30_000_000 }, new[] { 0, 29_988_000 } });
                borderCanal.LengthBlocks = 12_000;
                borderCanal.Description = "Separate Southern Canal excavation dug at least 12,000 blocks northward from the +Z Overworld world border. It is intentionally mapped separately from the spawnward canal because the intervening route is not documented as complete.";
                borderCanal.LastVerifiedUtc = "2026-09-03T00:00:00Z";
            }
        }
        var spawnwardCanal = await _context.Highways.FirstOrDefaultAsync(highway =>
            highway.Slug == "southern-canal-spawnward-segment");
        if (spawnwardCanal is not null)
        {
            spawnwardCanal.DisplayWeight = Math.Max(spawnwardCanal.DisplayWeight ?? 0, 12);
            if (string.IsNullOrWhiteSpace(spawnwardCanal.Description) ||
                spawnwardCanal.Description.StartsWith("Main +Z Overworld canal from the spawn region", StringComparison.Ordinal))
                spawnwardCanal.Description = "Main +Z Overworld canal from the spawn region to approximately Z +1.1M. The route was started by independent builders, extended by several infrastructure groups, taken to roughly 540k by the Southern Canal Association, and subsequently widened to 32 blocks and extended by WaterWay Union. This is not connected to the separate world-border excavation.";
        }
        await _context.SaveChangesAsync();

        var persisted = await _context.Highways.ToDictionaryAsync(highway => highway.Slug, StringComparer.OrdinalIgnoreCase);
        var linked = await SeedAttributionsAsync(persisted, groups);
        var routesWithGroups = await _context.Highways.Include(highway => highway.HighwayGroups).ToListAsync();
        foreach (var route in routesWithGroups.Where(route => !route.BuilderGroupId.HasValue))
        {
            route.BuilderGroupId = route.HighwayGroups
                .OrderBy(link => link.Role.Contains("Primary", StringComparison.OrdinalIgnoreCase) ||
                                 link.Role.Contains("Current", StringComparison.OrdinalIgnoreCase) ? 0 :
                                 link.Role.Equals("Builder", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                .Select(link => (int?)link.GroupId)
                .FirstOrDefault();
        }
        await _context.SaveChangesAsync();
        _logger.LogInformation("Highway seed: {Missing} route(s) added and {Linked} group attribution(s) added.", missing.Count, linked);
    }

    /// <summary>Builds the full canonical highway set (matches atlas-map.js HIGHWAYS).</summary>
    private static List<Highway> BuildCanonicalHighways(int? waterWayUnionId = null, int? hwuId = null)
    {
        const int reach = 30_000_000; // axis/diagonal reach in Nether units
        var list = new List<Highway>();

        // Axis highways (obsidian, width 6) — run to ±30M Nether units.
        AddAxis(list, "+X Highway", 0, 0, reach, 0);
        AddAxis(list, "-X Highway", 0, 0, -reach, 0);
        AddAxis(list, "+Z Highway", 0, 0, 0, reach);
        AddAxis(list, "-Z Highway", 0, 0, 0, -reach);

        // Diagonal highways (obsidian, width 3).
        AddDiagonal(list, "+X,+Z Diagonal Highway", reach, reach);
        AddDiagonal(list, "+X,-Z Diagonal Highway", reach, -reach);
        AddDiagonal(list, "-X,+Z Diagonal Highway", -reach, reach);
        AddDiagonal(list, "-X,-Z Diagonal Highway", -reach, -reach);

        // Square ring roads: [name, radius, width].
        var rings = new (string Name, int R, int W)[]
        {
            ("World Border Ring Road", 3750000, 6), ("2.5m Ring Road", 2500000, 6),
            ("1.875m Ring Road", 1875000, 6), ("Farlands Ring Road", 1568852, 6),
            ("1.25m Ring Road", 1250000, 6), ("1m Ring Road", 1000000, 6),
            ("750k Ring Road", 750000, 6), ("500k Ring Road", 500000, 6),
            ("250k Ring Road", 250000, 6), ("125k Ring Road", 125000, 4),
            ("100k Ring Road", 100000, 4), ("75k Ring Road", 75000, 4),
            ("62.5k Ring Road", 62500, 4), ("55k Ring Road", 55000, 4),
            ("50k Ring Road", 50000, 4), ("30k Ring Road", 30000, 4),
            ("25k Ring Road", 25000, 4), ("24k Ring Road", 24000, 2),
            ("23k Ring Road", 23000, 2), ("22k Ring Road", 22000, 2),
            ("21k Ring Road", 21000, 2), ("20k Ring Road", 20000, 4),
            ("15k Ring Road", 15000, 4), ("10k Ring Road", 10000, 4),
            ("7.5k Ring Road", 7500, 4), ("5k Ring Road", 5000, 4),
            ("2.5k Ring Road", 2500, 4), ("2k Ring Road", 2000, 4),
            ("1.5k Ring Road", 1500, 4), ("1k Ring Road", 1000, 4),
            ("500 Ring Road", 500, 4), ("200 Ring Road", 200, 4),
        };
        foreach (var (name, r, w) in rings)
            list.Add(Make(name, "Ring", SquareRing(r), w, ringRadius: r));

        // Diamond ring roads (width 4).
        var diamonds = new (string Name, int R)[]
        {
            ("World Border Diamond Ring Road", 3750000), ("500k Diamond Ring Road", 500000),
            ("250k Diamond Ring Road", 250000), ("125k Diamond Ring Road", 125000),
            ("50k Diamond Ring Road", 50000), ("25k Diamond Ring Road", 25000),
            ("15k Diamond Ring Road", 15000), ("10k Diamond Ring Road", 10000),
            ("5k Diamond Ring Road", 5000), ("2.5k Diamond Ring Road", 2500),
            ("2k Diamond Ring Road", 2000), ("1k Diamond Ring Road", 1000),
        };
        foreach (var (name, r) in diamonds)
            list.Add(Make(name, "DiamondRing", DiamondRing(r), 4, ringRadius: r));

        // Nether Star Ring Road.
        var star = new[]
        {
            new[] { -50000, 50000 }, new[] { 0, 125000 }, new[] { 50000, 50000 }, new[] { 125000, 0 },
            new[] { 50000, -50000 }, new[] { 0, -125000 }, new[] { -50000, -50000 }, new[] { -125000, 0 }, new[] { -50000, 50000 },
        };
        list.Add(Make("Nether Star Ring Road", "Star", star, 4));

        // 50k grid (every 5k between -45k..45k, horizontal + vertical).
        int[] gridVals = { 45000, 40000, 35000, 30000, 25000, 20000, 15000, 10000, 5000,
                           -5000, -10000, -15000, -20000, -25000, -30000, -35000, -40000, -45000 };
        foreach (var v in gridVals)
        {
            list.Add(Make($"50k Grid z={v}", "Grid", new[] { new[] { -50000, v }, new[] { 50000, v } }, 2));
            list.Add(Make($"50k Grid x={v}", "Grid", new[] { new[] { v, -50000 }, new[] { v, 50000 } }, 2));
        }

        foreach (var highway in list.Where(highway => highway.Category is "Axis" or "Diagonal" or "Grid"))
            highway.BuilderGroupId = hwuId;
        foreach (var highway in list.Where(highway =>
                     highway.Name is "125k Diamond Ring Road" or "250k Diamond Ring Road" or "500k Diamond Ring Road"))
            highway.BuilderGroupId = hwuId;

        AddEndAxis(list, "+X End Highway", 505_000, 0,
            "Extended to 505k by BRABcraft in late 2023; earlier sections include ice, rail, and End-stone.");
        AddEndAxis(list, "-X End Highway", -100_000, 0,
            "Documented as one of the four End axes extending roughly 100k from spawn.");
        AddEndAxis(list, "+Z End Ice Highway", 0, 30_000_000,
            "Runs from End spawn to the southern (+Z) world border. Extended to 1.5m by Texy69 in early 2024 and verified through to the border by the Atlas operator in August 2026.",
            pavingMaterial: "BlueIce", verifiedUtc: "2026-08-10T00:00:00Z");
        AddEndAxis(list, "-Z End Highway", 0, -1_335_000,
            "Extended to -1.335m by Texy69 in early 2024 using an automated four-wide path builder.");

        // The Southern Canal has two independently documented heads. Keeping separate
        // geometries avoids drawing an imaginary completed line across the ~28.9M gap.
        var southernCanal = Make("Southern Canal — spawnward segment", "Custom",
            new[] { new[] { 0, 1_200 }, new[] { 0, 1_100_000 } }, 32);
        southernCanal.Dimension = (int)Atlas.Dimension.Overworld;
        southernCanal.YLevel = 63;
        southernCanal.Status = "UnderConstruction";
        southernCanal.BuilderGroupId = waterWayUnionId;
        southernCanal.LengthBlocks = 1_098_800;
        southernCanal.Color = "#21a7a1";
        southernCanal.DisplayWeight = 12;
        southernCanal.Description = "Main +Z Overworld canal from the spawn region to approximately Z +1.1M. The route was started by independent builders, extended by several infrastructure groups, taken to roughly 540k by the Southern Canal Association, and subsequently widened to 32 blocks and extended by WaterWay Union. This is not connected to the separate world-border excavation.";
        southernCanal.WikiUrl = "https://2b2t.wikioasis.org/wiki/Southern_Canal";
        southernCanal.LastVerifiedUtc = "2025-11-24T00:00:00Z";
        list.Add(southernCanal);

        var borderCanal = Make("Southern Canal — world-border segment", "Custom",
            new[] { new[] { 0, 30_000_000 }, new[] { 0, 29_988_000 } }, 32);
        borderCanal.Dimension = (int)Atlas.Dimension.Overworld;
        borderCanal.YLevel = 63;
        borderCanal.Status = "Partial";
        borderCanal.BuilderGroupId = waterWayUnionId;
        borderCanal.LengthBlocks = 12_000;
        borderCanal.Color = "#21a7a1";
        borderCanal.DisplayWeight = 12;
        borderCanal.Description = "Separate Southern Canal excavation dug at least 12,000 blocks northward from the +Z Overworld world border. It is intentionally mapped separately from the spawnward canal because the intervening route is not documented as complete.";
        borderCanal.WikiUrl = "https://2b2t.wikioasis.org/wiki/Southern_Canal";
        borderCanal.LastVerifiedUtc = "2026-09-03T00:00:00Z";
        list.Add(borderCanal);

        EnsureUniqueSlugs(list);
        return list;
    }

    private async Task<int> SeedAttributionsAsync(
        IReadOnlyDictionary<string, Highway> highways,
        IReadOnlyDictionary<string, Group> groups)
    {
        var existing = (await _context.HighwayGroups.AsNoTracking().ToListAsync())
            .Select(link => (link.HighwayId, link.GroupId))
            .ToHashSet();
        var now = DateTime.UtcNow.ToString("o");
        var added = 0;

        void Link(string slug, string groupName, string role, string evidence)
        {
            if (!highways.TryGetValue(slug, out var highway) ||
                !groups.TryGetValue(groupName, out var group) ||
                !existing.Add((highway.Id, group.Id))) return;
            _context.HighwayGroups.Add(new HighwayGroup
            {
                HighwayId = highway.Id,
                GroupId = group.Id,
                Role = role,
                Evidence = evidence,
                DateAddedUtc = now,
            });
            added++;
        }

        var cardinalSlugs = new[] { "x-highway", "x-highway-2", "z-highway", "z-highway-2" };
        var diagonalSlugs = new[]
        {
            "x-z-diagonal-highway", "x-z-diagonal-highway-2",
            "x-z-diagonal-highway-3", "x-z-diagonal-highway-4",
        };
        foreach (var slug in cardinalSlugs.Concat(diagonalSlugs))
            Link(slug, "Highway Workers Union (HWU)", "Primary builder and current steward",
                "https://www.reddit.com/r/2b2t/comments/1ldirek/; https://www.reddit.com/r/2b2t/comments/1ob2ow8/");
        foreach (var slug in cardinalSlugs)
        {
            Link(slug, "Independent Interstate Society (IIS)", "Predecessor builder", "https://2b2t.wikioasis.org/wiki/Independent_Interstate_Society");
            Link(slug, "Motorway Extension Gurus (MEG)", "Contributor", "https://www.reddit.com/r/2b2t/comments/lj0slq/");
        }
        Link("x-highway-2", "-X Diggers", "Historical builder", "https://2b2t.wikioasis.org/wiki/-X_Diggers");
        Link("z-highway", "+Z Digging Group", "Historical builder", "https://2b2t.wikioasis.org/wiki/%2BZ_Digging_Group");
        Link("x-highway", "Nether Highway Group (NHG)", "Historical builder", "https://2b2t.wikioasis.org/wiki/Nether_Highway_Group");
        foreach (var slug in diagonalSlugs)
            Link(slug, "Motorway Extension Gurus (MEG)", "Predecessor builder", "https://2b2t.wikioasis.org/wiki/Motorway_Extension_Gurus");
        foreach (var highway in highways.Values.Where(highway => highway.Category == "Grid"))
            Link(highway.Slug, "Highway Workers Union (HWU)", "Builder", "https://www.reddit.com/r/2b2t/comments/13dqyj1/");
        foreach (var slug in new[] { "125k-diamond-ring-road", "250k-diamond-ring-road", "500k-diamond-ring-road" })
            Link(slug, "Highway Workers Union (HWU)", "Builder", "https://www.reddit.com/r/2b2t/comments/13hj8t6/");

        foreach (var slug in new[] { "southern-canal-spawnward-segment", "southern-canal-world-border-segment" })
        {
            Link(slug, "WaterWay Union", "Current builder and maintainer", "https://2b2t.wikioasis.org/wiki/WaterWay_Union");
            Link(slug, "Southern Canal Association", "Predecessor builder", "https://2b2t.wikioasis.org/wiki/Southern_Canal_Association");
            Link(slug, "Spawn Infrastructure Group (SIG)", "Historical contributor", "https://2b2t.wikioasis.org/wiki/Spawn_Infrastructure_Group");
        }

        if (added > 0) await _context.SaveChangesAsync();
        return added;
    }

    /// <summary>Disambiguates any duplicate slugs (e.g. grid lines that differ only by sign).</summary>
    private static void EnsureUniqueSlugs(List<Highway> list)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in list)
        {
            var slug = h.Slug;
            var n = 2;
            while (!seen.Add(slug))
                slug = $"{h.Slug}-{n++}";
            h.Slug = slug;
        }
    }

    private static int[][] SquareRing(int r) =>
        new[] { new[] { -r, r }, new[] { r, r }, new[] { r, -r }, new[] { -r, -r }, new[] { -r, r } };

    private static int[][] DiamondRing(int r) =>
        new[] { new[] { -r, 0 }, new[] { 0, r }, new[] { r, 0 }, new[] { 0, -r }, new[] { -r, 0 } };

    private static void AddAxis(List<Highway> list, string name, int x1, int z1, int x2, int z2)
    {
        var h = Make(name, "Axis", new[] { new[] { x1, z1 }, new[] { x2, z2 } }, 6);
        h.Paved = 1;
        h.PavingMaterial = "Obsidian";
        h.Status = "Partial";
        list.Add(h);
    }

    private static void AddDiagonal(List<Highway> list, string name, int x2, int z2)
    {
        var h = Make(name, "Diagonal", new[] { new[] { 0, 0 }, new[] { x2, z2 } }, 3);
        h.Paved = 1;
        h.PavingMaterial = "Obsidian";
        h.Status = "Partial";
        list.Add(h);
    }

    private static void AddEndAxis(
        List<Highway> list,
        string name,
        int x2,
        int z2,
        string description,
        string pavingMaterial = "Mixed",
        string? verifiedUtc = null)
    {
        var highway = Make(name, "Axis", new[] { new[] { 0, 0 }, new[] { x2, z2 } }, 4);
        highway.Dimension = (int)Atlas.Dimension.End;
        highway.Paved = 1;
        highway.PavingMaterial = pavingMaterial;
        highway.Status = "Partial";
        highway.Description = description;
        highway.WikiUrl = "https://2b2t.wikioasis.org/wiki/End_highways";
        highway.LastVerifiedUtc = verifiedUtc;
        list.Add(highway);
    }

    private static Highway Make(string name, string category, int[][] points, int width, int? ringRadius = null) => new()
    {
        Name = name,
        Slug = Slugify(name),
        Dimension = DimNether,
        Category = category,
        PointsJson = JsonSerializer.Serialize(points),
        RingRadius = ringRadius,
        Width = width,
        Visibility = "Public",
        ReviewStatus = "Approved",
        DateAddedUtc = DateTime.UtcNow.ToString("o"),
    };

    private static string Slugify(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (ch is ' ' or '-' or ',' or '.' or '=') sb.Append('-');
        }
        var slug = sb.ToString();
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Trim('-');
    }
}
