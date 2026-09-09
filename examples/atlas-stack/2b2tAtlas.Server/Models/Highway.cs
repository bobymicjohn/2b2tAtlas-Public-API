namespace _2b2tAtlas.Server.Models;

/// <summary>
/// EF Core entity for a 2b2t highway (see GAMEPLAN §14). Geometry is stored as a
/// JSON string of native-dimension coordinate points; enums are stored as strings for
/// SQLite readability. The table is created by <c>SchemaUpgrader</c> (the app
/// uses EnsureCreated, not migrations).
/// </summary>
public class Highway
{
    /// <summary>Gets or sets the database-generated route identifier.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the public canonical or community-supplied highway name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the stable URL-safe key used to identify seeded and edited routes.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Dimension index (0 Overworld, 1 Nether, 2 End).</summary>
    public int Dimension { get; set; } = 1;

    /// <summary>Gets or sets the string-backed geometry category used by map styling and editors.</summary>
    public string Category { get; set; } = "Custom";

    /// <summary>JSON array of [x,z] pairs in native dimension coordinates.</summary>
    public string PointsJson { get; set; } = "[]";

    /// <summary>Gets or sets a ring route's radius in native-dimension blocks.</summary>
    public int? RingRadius { get; set; }

    /// <summary>Gets or sets the documented traversable width in Minecraft blocks.</summary>
    public int Width { get; set; } = 4;

    /// <summary>Gets or sets the documented vertical clearance in Minecraft blocks.</summary>
    public int? Height { get; set; }

    /// <summary>Gets or sets the route's documented Minecraft elevation.</summary>
    public int? YLevel { get; set; }

    /// <summary>Gets or sets whether the route has an installed paving surface, stored as 1 or 0.</summary>
    public int Paved { get; set; }

    /// <summary>Gets or sets the string-backed primary paving material.</summary>
    public string PavingMaterial { get; set; } = "Unknown";

    /// <summary>Gets or sets whether the route has constructed side walls, stored as 1 or 0.</summary>
    public int Walls { get; set; }

    /// <summary>Gets or sets whether the route is enclosed by walls and a ceiling, stored as 1 or 0.</summary>
    public int Enclosed { get; set; }

    /// <summary>Gets or sets whether the route runs on the Nether roof, stored as 1 or 0.</summary>
    public int IsRoofHighway { get; set; }

    /// <summary>Gets or sets whether lighting is documented along the route, stored as 1 or 0.</summary>
    public int Lit { get; set; }

    /// <summary>Gets or sets the string-backed completion or maintenance status.</summary>
    public string Status { get; set; } = "Unknown";

    /// <summary>Gets or sets the group credited with building or maintaining the route.</summary>
    public int? BuilderGroupId { get; set; }

    /// <summary>Gets or sets an optional documented route length in native-dimension blocks.</summary>
    public int? LengthBlocks { get; set; }

    /// <summary>Gets or sets public historical, construction, or condition notes.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets an optional public 2b2t wiki reference.</summary>
    public string? WikiUrl { get; set; }

    /// <summary>Gets or sets an optional public video reference.</summary>
    public string? VideoUrl { get; set; }

    /// <summary>Gets or sets the optional CSS color used to render this route on the map.</summary>
    public string? Color { get; set; }

    /// <summary>Gets or sets an optional map stroke weight override.</summary>
    public int? DisplayWeight { get; set; }

    /// <summary>Gets or sets whether the route is eligible for the public map collection query.</summary>
    public string Visibility { get; set; } = "Public";

    /// <summary>Gets or sets the moderation state; only approved routes enter the public map collection.</summary>
    public string ReviewStatus { get; set; } = "Approved";

    /// <summary>Gets or sets the authenticated account that originally submitted the route.</summary>
    public int? CreatedByUserId { get; set; }

    /// <summary>Gets or sets the authenticated account that most recently edited or moderated the route.</summary>
    public int? LastEditedByUserId { get; set; }

    /// <summary>Gets or sets the UTC record creation timestamp in round-trip text form.</summary>
    public string DateAddedUtc { get; set; } = DateTime.UtcNow.ToString("o");

    /// <summary>Gets or sets the UTC timestamp of the latest source-backed or operator verification.</summary>
    public string? LastVerifiedUtc { get; set; }

    /// <summary>Gets or sets all reviewed group attributions for this route.</summary>
    public virtual ICollection<HighwayGroup> HighwayGroups { get; set; } = new List<HighwayGroup>();
}
