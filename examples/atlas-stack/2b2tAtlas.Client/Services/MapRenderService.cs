using System.Net.Http.Json;
using Atlas;

namespace _2b2tAtlas.Client.Services;

/// <summary>
/// Client-side service for dimension-level world renders (GAMEPLAN §6b / Phase 1).
/// Reads are public; the map merges these into its render picker so ingest-registered
/// world downloads appear as layers without any code change.
/// </summary>
public class MapRenderService
{
    private readonly HttpClient _http;

    /// <summary>Initializes the public dimension-level map render API client.</summary>
    /// <param name="http">The client used for relative <c>api/maprenders</c> requests.</param>
    public MapRenderService(HttpClient http)
    {
        _http = http;
    }

    /// <summary>Fetches published world renders, or an empty list on failure.</summary>
    public async Task<List<MapRenderDto>> GetPublishedAsync()
    {
        try
        {
            return await _http.GetFromJsonAsync<List<MapRenderDto>>("api/maprenders") ?? new();
        }
        catch
        {
            return new();
        }
    }
}
