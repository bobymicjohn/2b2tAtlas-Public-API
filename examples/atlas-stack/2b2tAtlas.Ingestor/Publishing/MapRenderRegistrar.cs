using System.Net.Http.Headers;
using System.Net.Http.Json;
using Atlas;
using Atlas.Ingestor.Models;
using Atlas.Ingestor.Security;

namespace Atlas.Ingestor.Publishing;

/// <summary>Registers a verified publication with the Atlas API as an unpublished map render.</summary>
public static class MapRenderRegistrar
{
    /// <summary>Creates or updates one unpublished map-render record through the authenticated API.</summary>
    /// <param name="apiBase">Atlas API base URI; non-loopback endpoints must use HTTPS.</param>
    /// <param name="tokenEnvironmentVariable">Environment variable containing the bearer token.</param>
    /// <param name="plan">Authenticated render plan supplying public metadata.</param>
    /// <param name="dimension">Dimension being registered.</param>
    /// <param name="urlTemplate">Public tile URL template.</param>
    /// <param name="maxNativeZoom">Maximum native map zoom exposed to clients.</param>
    /// <param name="cancellationToken">Token that cancels the HTTP request or response read.</param>
    /// <returns>A task that completes after the API accepts the registration.</returns>
    /// <exception cref="InputSecurityException">A non-loopback API URI does not use HTTPS.</exception>
    /// <exception cref="InputValidationException">The token environment variable is absent or empty.</exception>
    /// <exception cref="IngestException">The API returns a non-success status.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    /// <remarks>The bearer token is read only from the named environment variable. The submitted record always has <c>IsPublished=false</c>.</remarks>
    public static async Task RegisterAsync(
        Uri apiBase,
        string tokenEnvironmentVariable,
        RenderPlan plan,
        DimensionInfo dimension,
        string urlTemplate,
        int maxNativeZoom,
        CancellationToken cancellationToken = default)
    {
        if (!apiBase.IsLoopback && apiBase.Scheme != Uri.UriSchemeHttps)
            throw new InputSecurityException("Registration API must use HTTPS except on localhost.");
        var token = Environment.GetEnvironmentVariable(tokenEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(token))
            throw new InputValidationException($"Registration token environment variable is not set: {tokenEnvironmentVariable}");

        var dto = new MapRenderDto
        {
            Slug = $"{plan.Slug}-{dimension.Key}",
            Name = dimension.Key == "overworld" ? plan.Name : $"{plan.Name} ({dimension.Key})",
            Dimension = dimension.RendererId,
            Scale = plan.Scale,
            UrlTemplate = urlTemplate,
            HasDayNight = plan.DayNight,
            MaxNativeZoom = maxNativeZoom,
            WorldDownloadDate = plan.WorldDownloadDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            Source = plan.Source,
            SortOrder = 0,
            IsPublished = false,
        };

        using var client = new HttpClient { BaseAddress = apiBase, Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.PutAsJsonAsync("api/maprenders", dto, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new IngestException($"Map render registration failed ({(int)response.StatusCode}): {body[..Math.Min(body.Length, 1000)]}");
        }
    }
}
