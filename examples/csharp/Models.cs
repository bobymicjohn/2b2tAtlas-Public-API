sealed class Location
{
    public int Rowid { get; init; }
    public string Name { get; init; } = "";
    public int Dimension { get; init; }
    public string DimensionName { get; init; } = "";
    public long X { get; init; }
    public int Y { get; init; }
    public long Z { get; init; }
    public string CanonicalUrl { get; init; } = "";
    public string InteractiveUrl { get; init; } = "";
    public List<LocationGroup> Groups { get; init; } = [];
}

sealed class LocationGroup
{
    public int GroupId { get; init; }
    public string GroupName { get; init; } = "";
    public string Role { get; init; } = "";
}

sealed class Group
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public List<string> Aliases { get; init; } = [];
    public int LocationCount { get; init; }
    public int HighwayCount { get; init; }
    public List<GroupLocation> Locations { get; init; } = [];
    public List<GroupHighway> Highways { get; init; } = [];
}

sealed class GroupLocation
{
    public int LocationId { get; init; }
    public string Name { get; init; } = "";
    public string Role { get; init; } = "";
    public string LocationInteractiveUrl { get; init; } = "";
}

sealed class GroupHighway
{
    public int HighwayId { get; init; }
    public string Name { get; init; } = "";
    public string Role { get; init; } = "";
    public string HighwayApiUrl { get; init; } = "";
}

sealed class Warp
{
    public int Id { get; init; }
    public int? LocationRowid { get; init; }
    public string Name { get; init; } = "";
    public string? WorldDownloadDate { get; init; }
    public string ApiUrl { get; init; } = "";
}

sealed class Render
{
    public int RenderId { get; init; }
    public int LocationId { get; init; }
    public string Name { get; init; } = "";
    public string? WorldDownloadDate { get; init; }
    public string ApiUrl { get; init; } = "";
    public string? TileUrlTemplate { get; init; }
    public string? WorldDownloadUrl { get; init; }
    public string? WorldDownloadMetadataUrl { get; init; }
    public string? WorldDownloadScope { get; init; }
    public string? WorldDownloadSha256 { get; init; }
    public string? WorldDownloadSource { get; init; }
    public string? BlueMapUrl { get; init; }
    public string? BlueMapPath { get; init; }
    public int? BlueMapProfileVersion { get; init; }
}

sealed class Highway
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public int Dimension { get; init; }
    public string ApiUrl { get; init; } = "";
    public List<HighwayPoint> Points { get; init; } = [];
}

sealed class HighwayPoint
{
    public long X { get; init; }
    public long Z { get; init; }
}
