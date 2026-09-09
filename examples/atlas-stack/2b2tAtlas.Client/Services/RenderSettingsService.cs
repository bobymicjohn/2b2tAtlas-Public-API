using System.Net.Http.Headers;
using System.Net.Http.Json;
using Atlas;

namespace _2b2tAtlas.Client.Services;

/// <summary>
/// Client-side service for the admin render-settings tab. It reads and writes the worker's unMINED render
/// flags and colour/biome/style config, and requests a bounded sample preview render. All calls require the
/// <c>renders.manage</c> permission via the bearer token.
/// </summary>
public class RenderSettingsService
{
    private readonly HttpClient _http;
    private readonly AuthService _auth;

    /// <summary>Initializes the render-settings API client.</summary>
    /// <param name="http">The client used for relative <c>api/render-settings</c> requests.</param>
    /// <param name="auth">The source of bearer tokens.</param>
    public RenderSettingsService(HttpClient http, AuthService auth)
    {
        _http = http;
        _auth = auth;
    }

    /// <summary>Loads the current render settings, or null when unauthorized or unavailable.</summary>
    public async Task<RenderSettingsDto?> GetAsync()
    {
        var request = await CreateAsync(HttpMethod.Get, "api/render-settings");
        if (request is null) return null;
        var response = await _http.SendAsync(request);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<RenderSettingsDto>() : null;
    }

    /// <summary>Saves the render settings. Returns success and any server error message.</summary>
    public async Task<(bool Ok, string? Error)> SaveAsync(RenderSettingsDto dto)
    {
        var request = await CreateAsync(HttpMethod.Put, "api/render-settings");
        if (request is null) return (false, "Not authenticated");
        request.Content = JsonContent.Create(dto);
        var response = await _http.SendAsync(request);
        return response.IsSuccessStatusCode ? (true, null) : (false, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Requests a bounded preview render from a named sample world and dimension.</summary>
    /// <param name="world">The staged sample world name.</param>
    /// <param name="dimension">The dimension to render.</param>
    public async Task<RenderPreviewResult?> PreviewAsync(string world, string dimension)
    {
        var request = await CreateAsync(HttpMethod.Post,
            $"api/render-settings/preview?world={Uri.EscapeDataString(world)}&dimension={Uri.EscapeDataString(dimension)}");
        if (request is null) return null;
        var response = await _http.SendAsync(request);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<RenderPreviewResult>() : null;
    }

    /// <summary>Gets current bulk re-render queue counts.</summary>
    public async Task<BulkRerenderStatus?> GetRerenderStatusAsync()
    {
        var request = await CreateAsync(HttpMethod.Get, "api/render-settings/rerender-status");
        if (request is null) return null;
        var response = await _http.SendAsync(request);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<BulkRerenderStatus>() : null;
    }

    /// <summary>Queues all completed renders for rebuilding from archived WDLs.</summary>
    public async Task<(bool Ok, BulkRerenderResult? Result, string? Error)> RerenderAllAsync()
    {
        var request = await CreateAsync(HttpMethod.Post, "api/render-settings/rerender-all");
        if (request is null) return (false, null, "Not authenticated");
        var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return (false, null, await response.Content.ReadAsStringAsync());
        return (true, await response.Content.ReadFromJsonAsync<BulkRerenderResult>(), null);
    }

    private async Task<HttpRequestMessage?> CreateAsync(HttpMethod method, string url)
    {
        var token = await _auth.GetTokenAsync();
        if (string.IsNullOrEmpty(token)) return null;
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }
}
