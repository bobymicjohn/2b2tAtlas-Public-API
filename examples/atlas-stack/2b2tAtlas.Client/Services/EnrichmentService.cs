using System.Net.Http.Headers;
using System.Net.Http.Json;
using Atlas;
using Atlas.Enrichment;

namespace _2b2tAtlas.Client.Services;

/// <summary>
/// Client-side service for the local AI wiki-enrichment engine. It drives the admin enrichment surface —
/// reading engine status, launching batch runs, listing AI suggestions, and applying, keeping, rejecting,
/// or reverting them — and provides the interactive per-location preview used by the location editor. All
/// calls require the moderator token except <see cref="PreviewAsync"/>, which needs edit rights.
/// </summary>
public class EnrichmentService
{
    private readonly HttpClient _http;
    private readonly AuthService _auth;

    /// <summary>Initializes the enrichment API client.</summary>
    /// <param name="http">The client used for relative <c>api/enrichment</c> and <c>api/locations</c> requests.</param>
    /// <param name="auth">The source of bearer tokens for every request.</param>
    public EnrichmentService(HttpClient http, AuthService auth)
    {
        _http = http;
        _auth = auth;
    }

    /// <summary>Gets the current engine status, or null when unauthenticated or unavailable.</summary>
    public async Task<EnrichmentStatusDto?> GetStatusAsync()
    {
        var request = await CreateAsync(HttpMethod.Get, "api/enrichment/status");
        if (request == null) return null;
        var response = await _http.SendAsync(request);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<EnrichmentStatusDto>()
            : null;
    }

    /// <summary>Lists AI enrichment revisions, optionally filtered by status.</summary>
    public async Task<List<RevisionDto>> GetRevisionsAsync(string? status = null)
    {
        var url = string.IsNullOrWhiteSpace(status)
            ? "api/enrichment/revisions"
            : $"api/enrichment/revisions?status={Uri.EscapeDataString(status)}";
        var request = await CreateAsync(HttpMethod.Get, url);
        if (request == null) return new();
        var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return new();
        return await response.Content.ReadFromJsonAsync<List<RevisionDto>>() ?? new();
    }

    /// <summary>
    /// Starts a server-owned batch pass. A conflict response also returns the currently active run so every
    /// browser converges on the same state instead of launching duplicate work.
    /// </summary>
    public async Task<EnrichmentRunStatusDto?> RunAsync(EnrichmentRunRequest options)
    {
        var request = await CreateAsync(HttpMethod.Post, "api/enrichment/run");
        if (request == null) return null;
        request.Content = JsonContent.Create(options);
        var response = await _http.SendAsync(request);
        if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Conflict)
            return await response.Content.ReadFromJsonAsync<EnrichmentRunStatusDto>();
        return null;
    }

    /// <summary>Generates review-only proposals for unrepresented groups with explicit Atlas builds.</summary>
    public async Task<GroupDiscoveryRunSummary?> RunGroupDiscoveryAsync(GroupDiscoveryRunRequest options)
    {
        var request = await CreateAsync(HttpMethod.Post, "api/enrichment/groups/run");
        if (request == null) return null;
        request.Content = JsonContent.Create(options);
        var response = await _http.SendAsync(request);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<GroupDiscoveryRunSummary>()
            : null;
    }

    /// <summary>Applies a queued (Pending) AI suggestion.</summary>
    public Task<bool> ApplyAsync(int id) => PostAsync($"api/enrichment/{id}/apply");

    /// <summary>Keeps an auto-applied (Applied) AI suggestion.</summary>
    public Task<bool> KeepAsync(int id) => PostAsync($"api/enrichment/{id}/keep");

    /// <summary>Rejects a queued (Pending) AI suggestion.</summary>
    public Task<bool> RejectAsync(int id) => PostAsync($"api/enrichment/{id}/reject");

    /// <summary>Reverts an auto-applied (Applied) AI suggestion, restoring the prior values.</summary>
    public Task<bool> RevertAsync(int id) => PostAsync($"api/enrichment/{id}/revert");

    /// <summary>
    /// Previews a wiki match and drafted description for a saved location without persisting anything, for
    /// review in the location editor.
    /// </summary>
    /// <param name="locationId">The saved location's row id.</param>
    /// <param name="regenerateDescription">When true, drafts a description even if one already exists.</param>
    /// <returns>The suggestion, or null when unauthenticated, unavailable, or the location is missing.</returns>
    public async Task<WikiEnrichmentSuggestion?> PreviewAsync(int locationId, bool regenerateDescription)
    {
        var url = $"api/locations/{locationId}/enrich?regenerateDescription={(regenerateDescription ? "true" : "false")}";
        var request = await CreateAsync(HttpMethod.Post, url);
        if (request == null) return null;
        var response = await _http.SendAsync(request);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<WikiEnrichmentSuggestion>()
            : null;
    }

    private async Task<HttpRequestMessage?> CreateAsync(HttpMethod method, string url)
    {
        var token = await _auth.GetTokenAsync();
        if (string.IsNullOrEmpty(token)) return null;
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private async Task<bool> PostAsync(string url)
    {
        var request = await CreateAsync(HttpMethod.Post, url);
        if (request == null) return false;
        var response = await _http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }
}
