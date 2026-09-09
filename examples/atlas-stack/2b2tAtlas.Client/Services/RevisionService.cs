using System.Net.Http.Headers;
using System.Net.Http.Json;
using Atlas;

namespace _2b2tAtlas.Client.Services;

/// <summary>
/// Client-side service for proposed edits (revisions) to existing Locations/Highways
/// (GAMEPLAN §15). Submitting requires a contributor permission; reviewing requires
/// <c>submissions.moderate</c>.
/// </summary>
public class RevisionService
{
    private readonly HttpClient _http;
    private readonly AuthService _auth;

    /// <summary>Initializes the revision submission and moderation API client.</summary>
    /// <param name="http">The client used for relative <c>api/revisions</c> requests.</param>
    /// <param name="auth">The source of bearer tokens for every revision request.</param>
    public RevisionService(HttpClient http, AuthService auth)
    {
        _http = http;
        _auth = auth;
    }

    /// <summary>Submits a proposed edit. Returns the saved revision or an error message.</summary>
    public async Task<(bool Ok, RevisionDto? Saved, string? Error)> SubmitAsync(RevisionDto revision)
    {
        var token = await _auth.GetTokenAsync();
        if (string.IsNullOrEmpty(token)) return (false, null, "Not authenticated");
        var req = new HttpRequestMessage(HttpMethod.Post, "api/revisions") { Content = JsonContent.Create(revision) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
            return (false, null, await resp.Content.ReadAsStringAsync());
        var saved = await resp.Content.ReadFromJsonAsync<RevisionDto>();
        return (true, saved, null);
    }

    /// <summary>Fetches revisions awaiting review (requires submissions.moderate).</summary>
    public async Task<List<RevisionDto>> GetPendingAsync()
    {
        var token = await _auth.GetTokenAsync();
        if (string.IsNullOrEmpty(token)) return new();
        var req = new HttpRequestMessage(HttpMethod.Get, "api/revisions/pending");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return new();
        return await resp.Content.ReadFromJsonAsync<List<RevisionDto>>() ?? new();
    }

    /// <summary>Approves a pending revision (applies the proposed edit).</summary>
    public Task<bool> ApproveAsync(int id) => PostActionAsync($"api/revisions/{id}/approve");

    /// <summary>Rejects a pending revision.</summary>
    public Task<bool> RejectAsync(int id) => PostActionAsync($"api/revisions/{id}/reject");

    private async Task<bool> PostActionAsync(string url)
    {
        var token = await _auth.GetTokenAsync();
        if (string.IsNullOrEmpty(token)) return false;
        var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await _http.SendAsync(req);
        return resp.IsSuccessStatusCode;
    }
}
