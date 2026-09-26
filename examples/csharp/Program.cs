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
{
    Console.WriteLine($"  Approximate Nether portal candidate: {nether.X}, {nether.Z}");
}

Console.WriteLine("\nArchive warps for this location:");
var warps = await api.GetWarpsAsync(location.Rowid);
foreach (var warp in warps)
{
    Console.WriteLine($"  /warp {warp.Name} ({warp.WorldDownloadDate ?? "date unknown"})");
}

if (warps.Count == 0)
{
    Console.WriteLine("  none catalogued");
}

Console.WriteLine("\nWDL-derived renders for this location:");
var locationRenders = await api.GetRendersAsync(location.Rowid);
foreach (var render in locationRenders)
{
    Console.WriteLine($"  {render.Name} | {render.WorldDownloadDate ?? "date unknown"} | {render.ApiUrl}" +
        (string.IsNullOrWhiteSpace(render.WorldDownloadUrl) ? "" : $" | source WDL: {render.WorldDownloadUrl}") +
        (string.IsNullOrWhiteSpace(render.BlueMapUrl) ? "" : $" | 3D: {render.BlueMapUrl} (profile {render.BlueMapProfileVersion})"));
}

if (locationRenders.Count == 0)
{
    Console.WriteLine("  none catalogued");
}

var allRenders = await api.GetAllRendersAsync();
var renderedLocationCount = allRenders.Select(render => render.LocationId).Distinct().Count();
Console.WriteLine($"\nCatalog: {allRenders.Count:N0} public renders across {renderedLocationCount:N0} locations");
Console.WriteLine($"          {allRenders.Count(render => !string.IsNullOrWhiteSpace(render.BlueMapUrl)):N0} currently advertise BlueMap 3D");

var relatedGroup = location.Groups.FirstOrDefault();
var groupSearch = relatedGroup?.GroupName ?? "Highway Workers Union";
Console.WriteLine($"\nFinding bases/highways attributed to group: {groupSearch}");
var group = await api.FindGroupAsync(groupSearch);
if (group is not null)
{
    Console.WriteLine($"  {group.Name}: {group.LocationCount} builds, {group.HighwayCount} highways");
    foreach (var build in group.Locations.Take(5))
    {
        Console.WriteLine($"  build: {build.Name} ({build.Role}) -> {build.LocationInteractiveUrl}");
    }

    foreach (var highway in group.Highways.Take(5))
    {
        Console.WriteLine($"  route: {highway.Name} ({highway.Role}) -> {highway.HighwayApiUrl}");
    }
}

Console.WriteLine("\nReviewed highway/canal records containing '+Z':");
var highways = await api.GetHighwaysAsync();
foreach (var highway in highways.Where(item => item.Name.Contains("+Z", StringComparison.OrdinalIgnoreCase)).Take(8))
{
    Console.WriteLine($"  {highway.Name} [{highway.Dimension}] {highway.Points.Count} points -> {highway.ApiUrl}");
}