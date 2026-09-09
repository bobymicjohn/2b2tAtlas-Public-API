namespace Atlas;

/// <summary>Geometry / role of a highway (drives default colour + weight).</summary>
public enum HighwayCategory
{
    /// <summary>A cardinal highway extending along an X or Z axis.</summary>
    Axis,

    /// <summary>A highway extending diagonally from the origin.</summary>
    Diagonal,

    /// <summary>An axis-aligned square ring road.</summary>
    Ring,

    /// <summary>A diamond-shaped ring road.</summary>
    DiamondRing,

    /// <summary>A repeated grid segment.</summary>
    Grid,

    /// <summary>A spoke belonging to a star-shaped network.</summary>
    Star,

    /// <summary>A branch connecting to a larger highway.</summary>
    Spur,

    /// <summary>A highway whose geometry does not match a standard category.</summary>
    Custom,
}

/// <summary>Paving material — durability + prestige on 2b2t.</summary>
public enum PavingMaterial
{
    /// <summary>The paving material has not been recorded.</summary>
    Unknown,

    /// <summary>The surface is paved with obsidian.</summary>
    Obsidian,

    /// <summary>The route is cleared through native netherrack without a separate paved surface.</summary>
    NetherrackCleared,

    /// <summary>The surface is paved with blackstone.</summary>
    Blackstone,

    /// <summary>The surface is paved with basalt.</summary>
    Basalt,

    /// <summary>The surface is paved with blue ice for boat travel.</summary>
    BlueIce,

    /// <summary>The surface contains multiple paving materials.</summary>
    Mixed,

    /// <summary>The surface uses a material not represented by another value.</summary>
    Other,
}

/// <summary>Build/maintenance condition of a highway.</summary>
public enum HighwayStatus
{
    /// <summary>The road's condition has not been recorded.</summary>
    Unknown,

    /// <summary>The recorded route is complete.</summary>
    Complete,

    /// <summary>Only part of the recorded route is complete.</summary>
    Partial,

    /// <summary>The route has been cleared but not paved.</summary>
    ClearedOnly,

    /// <summary>The route is actively being built.</summary>
    UnderConstruction,

    /// <summary>The route is materially damaged.</summary>
    Griefed,

    /// <summary>The route is obstructed by fluid.</summary>
    Flooded,

    /// <summary>The route is no longer maintained.</summary>
    Abandoned,
}

/// <summary>Moderation state of a highway record (see GAMEPLAN §15).</summary>
public enum ReviewStatus
{
    /// <summary>The record is an unsubmitted draft.</summary>
    Draft,

    /// <summary>The record is awaiting moderator review.</summary>
    Pending,

    /// <summary>The record has been approved for normal use.</summary>
    Approved,

    /// <summary>The record was rejected during moderation.</summary>
    Rejected,
}

/// <summary>Public visibility of a highway record.</summary>
public enum HighwayVisibility
{
    /// <summary>The record may be returned by public endpoints when approved.</summary>
    Public,

    /// <summary>The record is hidden from public highway listings.</summary>
    Hidden,
}

/// <summary>A single point on a highway path in the route's native dimension coordinates.</summary>
public class HighwayPoint
{
    /// <summary>Gets or sets the X coordinate in the highway's native dimension.</summary>
    public int X { get; set; }

    /// <summary>Gets or sets the Z coordinate in the highway's native dimension.</summary>
    public int Z { get; set; }

    /// <summary>Initializes an empty highway point for serialization.</summary>
    public HighwayPoint() { }

    /// <summary>Initializes a highway point with native-dimension coordinates.</summary>
    /// <param name="x">The native X coordinate.</param>
    /// <param name="z">The native Z coordinate.</param>
    public HighwayPoint(int x, int z)
    {
        X = x;
        Z = z;
    }
}

/// <summary>
/// A 2b2t highway (lore-accurate; see GAMEPLAN §14). Geometry is stored in
/// native dimension coordinates. Rich attributes (material, walls, status…) let the
/// map show <em>what kind</em> of road it is and approved editors keep it current.
/// </summary>
public class Highway
{
    /// <summary>Opaque current-state token required for edits; reload after a conflict.</summary>
    public string? EditVersion { get; set; }

    /// <summary>Gets or sets the highway's persistent identifier.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the public API URL for this highway record.</summary>
    public string? ApiUrl { get; set; }

    /// <summary>Gets or sets an interactive Atlas map URL for this highway.</summary>
    public string? MapUrl { get; set; }

