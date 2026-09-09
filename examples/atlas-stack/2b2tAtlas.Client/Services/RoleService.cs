using System.Net.Http.Headers;
using System.Net.Http.Json;
using Atlas.Auth;

namespace _2b2tAtlas.Client.Services;

/// <summary>
/// Client service for the role → permission matrix editor (GAMEPLAN §15).
/// All calls require the <c>users.roles.assign</c> permission (bearer-authed).
/// </summary>
public class RoleService
{
    private readonly HttpClient _http;
    private readonly AuthService _auth;

    /// <summary>Initializes the protected role-permission matrix API client.</summary>
    /// <param name="http">The client used for relative <c>api/roles</c> requests.</param>
    /// <param name="auth">The source of bearer tokens for role-profile administration.</param>
    public RoleService(HttpClient http, AuthService auth)
    {
        _http = http;
        _auth = auth;
    }

    /// <summary>Fetches the role/permission matrix, or null if unauthorized.</summary>
    public async Task<RoleMatrix?> GetMatrixAsync()
    {
        var req = await BuildAsync(HttpMethod.Get, "api/roles", null);
        if (req == null) return null;
        var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<RoleMatrix>();
    }

    /// <summary>Replaces a role's permission set. Returns the refreshed matrix.</summary>
    public async Task<RoleMatrix?> SetRoleAsync(string role, IEnumerable<string> permissions)
    {
        var req = await BuildAsync(HttpMethod.Put, $"api/roles/{role}", permissions.ToList());
        if (req == null) return null;
        var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<RoleMatrix>();
    }

    /// <summary>Reverts a role to its code-defined default permissions.</summary>
    public async Task<RoleMatrix?> ResetRoleAsync(string role)
    {
        var req = await BuildAsync(HttpMethod.Delete, $"api/roles/{role}", null);
        if (req == null) return null;
        var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<RoleMatrix>();
    }

    private async Task<HttpRequestMessage?> BuildAsync(HttpMethod method, string url, object? body)
    {
        var token = await _auth.GetTokenAsync();
        if (string.IsNullOrEmpty(token)) return null;
        var req = new HttpRequestMessage(method, url);
        if (body != null) req.Content = JsonContent.Create(body);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }
}
