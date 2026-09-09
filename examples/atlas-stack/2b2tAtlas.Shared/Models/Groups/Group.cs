namespace Atlas;

/// <summary>What a group primarily does on 2b2t.</summary>
public enum GroupType
{
    /// <summary>A group focused on constructing bases or monuments.</summary>
    Build,

    /// <summary>A group focused on constructing or maintaining highways.</summary>
    Highway,

    /// <summary>A group active in both building and highway work.</summary>
    Mixed,

    /// <summary>A group that does not fit the other activity categories.</summary>
    Other,
}

/// <summary>Reviewed alternate names for canonical Atlas group identities.</summary>
public static class GroupAliases
{
    private static readonly IReadOnlyDictionary<string, string[]> Reviewed =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Valkyria"] = ["Space Valkyria"],
            ["The Emperium"] = ["Emperium"],
            ["DGA"] = ["Democratic Group Alliance", "Small Groups Alliance"],
            ["Shortbus Caliphate"] = ["SBC"],
            ["The Mew Revolution"] = ["Mew Revolution"],
            ["Vapepens Elite Alliance"] = ["VEA"],
            ["The Society Project"] = ["The Society"],
            ["Spawn Infrastructure Group (SIG)"] = ["SIG"],
            ["Independent Interstate Society (IIS)"] = ["IIS"],
            ["Motorway Extension Gurus (MEG)"] = ["MEG"],
            ["Headpats4All"] = ["H4A"],
            ["Peacekeepers"] = ["PK"],
            ["The Watchmen"] = ["Watchmen"],
        };

    /// <summary>Returns exact reviewed aliases for a canonical group name.</summary>
    public static IReadOnlyList<string> For(string? name) =>
        !string.IsNullOrWhiteSpace(name) && Reviewed.TryGetValue(name, out var aliases)
            ? aliases
            : Array.Empty<string>();
}

/// <summary>
/// A 2b2t group/organisation used to attribute highways and bases (GAMEPLAN §15).
/// </summary>
public class Group
{
    /// <summary>Gets or sets the group's persistent identifier.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the canonical crawlable entity URL for this group.</summary>
    public string? CanonicalUrl { get; set; }

    /// <summary>Gets or sets the interactive Atlas group-page URL.</summary>
    public string? InteractiveUrl { get; set; }

    /// <summary>Gets or sets the public API URL for this group record.</summary>
    public string? ApiUrl { get; set; }

    /// <summary>Gets or sets the group's public name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets reviewed alternate names; this public projection is derived from the canonical name.</summary>
    public List<string> Aliases { get; set; } = [];

    /// <summary>Gets or sets the group's primary activity category.</summary>
    public GroupType Type { get; set; } = GroupType.Other;

    /// <summary>Gets or sets an optional description of the group.</summary>
    public string? Description { get; set; }

    /// <summary>Optional render hint (hex) for markers/labels.</summary>
    public string? Color { get; set; }

    /// <summary>Gets or sets an optional absolute URL for the group's wiki article.</summary>
    public string? WikiUrl { get; set; }

    /// <summary>Gets or sets the group's official website, when one is known.</summary>
    public string? WebsiteUrl { get; set; }

    /// <summary>Gets or sets the group's public Discord invite, when one is known and still current.</summary>
    public string? DiscordUrl { get; set; }

    /// <summary>Gets or sets a public logo or representative emblem URL.</summary>
    public string? LogoUrl { get; set; }

    /// <summary>Gets or sets the page that identifies the source and licensing context of the logo.</summary>
    public string? LogoSourceUrl { get; set; }

    /// <summary>Gets or sets a human-readable founding date because many historical dates are approximate.</summary>
    public string? Founded { get; set; }

    /// <summary>Gets or sets the group's documented state, such as Active, Inactive, or Disbanded.</summary>
    public string? Status { get; set; }

    /// <summary>Gets or sets the number of Atlas locations explicitly attributed to the group.</summary>
    public int LocationCount { get; set; }

    /// <summary>Gets or sets the number of Atlas highways explicitly attributed to the group.</summary>
    public int HighwayCount { get; set; }

    /// <summary>Gets or sets locations explicitly attributed to this group on the detail endpoint.</summary>
    public List<GroupLocationSummary> Locations { get; set; } = [];

    /// <summary>Gets or sets highways explicitly attributed to this group on the detail endpoint.</summary>
    public List<GroupHighwaySummary> Highways { get; set; } = [];

    /// <summary>Gets or sets the UTC time at which the group was added to the Atlas.</summary>
    public DateTime DateAddedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Gets or sets when the public group record was last materially changed.</summary>
    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>A compact, navigable Atlas location attributed to a group.</summary>
public class GroupLocationSummary
{
    /// <summary>Gets or sets the Atlas location identifier.</summary>
    public int LocationId { get; set; }
    /// <summary>Gets or sets the canonical crawlable URL for the location.</summary>
    public string LocationUrl { get; set; } = string.Empty;
    /// <summary>Gets or sets the interactive Atlas URL for the location.</summary>
    public string LocationInteractiveUrl { get; set; } = string.Empty;
    /// <summary>Gets or sets the public API URL for the location.</summary>
    public string LocationApiUrl { get; set; } = string.Empty;
    /// <summary>Gets or sets the public location name.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Gets or sets the group's role at the location.</summary>
    public string Role { get; set; } = "Builder";
    /// <summary>Gets or sets the Minecraft dimension identifier.</summary>
    public int Dimension { get; set; }
    /// <summary>Gets or sets the location's X coordinate.</summary>
    public int X { get; set; }
    /// <summary>Gets or sets the location's Z coordinate.</summary>
    public int Z { get; set; }
    /// <summary>Gets or sets the number of public renders linked to the location.</summary>
    public int RenderCount { get; set; }
}

/// <summary>A compact, navigable highway attributed to a group.</summary>
public class GroupHighwaySummary
{
    /// <summary>Gets or sets the highway identifier.</summary>
    public int HighwayId { get; set; }
    /// <summary>Gets or sets the public API URL for the highway record.</summary>
    public string HighwayApiUrl { get; set; } = string.Empty;
    /// <summary>Gets or sets a map URL centered on the highway's dimension.</summary>
    public string MapUrl { get; set; } = string.Empty;
    /// <summary>Gets or sets the public highway name.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Gets or sets the Minecraft dimension identifier.</summary>
    public int Dimension { get; set; }

    /// <summary>Gets or sets this group's documented role on the route.</summary>
    public string Role { get; set; } = "Contributor";
}

/// <summary>Public group attribution embedded in a location record.</summary>
public class LocationGroupAttribution
{
    /// <summary>Gets or sets the attributed group's identifier.</summary>
    public int GroupId { get; set; }
    /// <summary>Gets or sets the group's canonical crawlable entity URL.</summary>
    public string GroupUrl { get; set; } = string.Empty;
    /// <summary>Gets or sets the group's interactive Atlas URL.</summary>
    public string GroupInteractiveUrl { get; set; } = string.Empty;
    /// <summary>Gets or sets the group's public API URL.</summary>
    public string GroupApiUrl { get; set; } = string.Empty;
    /// <summary>Gets or sets the attributed group's public name.</summary>
    public string GroupName { get; set; } = string.Empty;
    /// <summary>Gets or sets the group's role at the location.</summary>
    public string Role { get; set; } = "Builder";
    /// <summary>Gets or sets the group's display color.</summary>
    public string? Color { get; set; }
    /// <summary>Gets or sets the group's optional logo URL.</summary>
    public string? LogoUrl { get; set; }
}