    /// <summary>Gets or sets the highway's public name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the stable URL-safe identifier used by API records and seeds.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Dimension the geometry belongs to.</summary>
    public Dimension Dimension { get; set; } = Dimension.Nether;

    /// <summary>Gets or sets the highway's geometric category.</summary>
    public HighwayCategory Category { get; set; } = HighwayCategory.Custom;

    /// <summary>Ordered path in native dimension coordinates.</summary>
    public List<HighwayPoint> Points { get; set; } = new();

    /// <summary>Radius (Nether units) for ring / diamond-ring roads.</summary>
    public int? RingRadius { get; set; }

    /// <summary>Walkable path width (blocks).</summary>
    public int Width { get; set; } = 4;

    /// <summary>Vertical clearance / tunnel headroom (blocks); null = open-air.</summary>
    public int? Height { get; set; }

    /// <summary>Build height (roof ≈128, lava ≈10 …).</summary>
    public int? YLevel { get; set; }

    /// <summary>Gets or sets whether the route has a deliberately constructed surface.</summary>
    public bool Paved { get; set; }

    /// <summary>Gets or sets the predominant material used for the paved surface.</summary>
    public PavingMaterial PavingMaterial { get; set; } = PavingMaterial.Unknown;

    /// <summary>Gets or sets whether the route has side walls.</summary>
    public bool Walls { get; set; }

    /// <summary>Gets or sets whether the route is enclosed by walls and a ceiling.</summary>
    public bool Enclosed { get; set; }

    /// <summary>Gets or sets whether the route runs on the Nether roof.</summary>
    public bool IsRoofHighway { get; set; }

    /// <summary>Gets or sets whether the route has installed lighting.</summary>
    public bool Lit { get; set; }

    /// <summary>Gets or sets the recorded construction or maintenance condition.</summary>
    public HighwayStatus Status { get; set; } = HighwayStatus.Unknown;

    /// <summary>Attributed builder group (FK → Group), if known.</summary>
    public int? BuilderGroupId { get; set; }

    /// <summary>Resolved builder-group name for display (server-populated; not stored here).</summary>
    public string? BuilderGroupName { get; set; }

    /// <summary>All reviewed builder, maintainer, and contributor attributions for this route.</summary>
    public List<HighwayGroupAttribution> BuilderGroups { get; set; } = [];

    /// <summary>Computed/stored path length in blocks.</summary>
    public int? LengthBlocks { get; set; }

    /// <summary>Gets or sets an optional description of the route and its history.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets an optional absolute URL for the highway's wiki article.</summary>
    public string? WikiUrl { get; set; }

    /// <summary>Gets or sets an optional absolute URL for related video coverage.</summary>
    public string? VideoUrl { get; set; }

    /// <summary>Render hint; falls back to Category defaults when null.</summary>
    public string? Color { get; set; }

    /// <summary>Gets or sets an optional map line weight override.</summary>
    public int? DisplayWeight { get; set; }

    /// <summary>Gets or sets whether the record is eligible for public listing.</summary>
    public HighwayVisibility Visibility { get; set; } = HighwayVisibility.Public;

    /// <summary>Gets or sets the record's moderation state.</summary>
    public ReviewStatus ReviewStatus { get; set; } = ReviewStatus.Approved;

    /// <summary>Gets or sets the identifier of the user who created the record.</summary>
    public int? CreatedByUserId { get; set; }

    /// <summary>Gets or sets the identifier of the user who most recently edited the record.</summary>
    public int? LastEditedByUserId { get; set; }

    /// <summary>Gets or sets the UTC time at which the record was added.</summary>
    public DateTime DateAddedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Gets or sets the UTC time at which the route was last verified in-game.</summary>
    public DateTime? LastVerifiedUtc { get; set; }
}

/// <summary>A reviewed group contribution to one highway.</summary>
public class HighwayGroupAttribution
{
    /// <summary>Gets or sets the attributed group identifier.</summary>
    public int GroupId { get; set; }

    /// <summary>Gets or sets the group's canonical crawlable entity URL.</summary>
    public string GroupUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the group's public API URL.</summary>
    public string GroupApiUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the attributed group's public name.</summary>
    public string GroupName { get; set; } = string.Empty;

    /// <summary>Gets or sets its role, such as Primary builder, Predecessor, or Contributor.</summary>
    public string Role { get; set; } = "Contributor";

    /// <summary>Gets or sets a concise public source note.</summary>
    public string? Evidence { get; set; }
}
