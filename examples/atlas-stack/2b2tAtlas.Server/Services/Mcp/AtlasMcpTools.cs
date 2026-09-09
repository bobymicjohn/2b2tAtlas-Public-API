using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

#pragma warning disable CS1591 // Tool descriptions and generated MCP schemas document the public surface.

namespace _2b2tAtlas.Server.Services.Mcp;

/// <summary>Read-only, bounded tools for exploring the public 2b2t Atlas knowledge graph.</summary>
[McpServerToolType]
public sealed class AtlasMcpTools
{
    private readonly AtlasKnowledgeQueryService _queries;

    public AtlasMcpTools(AtlasKnowledgeQueryService queries) => _queries = queries;

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Search documented 2b2t locations by name, description, tag, Archive warp, dimension, group, preservation status, or render availability. Returns at most 100 compact records with canonical links.")]
    public Task<IReadOnlyList<LocationSummary>> search_locations(
        [Description("Words from a location name, description, tag, or Archive warp. Leave empty to browse.")] string? query = null,
        [Description("Minecraft dimension: 0 Overworld, 1 Nether, or 2 End.")] int? dimension = null,
        [Description("Builder or owning group name, including a partial name.")] string? group = null,
        [Description("Location classification or tag, such as base, spawn, monument, or world border.")] string? type = null,
        [Description("True to require a public map render; false to require none; omit for either.")] bool? has_render = null,
        [Description("True to require a publicly downloadable preserved WDL; false to require none; omit for either.")] bool? has_world_download = null,
        [Description("Maximum results, from 1 to 100. Defaults to 20.")] int limit = 20,
        CancellationToken cancellationToken = default) =>
        _queries.SearchLocationsAsync(query, dimension, group, type, has_render, has_world_download, limit, cancellationToken);

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Get one canonical Atlas location with coordinates, history, groups, Archive warps, renders, attachments, WDL links, and provenance URLs.")]
    public Task<LocationDetail?> get_location(
        [Description("Persistent numeric Atlas location ID.")] int id,
        CancellationToken cancellationToken = default) => _queries.GetLocationAsync(id, cancellationToken);

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Find documented locations within a native-dimension block radius of Minecraft X/Z coordinates. Results are sorted by exact planar distance.")]
    public Task<IReadOnlyList<NearbyLocation>> find_locations_near(
        [Description("Native-dimension Minecraft X block coordinate.")] int x,
        [Description("Native-dimension Minecraft Z block coordinate.")] int z,
        [Description("Search radius in blocks, from 1 to 30,000,000.")] int radius,
        [Description("Minecraft dimension: 0 Overworld, 1 Nether, or 2 End.")] int dimension = 0,
        [Description("Maximum results, from 1 to 100. Defaults to 20.")] int limit = 20,
        CancellationToken cancellationToken = default) =>
        _queries.FindLocationsNearAsync(x, z, radius, dimension, limit, cancellationToken);

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Find locations with a dated Archive warp or public render captured within an inclusive time range.")]
    public Task<IReadOnlyList<LocationSummary>> find_locations_by_time_range(
        [Description("Inclusive start date in YYYY-MM-DD form.")] string from,
        [Description("Inclusive end date in YYYY-MM-DD form.")] string to,
        [Description("Optional Minecraft dimension: 0 Overworld, 1 Nether, or 2 End.")] int? dimension = null,
        [Description("Maximum results, from 1 to 100. Defaults to 20.")] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (!DateOnly.TryParse(from, out var fromDate) || !DateOnly.TryParse(to, out var toDate))
            throw new ArgumentException("from and to must be valid dates, preferably YYYY-MM-DD.");
        return _queries.FindByCaptureDateAsync(fromDate, toDate, dimension, limit, cancellationToken);
    }

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Find preserved 2b2t builds that have both a public map render and a downloadable bounded world snapshot, optionally filtered by group, dimension, or capture dates.")]
    public Task<IReadOnlyList<LocationSummary>> find_preserved_builds(
        [Description("Optional location or history search text.")] string? query = null,
        [Description("Optional Minecraft dimension: 0 Overworld, 1 Nether, or 2 End.")] int? dimension = null,
        [Description("Optional builder or owning group name.")] string? group = null,
        [Description("Optional inclusive earliest capture date in YYYY-MM-DD form.")] string? from_date = null,
        [Description("Optional inclusive latest capture date in YYYY-MM-DD form.")] string? to_date = null,
        [Description("Maximum results, from 1 to 100. Defaults to 20.")] int limit = 20,
        CancellationToken cancellationToken = default) =>
        _queries.FindPreservedBuildsAsync(query, dimension, group, from_date, to_date, limit, cancellationToken);

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Resolve a location name and return the best full Atlas record, alternate search candidates, nearby places, relationships, and an evidence-use reminder. Useful as one-call research context.")]
    public Task<LocationResearch?> research_location(
        [Description("Location name or distinctive search phrase.")] string name,
        CancellationToken cancellationToken = default) => _queries.ResearchLocationAsync(name, cancellationToken);

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Search documented 2b2t groups by name, history, or activity classification.")]
    public Task<IReadOnlyList<GroupSummary>> search_groups(
        [Description("Group name, alias, or words from its description. Leave empty to browse.")] string? query = null,
        [Description("Optional Atlas classification: Build, Highway, Mixed, or Other.")] string? type = null,
        [Description("Maximum results, from 1 to 100. Defaults to 20.")] int limit = 20,
        CancellationToken cancellationToken = default) => _queries.SearchGroupsAsync(query, type, limit, cancellationToken);

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Get one group with its history, aliases, links, attributed builds, attributed highways, roles, and canonical URLs.")]
    public Task<GroupDetail?> get_group(
        [Description("Persistent numeric Atlas group ID.")] int id,
        CancellationToken cancellationToken = default) => _queries.GetGroupAsync(id, cancellationToken);

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("List locations explicitly attributed to a group, including roles, coordinates, render counts, and canonical links.")]
    public Task<IReadOnlyList<GroupBuild>> get_group_builds(
        [Description("Persistent numeric Atlas group ID.")] int group_id,
        [Description("Maximum results, from 1 to 100. Defaults to 50.")] int limit = 50,
        CancellationToken cancellationToken = default) => _queries.GetGroupBuildsAsync(group_id, limit, cancellationToken);

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Search public, approved 2b2t highways and canals by name, history, dimension, or attributed group.")]
    public Task<IReadOnlyList<HighwaySummary>> search_highways(
        [Description("Highway/canal name or words from its description. Leave empty to browse.")] string? query = null,
        [Description("Optional Minecraft dimension: 0 Overworld, 1 Nether, or 2 End.")] int? dimension = null,
        [Description("Optional builder or maintainer group name.")] string? group = null,
        [Description("Maximum results, from 1 to 100. Defaults to 20.")] int limit = 20,
        CancellationToken cancellationToken = default) =>
        _queries.SearchHighwaysAsync(query, dimension, group, limit, cancellationToken);

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Get one public, approved highway or canal with native-dimension geometry, construction metadata, credited groups, evidence notes, and map/API links.")]
    public Task<HighwayDetail?> get_highway(
        [Description("Persistent numeric Atlas highway ID.")] int id,
        CancellationToken cancellationToken = default) => _queries.GetHighwayAsync(id, cancellationToken);

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("List every Archive warp linked to a location, including landing coordinates, capture date, checksum, and WDL links when available.")]
    public Task<IReadOnlyList<WarpRecord>> get_warps(
        [Description("Persistent numeric Atlas location ID.")] int location_id,
        CancellationToken cancellationToken = default) => _queries.GetWarpsAsync(location_id, cancellationToken);

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("List publicly downloadable world snapshots for a location. Returns metadata and HTTPS ZIP URLs, never binary file bytes. WDLs are bounded historical captures, not complete copies of 2b2t.")]
    public Task<IReadOnlyList<WorldDownloadRecord>> get_world_downloads(
        [Description("Persistent numeric Atlas location ID.")] int location_id,
        CancellationToken cancellationToken = default) => _queries.GetWorldDownloadsAsync(location_id, cancellationToken);

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("List public map-render metadata for a location, including tile templates, exact bounds, coordinate scheme, dates, provenance, and source-WDL links.")]
    public Task<IReadOnlyList<RenderRecord>> get_render_metadata(
        [Description("Persistent numeric Atlas location ID.")] int location_id,
        CancellationToken cancellationToken = default) => _queries.GetRenderMetadataAsync(location_id, cancellationToken);

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Return current counts and machine-readable discovery links for the public 2b2t Atlas dataset.")]
    public Task<DatasetStats> get_dataset_stats(CancellationToken cancellationToken = default) =>
        _queries.GetDatasetStatsAsync(cancellationToken);
}

