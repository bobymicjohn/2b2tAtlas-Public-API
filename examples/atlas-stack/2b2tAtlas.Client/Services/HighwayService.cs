using System.Net.Http.Headers;
using System.Net.Http.Json;
using Atlas;

namespace _2b2tAtlas.Client.Services;

/// <summary>
/// Client-side service for highway data (GAMEPLAN §14). Reads are public; writes
/// attach the current user's JWT and require the matching <c>highways.*</c> permission.
/// </summary>
public class HighwayService
{
    private readonly HttpClient _http;
    private readonly AuthService _auth;

    /// <summary>Initializes the highway API client.</summary>
    /// <param name="http">The client used for relative <c>api/highways</c> requests.</param>
    /// <param name="auth">The source of bearer tokens for moderation and protected writes.</param>
    public HighwayService(HttpClient http, AuthService auth)
    {
        _http = http;
        _auth = auth;
    }

    /// <summary>Fetches all public highways, or an empty list if the request fails.</summary>
    public async Task<List<Highway>> GetHighwaysAsync()
    {
        try
        {
            var highways = await _http.GetFromJsonAsync<List<Highway>>("api/highways");
            return highways ?? new List<Highway>();
        }
        catch
        {
            // The map falls back to its built-in highway set if this fails.
            return new List<Highway>();
        }
    }

    /// <summary>Fetches a single highway (with full attributes) by id.</summary>
    public async Task<Highway?> GetHighwayAsync(int id)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"api/highways/{id}");
            var token = await _auth.GetTokenAsync();
            if (!string.IsNullOrEmpty(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await _http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<Highway>();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Fetches highways awaiting moderation (requires submissions.moderate).</summary>
    public async Task<List<Highway>> GetPendingAsync()
    {
        var token = await _auth.GetTokenAsync();
        if (string.IsNullOrEmpty(token)) return new();
        var req = new HttpRequestMessage(HttpMethod.Get, "api/highways/pending");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return new();
        return await resp.Content.ReadFromJsonAsync<List<Highway>>() ?? new();
    }

    /// <summary>Fetches every highway regardless of status/visibility (requires highways.edit).</summary>
    public async Task<List<Highway>> GetAllAsync()
    {
        var token = await _auth.GetTokenAsync();
        if (string.IsNullOrEmpty(token)) return new();
        var req = new HttpRequestMessage(HttpMethod.Get, "api/highways/all");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return new();
        return await resp.Content.ReadFromJsonAsync<List<Highway>>() ?? new();
    }

    /// <summary>Approves a pending highway.</summary>
    public Task<bool> ApproveAsync(int id) => PostActionAsync($"api/highways/{id}/approve");

    /// <summary>Rejects a pending highway.</summary>
    public Task<bool> RejectAsync(int id) => PostActionAsync($"api/highways/{id}/reject");

    private async Task<bool> PostActionAsync(string url)
    {
        var token = await _auth.GetTokenAsync();
        if (string.IsNullOrEmpty(token)) return false;
        var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await _http.SendAsync(req);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>Creates a highway. Returns the saved highway or an error message.</summary>
    public async Task<(bool Ok, Highway? Saved, string? Error)> CreateAsync(Highway highway)
    {
        var req = await BuildRequestAsync(HttpMethod.Post, "api/highways", highway);
        if (req == null) return (false, null, "Not authenticated");
        return await SendAsync(req);
    }

    /// <summary>Updates an existing highway. Returns the saved highway or an error message.</summary>
    public async Task<(bool Ok, Highway? Saved, string? Error)> UpdateAsync(int id, Highway highway)
    {
        var req = await BuildRequestAsync(HttpMethod.Put, $"api/highways/{id}", highway);
        if (req == null) return (false, null, "Not authenticated");
        return await SendAsync(req);
    }

    /// <summary>Deletes a highway. Returns true on success.</summary>
    public async Task<bool> DeleteAsync(int id)
    {
        var token = await _auth.GetTokenAsync();
        if (string.IsNullOrEmpty(token)) return false;
        var req = new HttpRequestMessage(HttpMethod.Delete, $"api/highways/{id}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await _http.SendAsync(req);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>Read the highway-only audit stream.</summary>
    public async Task<List<HighwayChange>> GetHistoryAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/highways/history");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _auth.GetTokenAsync());
        using var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<HighwayChange>>() ?? [];
    }

    /// <summary>Restore the previewed version; only the owner can execute this.</summary>
    public async Task<(bool Ok, Highway? Saved, string? Error)> RestoreAsync(int auditId, string expectedVersion)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/highways/history/{auditId}/restore")
        { Content = JsonContent.Create(new HighwayRestoreRequest { ExpectedVersion = expectedVersion }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _auth.GetTokenAsync());
        return await SendAsync(request);
    }

    private async Task<HttpRequestMessage?> BuildRequestAsync(HttpMethod method, string url, Highway body)
    {
        var token = await _auth.GetTokenAsync();
        if (string.IsNullOrEmpty(token)) return null;
        var req = new HttpRequestMessage(method, url)
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    private async Task<(bool Ok, Highway? Saved, string? Error)> SendAsync(HttpRequestMessage req)
    {
        try
        {
            var resp = await _http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                var saved = await resp.Content.ReadFromJsonAsync<Highway>();
                return (true, saved, null);
            }
            var msg = resp.StatusCode == System.Net.HttpStatusCode.Forbidden
                ? "You don't have permission to do that."
                : $"Save failed ({(int)resp.StatusCode}).";
            try
            {
                var detail = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
                if (detail.TryGetProperty("message", out var message)) msg = message.GetString() ?? msg;
            }
            catch (System.Text.Json.JsonException) { }
            return (false, null, msg);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }
}
