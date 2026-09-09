using System.Net.Http.Json;
using Atlas;

namespace _2b2tAtlas.Client.Services;

/// <summary>
/// Client-side service for retrieving location data from the Atlas API.
/// </summary>
public class LocationService
{
    private readonly HttpClient _http;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocationService"/> class.
    /// </summary>
    /// <param name="http">The HTTP client used to call the API.</param>
    public LocationService(HttpClient http)
    {
        _http = http;
    }

    /// <summary>
    /// Fetches all locations from the API.
    /// </summary>
    /// <returns>The list of locations, or an empty list if the request fails.</returns>
    public async Task<List<Location>> GetLocationsAsync()
    {
        try
        {
            var locations = await _http.GetFromJsonAsync<List<Location>>("api/locations");
            return locations ?? new List<Location>();
        }
        catch
        {
            // The map is still useful without markers; fail soft.
            return new List<Location>();
        }
    }

    /// <summary>
    /// Fetches a single location (with warps, attachments and renders) by its row id.
    /// </summary>
    /// <param name="id">The location's row id.</param>
    /// <returns>The location, or <c>null</c> if not found or the request fails.</returns>
    public async Task<Location?> GetLocationAsync(int id)
    {
        try
        {
            return await _http.GetFromJsonAsync<Location>($"api/locations/{id}");
        }
        catch
        {
            return null;
        }
    }
}
