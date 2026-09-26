using System.Net.Http.Json;
using System.Text.Json;

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
        GetPagesAsync<Warp>($"api/warps?locationId={locationId}");

    public Task<List<Render>> GetRendersAsync(int locationId) =>
        GetPagesAsync<Render>($"api/renders?locationId={locationId}");

    public Task<List<Highway>> GetHighwaysAsync() => GetAsync<List<Highway>>("api/highways");

    public Task<List<Render>> GetAllRendersAsync() => GetPagesAsync<Render>("api/renders");

    private async Task<List<T>> GetPagesAsync<T>(string path)
    {
        const int pageSize = 1000;
        var result = new List<T>();
        var separator = path.Contains('?') ? "&" : "?";
        for (var offset = 0; ; offset += pageSize)
        {
            var page = await GetAsync<List<T>>($"{path}{separator}limit={pageSize}&offset={offset}");
            result.AddRange(page);
            if (page.Count < pageSize)
            {
                return result;
            }
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
