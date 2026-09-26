namespace _2b2tAtlas.Server.Models;

/// <summary>
/// EF Core entity for a 2b2t group/organisation (GAMEPLAN §15). Enum stored as
/// string; table created by <c>SchemaUpgrader</c>.
/// </summary>
public class Group
{
    /// <summary>Database-generated group identifier used by highway attribution.</summary>
    public int Id { get; set; }

    /// <summary>Canonical or administrator-defined public group name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>String-backed build, highway, mixed, or other classification.</summary>
    public string Type { get; set; } = "Other";

    /// <summary>Gets or sets public historical context for the group.</summary>
    public string? Description { get; set; }

    /// <summary>Optional map and directory color in CSS hexadecimal form.</summary>
    public string? Color { get; set; }

    /// <summary>Gets or sets an optional public 2b2t wiki reference used for attribution.</summary>
    public string? WikiUrl { get; set; }

    /// <summary>Group's official website.</summary>
    public string? WebsiteUrl { get; set; }

    /// <summary>A public group Discord invitation.</summary>
    public string? DiscordUrl { get; set; }

    /// <summary>Public group logo or representative emblem.</summary>
    public string? LogoUrl { get; set; }

    /// <summary>Public source page for the logo.</summary>
    public string? LogoSourceUrl { get; set; }

    /// <summary>Documented, potentially approximate founding date.</summary>
    public string? Founded { get; set; }

    /// <summary>Documented activity status.</summary>
    public string? Status { get; set; }

    /// <summary>Gets or sets the UTC record creation timestamp in round-trip text form.</summary>
    public string DateAddedUtc { get; set; } = DateTime.UtcNow.ToString("o");

    /// <summary>Gets or sets the UTC timestamp of the latest material public metadata change.</summary>
    public string ModifiedUtc { get; set; } = DateTime.UtcNow.ToString("o");

    /// <summary>Gets or sets explicit Atlas location attributions for this group.</summary>
    public virtual ICollection<LocationGroup> LocationGroups { get; set; } = new List<LocationGroup>();

    /// <summary>Gets or sets reviewed highway contributions by this group.</summary>
    public virtual ICollection<HighwayGroup> HighwayGroups { get; set; } = new List<HighwayGroup>();
}
