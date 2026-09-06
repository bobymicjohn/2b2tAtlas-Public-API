using System.Net.Http.Json;
using System.Text.Json;

var baseUrl = Environment.GetEnvironmentVariable("ATLAS_API_BASE_URL")
    ?? "https://api.blackportal.cloud/";
baseUrl = baseUrl.TrimEnd('/') + "/";
var search = args.Length == 0 ? "Mu Megabase" : string.Join(' ', args);

using var http = new HttpClient
{
    BaseAddress = new Uri(baseUrl),
    Timeout = TimeSpan.FromSeconds(30),
};
http.DefaultRequestHeaders.UserAgent.ParseAdd("2b2tAtlas-Public-API/1.0");
http.DefaultRequestHeaders.Accept.ParseAdd("application/json");

var api = new AtlasApi(http);

Console.WriteLine($"Finding location: {search}");
var location = await api.FindLocationAsync(search);
if (location is null)
{
    Console.WriteLine("No location matched.");
    return;
}

Console.WriteLine($"  {location.Name} [{location.DimensionName}] {location.X}, {location.Y}, {location.Z}");
Console.WriteLine($"  {location.InteractiveUrl}");

var nether = Coordinates.OverworldToNether(location.X, location.Z);
if (location.Dimension == 0)
    Console.WriteLine($"  Approximate Nether portal candidate: {nether.X}, {nether.Z}");

Console.WriteLine("\nArchive warps for this location:");
var warps = await api.GetWarpsAsync(location.Rowid);
foreach (var warp in warps)
    Console.WriteLine($"  /warp {warp.Name} ({warp.WorldDownloadDate ?? "date unknown"})");
if (warps.Count == 0) Console.WriteLine("  none catalogued");

Console.WriteLine("\nWDL-derived renders for this location:");
var locationRenders = await api.GetRendersAsync(location.Rowid);
foreach (var render in locationRenders)
    Console.WriteLine($"  {render.Name} | {render.WorldDownloadDate ?? "date unknown"} | {render.ApiUrl}" +
        (string.IsNullOrWhiteSpace(render.WorldDownloadUrl) ? "" : $" | source WDL: {render.WorldDownloadUrl}"));
if (locationRenders.Count == 0) Console.WriteLine("  none catalogued");

var allRenders = await api.GetAllRendersAsync();
var renderedLocationCount = allRenders.Select(render => render.LocationId).Distinct().Count();
Console.WriteLine($"\nCatalog: {allRenders.Count:N0} public renders across {renderedLocationCount:N0} locations");

var relatedGroup = location.Groups.FirstOrDefault();
var groupSearch = relatedGroup?.GroupName ?? "Highway Workers Union";
Console.WriteLine($"\nFinding bases/highways attributed to group: {groupSearch}");
var group = await api.FindGroupAsync(groupSearch);
if (group is not null)
{
    Console.WriteLine($"  {group.Name}: {group.LocationCount} builds, {group.HighwayCount} highways");
    foreach (var build in group.Locations.Take(5))
        Console.WriteLine($"  build: {build.Name} ({build.Role}) -> {build.LocationInteractiveUrl}");
    foreach (var highway in group.Highways.Take(5))
        Console.WriteLine($"  route: {highway.Name} ({highway.Role}) -> {highway.HighwayApiUrl}");
}

Console.WriteLine("\nReviewed highway/canal records containing '+Z':");
var highways = await api.GetHighwaysAsync();
foreach (var highway in highways.Where(item => item.Name.Contains("+Z", StringComparison.OrdinalIgnoreCase)).Take(8))
    Console.WriteLine($"  {highway.Name} [{highway.Dimension}] {highway.Points.Count} points -> {highway.ApiUrl}");

sealed class AtlasApi(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Location?> FindLocationAsync(string name)
    {
        var locations = await GetAsync<List<Location>>("api/locations");
        return locations.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? locations.FirstOrDefault(item => item.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<Group?> FindGroupAsync(string nameOrAlias)
    {
        var groups = await GetAsync<List<Group>>("api/groups");
        var summary = groups.FirstOrDefault(item => item.Name.Equals(nameOrAlias, StringComparison.OrdinalIgnoreCase))
            ?? groups.FirstOrDefault(item => item.Aliases.Any(alias => alias.Equals(nameOrAlias, StringComparison.OrdinalIgnoreCase)))
            ?? groups.FirstOrDefault(item => item.Name.Contains(nameOrAlias, StringComparison.OrdinalIgnoreCase));
        return summary is null ? null : await GetAsync<Group>($"api/groups/{summary.Id}");
    }

    public Task<List<Warp>> GetWarpsAsync(int locationId) =>
        GetAsync<List<Warp>>($"api/warps?locationId={locationId}&limit=1000");

    public Task<List<Render>> GetRendersAsync(int locationId) =>
        GetAsync<List<Render>>($"api/renders?locationId={locationId}&limit=1000");

    public Task<List<Highway>> GetHighwaysAsync() => GetAsync<List<Highway>>("api/highways");

    public async Task<List<Render>> GetAllRendersAsync()
    {
        const int pageSize = 1000;
        var result = new List<Render>();
        for (var offset = 0; ; offset += pageSize)
        {
            var page = await GetAsync<List<Render>>($"api/renders?limit={pageSize}&offset={offset}");
            result.AddRange(page);
            if (page.Count < pageSize) return result;
        }
    }

    private async Task<T> GetAsync<T>(string path)
    {
        using var response = await http.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(Json)
            ?? throw new InvalidOperationException($"Atlas returned an empty JSON body for {path}.");
    }
}

static class Coordinates
{
    public static (long X, long Z) OverworldToNether(long x, long z) =>
        ((long)Math.Floor(x / 8d), (long)Math.Floor(z / 8d));
}

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
