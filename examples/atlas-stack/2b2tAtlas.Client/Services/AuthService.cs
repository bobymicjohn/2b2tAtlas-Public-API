using System.Net.Http.Json;
using System.Text.Json;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Blazored.LocalStorage;
using Atlas.Auth;

namespace _2b2tAtlas.Client.Services;

/// <summary>Coordinates authentication API calls and the browser-local JWT and user-profile cache.</summary>
public class AuthService
{
    private readonly HttpClient _httpClient;
    private readonly ILocalStorageService _localStorage;

    /// <summary>Initializes the authentication service.</summary>
    /// <param name="httpClient">The client used for relative <c>api/auth</c> requests.</param>
    /// <param name="localStorage">Browser storage for the <c>authToken</c> and <c>currentUser</c> entries.</param>
    public AuthService(HttpClient httpClient, ILocalStorageService localStorage)
    {
        _httpClient = httpClient;
        _localStorage = localStorage;
    }

    /// <summary>Posts credentials to <c>api/auth/login</c> and persists the issued token and user on success.</summary>
    /// <param name="request">The login credentials.</param>
    /// <returns>The API result, or a synthesized failed result when the request or response fails.</returns>
    public async Task<AuthResponse?> LoginAsync(LoginRequest request)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("api/auth/login", request);

            if (response.IsSuccessStatusCode)
            {
                var authResponse = await response.Content.ReadFromJsonAsync<AuthResponse>();
                if (authResponse != null && authResponse.Success && !string.IsNullOrEmpty(authResponse.Token))
                {
                    await _localStorage.SetItemAsync("authToken", authResponse.Token);
                    await _localStorage.SetItemAsync("currentUser", authResponse.User);
                    return authResponse;
                }
            }

            var errorContent = await response.Content.ReadAsStringAsync();
            return new AuthResponse
            {
                Success = false,
                Message = $"Login failed: {errorContent}",
                Token = null,
                User = null
            };
        }
        catch (Exception ex)
        {
            return new AuthResponse
            {
                Success = false,
                Message = $"Login error: {ex.Message}",
                Token = null,
                User = null
            };
        }
    }

    /// <summary>Posts a registration to <c>api/auth/register</c> and persists the issued token and user on success.</summary>
    /// <param name="request">The validated registration fields.</param>
    /// <returns>The API result, or a synthesized failed result when the request or response fails.</returns>
    public async Task<AuthResponse?> RegisterAsync(RegisterRequest request)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("api/auth/register", request);

            if (response.IsSuccessStatusCode)
            {
                var authResponse = await response.Content.ReadFromJsonAsync<AuthResponse>();
                if (authResponse != null && authResponse.Success && !string.IsNullOrEmpty(authResponse.Token))
                {
                    await _localStorage.SetItemAsync("authToken", authResponse.Token);
                    await _localStorage.SetItemAsync("currentUser", authResponse.User);
                    return authResponse;
                }
            }

            var errorContent = await response.Content.ReadAsStringAsync();
            return new AuthResponse
            {
                Success = false,
                Message = $"Registration failed: {errorContent}",
                Token = null,
                User = null
            };
        }
        catch (Exception ex)
        {
            return new AuthResponse
            {
                Success = false,
                Message = $"Registration error: {ex.Message}",
                Token = null,
                User = null
            };
        }
    }

    /// <summary>Removes cached authentication data and clears the shared HTTP authorization header.</summary>
    /// <returns>A task that completes after browser storage has been updated.</returns>
    public async Task LogoutAsync()
    {
        await _localStorage.RemoveItemAsync("authToken");
        await _localStorage.RemoveItemAsync("currentUser");

        // Clear any authorization headers
        _httpClient.DefaultRequestHeaders.Authorization = null;
    }

    /// <summary>Gets the cached JWT, clearing local authentication state when it is invalid or within 30 seconds of expiry.</summary>
    /// <returns>The usable bearer token, or <see langword="null"/> when no valid local token remains.</returns>
    public async Task<string?> GetTokenAsync()
    {
        var token = await _localStorage.GetItemAsync<string>("authToken");

        // Validate token expiration
        if (!string.IsNullOrEmpty(token) && IsTokenExpired(token))
        {
            // Token is expired, clear it
            await LogoutAsync();
            return null;
        }

        return token;
    }

    /// <summary>Gets the cached user profile only when a usable local token exists.</summary>
    /// <returns>The browser-stored user profile, or <see langword="null"/> when unauthenticated or absent.</returns>
    public async Task<User?> GetCurrentUserAsync()
    {
        // First check if we have a valid token
        var token = await GetTokenAsync();
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        return await _localStorage.GetItemAsync<User>("currentUser");
    }

    /// <summary>Determines whether browser storage contains a parseable, unexpired JWT without contacting the server.</summary>
    /// <returns><see langword="true"/> when a usable local token exists; otherwise, <see langword="false"/>.</returns>
    public async Task<bool> IsAuthenticatedAsync()
    {
        var token = await GetTokenAsync();
        return !string.IsNullOrEmpty(token);
    }

    /// <summary>
    /// Validates if the JWT token is expired
    /// </summary>
    /// <param name="token">JWT token string</param>
    /// <returns>True if token is expired, false otherwise</returns>
    private bool IsTokenExpired(string token)
    {
        try
        {
            var jwtHandler = new JwtSecurityTokenHandler();

            // Check if token can be read
            if (!jwtHandler.CanReadToken(token))
            {
                return true;
            }

            var jwtToken = jwtHandler.ReadJwtToken(token);

            // Check expiration time
            var expirationTime = jwtToken.ValidTo;

            // Add a small buffer (30 seconds) to account for clock skew
            return DateTime.UtcNow.AddSeconds(30) >= expirationTime;
        }
        catch
        {
            // If we can't parse the token, consider it expired
            return true;
        }
    }

    /// <summary>
    /// Validates token and handles authentication state
    /// </summary>
    /// <returns>True if authenticated with valid token</returns>
    public async Task<bool> ValidateAuthenticationAsync()
    {
        try
        {
            var token = await _localStorage.GetItemAsync<string>("authToken");

            if (string.IsNullOrEmpty(token) || IsTokenExpired(token))
            {
                await LogoutAsync();
                return false;
            }

            // Optionally verify token with server
            return await VerifyTokenWithServerAsync(token);
        }
        catch
        {
            await LogoutAsync();
            return false;
        }
    }

    /// <summary>
    /// Verifies token validity with the server
    /// </summary>
    /// <param name="token">JWT token</param>
    /// <returns>True if token is valid on server</returns>
    private async Task<bool> VerifyTokenWithServerAsync(string token)
    {
        try
        {
            // Set the authorization header
            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            // Try a simple authenticated endpoint
            var response = await _httpClient.GetAsync("api/auth/validate");

            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            // If unauthorized, clear auth data
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                await LogoutAsync();
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Posts to <c>api/auth/update-last-login</c> with the cached bearer token and logs out on an unauthorized response.</summary>
    /// <returns><see langword="true"/> when the API accepts the update; otherwise, <see langword="false"/>.</returns>
    public async Task<bool> UpdateLastLoginAsync()
    {
        try
        {
            var token = await GetTokenAsync();
            if (string.IsNullOrEmpty(token))
                return false;

            // Set authorization header
            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var response = await _httpClient.PostAsync("api/auth/update-last-login", null);

            // Handle unauthorized response
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                await LogoutAsync();
                return false;
            }

            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}