/// <summary>Stable MCP resource templates for clients that prefer URI-addressed context.</summary>
[McpServerResourceType]
public sealed class AtlasMcpResources
{
    private readonly AtlasKnowledgeQueryService _queries;

    public AtlasMcpResources(AtlasKnowledgeQueryService queries) => _queries = queries;

    [McpServerResource(UriTemplate = "2b2tatlas://location/{id}", Name = "atlas_location", Title = "2b2t Atlas location", MimeType = "application/json")]
    [Description("Canonical public record for one Atlas location.")]
    public async Task<string> GetLocation(int id, CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await _queries.GetLocationAsync(id, cancellationToken), AtlasMcpJson.Options);

    [McpServerResource(UriTemplate = "2b2tatlas://group/{id}", Name = "atlas_group", Title = "2b2t Atlas group", MimeType = "application/json")]
    [Description("Canonical public record for one Atlas group and its attributed work.")]
    public async Task<string> GetGroup(int id, CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await _queries.GetGroupAsync(id, cancellationToken), AtlasMcpJson.Options);

    [McpServerResource(UriTemplate = "2b2tatlas://highway/{id}", Name = "atlas_highway", Title = "2b2t Atlas highway", MimeType = "application/json")]
    [Description("Canonical public record for one Atlas highway or canal.")]
    public async Task<string> GetHighway(int id, CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await _queries.GetHighwayAsync(id, cancellationToken), AtlasMcpJson.Options);

    [McpServerResource(UriTemplate = "2b2tatlas://dataset", Name = "atlas_dataset", Title = "2b2t Atlas dataset overview", MimeType = "application/json")]
    [Description("Current Atlas catalog counts and machine-readable discovery URLs.")]
    public async Task<string> GetDataset(CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await _queries.GetDatasetStatsAsync(cancellationToken), AtlasMcpJson.Options);
}

internal static class AtlasMcpJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
}

#pragma warning restore CS1591
