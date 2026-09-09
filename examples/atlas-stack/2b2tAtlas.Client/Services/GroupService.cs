using System.Net.Http.Headers;
using System.Net.Http.Json;
using Atlas;

namespace _2b2tAtlas.Client.Services;

/// <summary>
/// Client-side service for 2b2t groups (GAMEPLAN §15). Reads are public; writes
/// attach the current user's JWT and require the <c>groups.manage</c> permission.
/// </summary>
public class GroupService
{
    private readonly HttpClient _http;
    private readonly AuthService _auth;

    /// <summary>Initializes the group API client.</summary>
    /// <param name="http">The client used for relative <c>api/groups</c> requests.</param>
    /// <param name="auth">The source of bearer tokens for protected writes.</param>
    public GroupService(HttpClient http, AuthService auth)
    {
        _http = http;
        _auth = auth;
    }

    /// <summary>Fetches all groups, or an empty list if the request fails.</summary>
    public async Task<List<Group>> GetGroupsAsync()
    {
        try
        {
            return await _http.GetFromJsonAsync<List<Group>>("api/groups") ?? new List<Group>();
        }
        catch
        {
            return new List<Group>();
        }
    }

    /// <summary>Fetches one group with its attributed locations and highways.</summary>
    public async Task<Group?> GetGroupAsync(int id)
    {
        try
        {
            return await _http.GetFromJsonAsync<Group>($"api/groups/{id}");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Creates a group. Returns the saved group or an error message.</summary>
    public async Task<(bool Ok, Group? Saved, string? Error)> CreateAsync(Group group)
    {
        var req = await BuildRequestAsync(HttpMethod.Post, "api/groups", group);
        if (req == null) return (false, null, "Not authenticated");
        return await SendAsync(req);
    }

    /// <summary>Updates an existing group. Returns the saved group or an error message.</summary>
    public async Task<(bool Ok, Group? Saved, string? Error)> UpdateAsync(int id, Group group)
    {
        var req = await BuildRequestAsync(HttpMethod.Put, $"api/groups/{id}", group);
        if (req == null) return (false, null, "Not authenticated");
        return await SendAsync(req);
    }

    /// <summary>Deletes a group. Returns true on success.</summary>
    public async Task<bool> DeleteAsync(int id)
    {
        var token = await _auth.GetTokenAsync();
        if (string.IsNullOrEmpty(token)) return false;
        var req = new HttpRequestMessage(HttpMethod.Delete, $"api/groups/{id}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await _http.SendAsync(req);
        return resp.IsSuccessStatusCode;
    }

    private async Task<HttpRequestMessage?> BuildRequestAsync(HttpMethod method, string url, Group body)
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

    private async Task<(bool Ok, Group? Saved, string? Error)> SendAsync(HttpRequestMessage req)
    {
        try
        {
            var resp = await _http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                var saved = await resp.Content.ReadFromJsonAsync<Group>();
                return (true, saved, null);
            }
            var msg = resp.StatusCode == System.Net.HttpStatusCode.Forbidden
                ? "You don't have permission to do that."
                : $"Save failed ({(int)resp.StatusCode}).";
            return (false, null, msg);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }
}
