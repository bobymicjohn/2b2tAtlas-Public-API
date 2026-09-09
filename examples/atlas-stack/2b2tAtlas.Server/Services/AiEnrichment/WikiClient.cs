using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace _2b2tAtlas.Server.Services.AiEnrichment;

/// <summary>A wiki search hit: a candidate page title with the search engine's relevance score.</summary>
/// <param name="Title">The page title.</param>
/// <param name="Score">The MediaWiki search score (higher is more relevant).</param>
public readonly record struct WikiSearchHit(string Title, double Score);

/// <summary>A fetched wiki page: its title, canonical URL, plaintext intro, and raw wikitext.</summary>
/// <param name="Title">The canonical page title.</param>
/// <param name="Url">The canonical article URL.</param>
/// <param name="Intro">The plaintext lead section, used as description source material.</param>
/// <param name="Wikitext">The raw wikitext of the main slot, mined for coordinates.</param>
public readonly record struct WikiPage(string Title, string Url, string Intro, string Wikitext);

/// <summary>
/// A read-only MediaWiki client for the approved 2b2t wiki. It performs full-text search and page fetches
/// via the public <c>api.php</c>, always sending the configured identifying User-Agent and never editing.
/// All content is attributed to the wiki under its license by callers.
/// </summary>
public sealed class WikiClient
{
    private readonly HttpClient _http;
    private readonly AiEnrichmentOptions _options;

    /// <summary>Initializes the client with a configured HTTP client and enrichment options.</summary>
    /// <param name="http">The typed HTTP client used for wiki requests.</param>
    /// <param name="options">The enrichment options supplying the wiki endpoints and User-Agent.</param>
    public WikiClient(HttpClient http, IOptions<AiEnrichmentOptions> options)
    {
        _http = http;
        _options = options.Value;
        if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", _options.UserAgent);
    }

    /// <summary>Searches article space for pages relevant to the given name.</summary>
    /// <param name="query">The location name to search for.</param>
    /// <param name="limit">The maximum number of hits to return.</param>
    /// <param name="cancellationToken">Token that cancels the request.</param>
    /// <returns>The search hits ordered by relevance; empty on failure or no results.</returns>
    public async Task<IReadOnlyList<WikiSearchHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var url = $"{_options.WikiApiBase}?action=query&list=search&srnamespace=0&srlimit={limit}" +
                  $"&srsearch={Uri.EscapeDataString(query)}&format=json&formatversion=2";
        try
        {
            var payload = await _http.GetFromJsonAsync<SearchEnvelope>(url, cancellationToken);
            var hits = payload?.Query?.Search;
            if (hits is null)
                return [];
            return hits
                .Where(h => !string.IsNullOrWhiteSpace(h.Title))
                .Select(h => new WikiSearchHit(h.Title!, h.Score))
                .ToArray();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return [];
        }
    }

    /// <summary>Fetches a single page's intro text, canonical URL, and raw wikitext.</summary>
    /// <param name="title">The exact page title to fetch.</param>
    /// <param name="cancellationToken">Token that cancels the request.</param>
    /// <returns>The page, or <see langword="null"/> when it is missing or the request fails.</returns>
    public async Task<WikiPage?> GetPageAsync(string title, CancellationToken cancellationToken)
    {
        var url = $"{_options.WikiApiBase}?action=query&prop=extracts%7Cinfo%7Crevisions" +
                  "&exintro=1&explaintext=1&inprop=url&rvprop=content&rvslots=main" +
                  $"&titles={Uri.EscapeDataString(title)}&format=json&formatversion=2";
        try
        {
            var payload = await _http.GetFromJsonAsync<PageEnvelope>(url, cancellationToken);
            var page = payload?.Query?.Pages?.FirstOrDefault();
            if (page is null || page.Missing || string.IsNullOrWhiteSpace(page.Title))
                return null;
            var wikitext = page.Revisions?.FirstOrDefault()?.Slots?.Main?.Content ?? string.Empty;
            var canonicalUrl = string.IsNullOrWhiteSpace(page.CanonicalUrl)
                ? _options.WikiSiteBase + Uri.EscapeDataString(page.Title!.Replace(' ', '_'))
                : page.CanonicalUrl!;
            return new WikiPage(page.Title!, canonicalUrl, page.Extract ?? string.Empty, wikitext);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private sealed record SearchEnvelope([property: JsonPropertyName("query")] SearchQuery? Query);

    private sealed record SearchQuery([property: JsonPropertyName("search")] List<SearchResult>? Search);

    private sealed record SearchResult(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("score")] double Score);

    private sealed record PageEnvelope([property: JsonPropertyName("query")] PageQuery? Query);

    private sealed record PageQuery([property: JsonPropertyName("pages")] List<PageResult>? Pages);

    private sealed record PageResult(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("missing")] bool Missing,
        [property: JsonPropertyName("extract")] string? Extract,
        [property: JsonPropertyName("canonicalurl")] string? CanonicalUrl,
        [property: JsonPropertyName("revisions")] List<PageRevision>? Revisions);

    private sealed record PageRevision([property: JsonPropertyName("slots")] RevisionSlots? Slots);

    private sealed record RevisionSlots([property: JsonPropertyName("main")] RevisionSlot? Main);

    private sealed record RevisionSlot([property: JsonPropertyName("content")] string? Content);
}
