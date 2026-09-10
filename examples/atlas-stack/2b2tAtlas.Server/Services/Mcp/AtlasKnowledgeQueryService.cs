using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using _2b2tAtlas.Server.Models;

#pragma warning disable CS1591 // Public result records are described by MCP tool output schemas.

namespace _2b2tAtlas.Server.Services.Mcp;

/// <summary>
/// Bounded, read-only knowledge queries shared by the public MCP surface. This service talks to
/// the Atlas database directly so MCP never loops through the HTTP API or duplicates its data.
/// </summary>
public sealed class AtlasKnowledgeQueryService
{
    private readonly AtlasContext _context;

    public AtlasKnowledgeQueryService(AtlasContext context) => _context = context;

    public async Task<IReadOnlyList<LocationSummary>> SearchLocationsAsync(
        string? query, int? dimension, string? group, string? type, bool? hasRender,
        bool? hasWorldDownload, int limit, CancellationToken cancellationToken)
    {
        limit = ClampLimit(limit);
        var rows = _context.Locations.AsNoTracking().AsQueryable();
        var term = Clean(query);
        if (term is not null)
        {
            var pattern = $"%{EscapeLike(term)}%";
            rows = rows.Where(location =>
                EF.Functions.Like(location.Name, pattern, "\\") ||
                (location.Description != null && EF.Functions.Like(location.Description, pattern, "\\")) ||
                (location.Tags != null && EF.Functions.Like(location.Tags, pattern, "\\")) ||
                location.Warps.Any(warp => EF.Functions.Like(warp.Name, pattern, "\\")));
        }

        if (dimension.HasValue) rows = rows.Where(location => location.Dimension == NormalizeDimension(dimension.Value));
        var groupTerm = Clean(group);
        if (groupTerm is not null)
        {
            var groupIds = await ResolveGroupIdsAsync(groupTerm, cancellationToken);
            if (groupIds.Count == 0) return [];
            rows = rows.Where(location => location.LocationGroups.Any(link => groupIds.Contains(link.GroupId)));
        }

        var typeTerm = Clean(type);
        if (typeTerm is not null)
        {
            var typePattern = $"%{EscapeLike(typeTerm)}%";
            rows = rows.Where(location => location.Tags != null && EF.Functions.Like(location.Tags, typePattern, "\\"));
        }

        if (hasRender.HasValue)
            rows = rows.Where(location => location.Renders.Any(render => render.IsPublic == 1) == hasRender.Value);
        if (hasWorldDownload.HasValue)
            rows = rows.Where(location =>
                (location.Warps.Any(warp => warp.ArchiveSha256 != null && warp.ArchiveSha256.Length == 64 &&
                    warp.Source != null && warp.Source.StartsWith("The Archive automated sync")) ||
                _context.IngestionJobs.Any(job => job.RenderId.HasValue &&
                    location.Renders.Any(render => render.Id == job.RenderId.Value) && job.Status == "completed" &&
                    job.WarpId == null && job.ArchiveSha256 != null && job.ArchiveSha256.Length == 64)) == hasWorldDownload.Value);

        var result = await rows
            .OrderBy(location => term != null && location.Name == term ? 0 : 1)
            .ThenBy(location => term != null && location.Name.StartsWith(term) ? 0 : 1)
            .ThenBy(location => location.Name)
            .Take(limit)
            .Select(location => new LocationSummary(
                location.Rowid,
                location.Name,
                DimensionName(location.Dimension),
                location.X,
                location.Y,
                location.Z,
                location.Description == null ? null : Truncate(NormalizePublicText(location.Description)!, 360),
                location.LocationGroups.OrderBy(link => link.Group.Name).Select(link => link.Group.Name).ToArray(),
                location.Warps.Count,
                location.Renders.Count(render => render.IsPublic == 1),
                location.Attachments.Count,
                PublicAtlasUrls.Location(location.Rowid),
                PublicAtlasUrls.LocationInteractive(location.Rowid),
                PublicAtlasUrls.LocationApi(location.Rowid)))
            .ToListAsync(cancellationToken);
        return result;
    }

    public async Task<LocationDetail?> GetLocationAsync(int id, CancellationToken cancellationToken)
    {
        var location = await _context.Locations.AsNoTracking()
            .AsSplitQuery()
            .Include(row => row.Warps)
            .Include(row => row.Renders)
            .Include(row => row.Attachments)
            .Include(row => row.LocationGroups).ThenInclude(link => link.Group)
            .SingleOrDefaultAsync(row => row.Rowid == id, cancellationToken);
        if (location is null) return null;

        var renderIds = location.Renders.Where(render => render.IsPublic == 1).Select(render => render.Id).ToList();
        var legacyJobs = await _context.IngestionJobs.AsNoTracking()
            .Where(job => job.RenderId.HasValue && renderIds.Contains(job.RenderId.Value) &&
                job.Status == "completed" && job.WarpId == null && job.ArchiveSha256 != null)
            .OrderByDescending(job => job.Id)
            .ToListAsync(cancellationToken);
        var legacyByRender = legacyJobs.Where(IsPublicRenderSourceJob)
            .GroupBy(job => job.RenderId!.Value).ToDictionary(grouping => grouping.Key, grouping => grouping.First());

        var warps = location.Warps.OrderBy(warp => warp.Id).Select(warp => MapWarp(warp, location.Name)).ToArray();
        var renders = location.Renders.Where(render => render.IsPublic == 1).OrderBy(render => render.Id)
            .Select(render => MapRender(render, location.Name, legacyByRender.GetValueOrDefault(render.Id))).ToArray();
        return new LocationDetail(
            location.Rowid,
            location.LocationUuid,
            location.Name,
            NormalizePublicText(location.Description),
            location.Tags,
            DimensionName(location.Dimension),
            location.X,
            location.Y,
            location.Z,
            ParseDate(location.DateAddedUtc),
            ParseDate(location.ModifiedUtc),
            location.Wiki,
            location.VideoUrl,
            location.LocationGroups.OrderBy(link => link.Group.Name).Select(link => new GroupLink(
                link.GroupId, link.Group.Name, link.Role, PublicAtlasUrls.Group(link.GroupId),
                PublicAtlasUrls.GroupInteractive(link.GroupId), PublicAtlasUrls.GroupApi(link.GroupId))).ToArray(),
            warps,
            renders,
            location.Attachments.OrderBy(attachment => attachment.Id).Select(attachment => new AttachmentRecord(
                attachment.Id, attachment.FileName, attachment.MediaType, NormalizePublicText(attachment.Caption),
                NormalizePublicText(attachment.Attribution), attachment.FilePath, attachment.ThumbnailPath,
                attachment.SourceUrl, PublicAtlasUrls.AttachmentApi(attachment.Id))).ToArray(),
            PublicAtlasUrls.Location(location.Rowid),
            PublicAtlasUrls.LocationInteractive(location.Rowid),
            PublicAtlasUrls.LocationApi(location.Rowid));
    }

    public async Task<IReadOnlyList<NearbyLocation>> FindLocationsNearAsync(
        int x, int z, int radius, int dimension, int limit, CancellationToken cancellationToken)
    {
        radius = Math.Clamp(radius, 1, 30_000_000);
        limit = ClampLimit(limit);
        dimension = NormalizeDimension(dimension);
        var minX = Math.Max((long)int.MinValue, (long)x - radius);
        var maxX = Math.Min((long)int.MaxValue, (long)x + radius);
        var minZ = Math.Max((long)int.MinValue, (long)z - radius);
        var maxZ = Math.Min((long)int.MaxValue, (long)z + radius);
        var candidates = await _context.Locations.AsNoTracking()
            .Where(location => location.Dimension == dimension && location.X >= minX && location.X <= maxX &&
                location.Z >= minZ && location.Z <= maxZ)
            .Select(location => new { location.Rowid, location.Name, location.X, location.Y, location.Z })
            .ToListAsync(cancellationToken);
        var radiusSquared = (double)radius * radius;
        return candidates.Select(location => new
            {
                location,
                DistanceSquared = Math.Pow((double)location.X - x, 2) + Math.Pow((double)location.Z - z, 2),
            })
            .Where(item => item.DistanceSquared <= radiusSquared)
            .OrderBy(item => item.DistanceSquared)
            .Take(limit)
            .Select(item => new NearbyLocation(item.location.Rowid, item.location.Name, DimensionName(dimension),
                item.location.X, item.location.Y, item.location.Z, Math.Sqrt(item.DistanceSquared),
                PublicAtlasUrls.Location(item.location.Rowid), PublicAtlasUrls.LocationInteractive(item.location.Rowid)))
            .ToArray();
    }

    public async Task<IReadOnlyList<LocationSummary>> FindByCaptureDateAsync(
        DateOnly from, DateOnly to, int? dimension, int limit, CancellationToken cancellationToken)
    {
        if (to < from) (from, to) = (to, from);
        var candidateIds = await _context.Warps.AsNoTracking()
            .Where(warp => warp.WorldDownloadDate != null)
            .Select(warp => new { warp.LocationRowid, warp.WorldDownloadDate })
            .Concat(_context.Renders.AsNoTracking().Where(render => render.IsPublic == 1 && render.WorldDownloadDate != null)
                .Select(render => new { LocationRowid = (int?)render.LocationRowid, render.WorldDownloadDate }))
            .ToListAsync(cancellationToken);
        var ids = candidateIds.Where(item => item.LocationRowid.HasValue && TryParseDateOnly(item.WorldDownloadDate, out var date) && date >= from && date <= to)
            .Select(item => item.LocationRowid!.Value).Distinct().ToHashSet();
        if (ids.Count == 0) return [];
        return await SearchLocationsByIdsAsync(ids, dimension, limit, cancellationToken);
    }

    public async Task<IReadOnlyList<LocationSummary>> FindPreservedBuildsAsync(
        string? query, int? dimension, string? group, string? fromDate, string? toDate,
        int limit, CancellationToken cancellationToken)
    {
        var rows = await SearchLocationsAsync(query, dimension, group, null, true, true, 100, cancellationToken);
        if (string.IsNullOrWhiteSpace(fromDate) && string.IsNullOrWhiteSpace(toDate)) return rows.Take(ClampLimit(limit)).ToArray();
        var from = DateOnly.TryParse(fromDate, out var parsedFrom) ? parsedFrom : DateOnly.MinValue;
        var to = DateOnly.TryParse(toDate, out var parsedTo) ? parsedTo : DateOnly.MaxValue;
        var dated = await FindByCaptureDateAsync(from, to, dimension, 100, cancellationToken);
        var datedIds = dated.Select(row => row.Id).ToHashSet();
        return rows.Where(row => datedIds.Contains(row.Id)).Take(ClampLimit(limit)).ToArray();
    }

    public async Task<LocationResearch?> ResearchLocationAsync(string name, CancellationToken cancellationToken)
    {
        var candidates = await SearchLocationsAsync(name, null, null, null, null, null, 8, cancellationToken);
        var best = candidates.FirstOrDefault();
        if (best is null) return null;
        var detail = await GetLocationAsync(best.Id, cancellationToken);
        if (detail is null) return null;
        var nearby = await FindLocationsNearAsync(detail.X, detail.Z, 10_000, NormalizeDimension(detail.Dimension), 12, cancellationToken);
        return new LocationResearch(detail, candidates, nearby.Where(item => item.Id != detail.Id).ToArray(),
            "Search results are deterministic Atlas records. Descriptions and attributions should be cited to their canonical URLs and checked against linked sources.");
    }

    public async Task<IReadOnlyList<GroupSummary>> SearchGroupsAsync(string? query, string? type, int limit, CancellationToken cancellationToken)
    {
        limit = ClampLimit(limit);
        var rows = _context.Groups.AsNoTracking().AsQueryable();
        var typeTerm = Clean(type);
        if (typeTerm is not null) rows = rows.Where(group => EF.Functions.Like(group.Type, typeTerm));
        var candidates = await rows.Select(group => new GroupSummary(group.Id, group.Name,
                Atlas.GroupAliases.For(group.Name).ToArray(), group.Type, group.Status, group.Founded,
                group.Description == null ? null : Truncate(NormalizePublicText(group.Description)!, 360), group.LocationGroups.Count,
                group.HighwayGroups.Count(link => link.Highway.Visibility == "Public" && link.Highway.ReviewStatus == "Approved"),
                PublicAtlasUrls.Group(group.Id), PublicAtlasUrls.GroupInteractive(group.Id), PublicAtlasUrls.GroupApi(group.Id)))
            .ToListAsync(cancellationToken);
        var term = Clean(query);
        if (term is null) return candidates.OrderBy(group => group.Name).Take(limit).ToArray();
        var normalized = NormalizeSearch(term);
        return candidates.Where(group => NormalizeSearch(group.Name).Contains(normalized, StringComparison.Ordinal) ||
                group.Aliases.Any(alias => NormalizeSearch(alias).Contains(normalized, StringComparison.Ordinal)) ||
                NormalizeSearch(group.DescriptionExcerpt).Contains(normalized, StringComparison.Ordinal))
            .OrderBy(group => NormalizeSearch(group.Name) == normalized ? 0 : 1)
            .ThenBy(group => group.Name).Take(limit).ToArray();
    }

    public async Task<GroupDetail?> GetGroupAsync(int id, CancellationToken cancellationToken)
    {
        var group = await _context.Groups.AsNoTracking().AsSplitQuery()
            .Include(row => row.LocationGroups).ThenInclude(link => link.Location)
            .Include(row => row.HighwayGroups).ThenInclude(link => link.Highway)
            .SingleOrDefaultAsync(row => row.Id == id, cancellationToken);
        if (group is null) return null;
        var locationIds = group.LocationGroups.Select(link => link.LocationRowid).ToList();
        var renderCounts = await _context.Renders.AsNoTracking().Where(render => locationIds.Contains(render.LocationRowid) && render.IsPublic == 1)
            .GroupBy(render => render.LocationRowid).Select(items => new { Id = items.Key, Count = items.Count() })
            .ToDictionaryAsync(item => item.Id, item => item.Count, cancellationToken);
        return new GroupDetail(group.Id, group.Name, Atlas.GroupAliases.For(group.Name).ToArray(), group.Type,
            NormalizePublicText(group.Description), group.Founded, group.Status, group.WikiUrl, group.WebsiteUrl, group.DiscordUrl,
            group.LogoUrl,
            group.LocationGroups.OrderBy(link => link.Location.Name).Select(link => new GroupBuild(
                link.LocationRowid, link.Location.Name, link.Role, DimensionName(link.Location.Dimension),
                link.Location.X, link.Location.Y, link.Location.Z, renderCounts.GetValueOrDefault(link.LocationRowid),
                PublicAtlasUrls.Location(link.LocationRowid), PublicAtlasUrls.LocationInteractive(link.LocationRowid),
                PublicAtlasUrls.LocationApi(link.LocationRowid))).ToArray(),
            group.HighwayGroups.Where(link => link.Highway.Visibility == "Public" && link.Highway.ReviewStatus == "Approved")
                .OrderBy(link => link.Highway.Name).Select(link => new HighwayLink(link.HighwayId, link.Highway.Name,
                    link.Role, link.Evidence, DimensionName(link.Highway.Dimension), PublicAtlasUrls.HighwayApi(link.HighwayId),
                    PublicAtlasUrls.HighwayMap(link.Highway.Dimension))).ToArray(),
            PublicAtlasUrls.Group(group.Id), PublicAtlasUrls.GroupInteractive(group.Id), PublicAtlasUrls.GroupApi(group.Id));
    }

    public async Task<IReadOnlyList<GroupBuild>> GetGroupBuildsAsync(int groupId, int limit, CancellationToken cancellationToken)
    {
        var detail = await GetGroupAsync(groupId, cancellationToken);
        return detail?.Builds.Take(ClampLimit(limit)).ToArray() ?? [];
    }

    public async Task<IReadOnlyList<HighwaySummary>> SearchHighwaysAsync(
        string? query, int? dimension, string? group, int limit, CancellationToken cancellationToken)
    {
        limit = ClampLimit(limit);
        var rows = _context.Highways.AsNoTracking()
            .Where(highway => highway.Visibility == "Public" && highway.ReviewStatus == "Approved");
        var term = Clean(query);
        if (term is not null)
        {
            var pattern = $"%{EscapeLike(term)}%";
            rows = rows.Where(highway => EF.Functions.Like(highway.Name, pattern, "\\") ||
                (highway.Description != null && EF.Functions.Like(highway.Description, pattern, "\\")));
        }
        if (dimension.HasValue) rows = rows.Where(highway => highway.Dimension == NormalizeDimension(dimension.Value));
        var groupTerm = Clean(group);
        if (groupTerm is not null)
        {
            var groupIds = await ResolveGroupIdsAsync(groupTerm, cancellationToken);
            if (groupIds.Count == 0) return [];
            rows = rows.Where(highway => highway.HighwayGroups.Any(link => groupIds.Contains(link.GroupId)));
        }
        return await rows.OrderBy(highway => highway.Name).Take(limit).Select(highway => new HighwaySummary(
            highway.Id, highway.Name, highway.Slug, DimensionName(highway.Dimension), highway.Category,
            highway.Status, highway.LengthBlocks, highway.Width,
            highway.HighwayGroups.OrderBy(link => link.Group.Name).Select(link => link.Group.Name).ToArray(),
            PublicAtlasUrls.HighwayApi(highway.Id), PublicAtlasUrls.HighwayMap(highway.Dimension))).ToListAsync(cancellationToken);
    }

    public async Task<HighwayDetail?> GetHighwayAsync(int id, CancellationToken cancellationToken)
    {
        var highway = await _context.Highways.AsNoTracking().Include(row => row.HighwayGroups).ThenInclude(link => link.Group)
            .SingleOrDefaultAsync(row => row.Id == id && row.Visibility == "Public" && row.ReviewStatus == "Approved", cancellationToken);
        if (highway is null) return null;
        return new HighwayDetail(highway.Id, highway.Name, highway.Slug, DimensionName(highway.Dimension), highway.Category,
            ParsePoints(highway.PointsJson), highway.RingRadius, highway.Width, highway.Height, highway.YLevel,
            highway.Paved == 1, highway.PavingMaterial, highway.Walls == 1, highway.Enclosed == 1,
            highway.IsRoofHighway == 1, highway.Lit == 1, highway.Status, highway.LengthBlocks,
            NormalizePublicText(highway.Description), highway.WikiUrl, highway.VideoUrl,
            highway.HighwayGroups.OrderBy(link => link.Group.Name).Select(link => new GroupLink(link.GroupId,
                link.Group.Name, link.Role, PublicAtlasUrls.Group(link.GroupId), PublicAtlasUrls.GroupInteractive(link.GroupId),
                PublicAtlasUrls.GroupApi(link.GroupId))).ToArray(), PublicAtlasUrls.HighwayApi(highway.Id),
            PublicAtlasUrls.HighwayMap(highway.Dimension));
    }

    public async Task<IReadOnlyList<WarpRecord>> GetWarpsAsync(int locationId, CancellationToken cancellationToken)
    {
        var locationName = await _context.Locations.AsNoTracking().Where(location => location.Rowid == locationId)
            .Select(location => location.Name).SingleOrDefaultAsync(cancellationToken);
        if (locationName is null) return [];
        var rows = await _context.Warps.AsNoTracking().Where(warp => warp.LocationRowid == locationId)
            .OrderBy(warp => warp.Id).ToListAsync(cancellationToken);
        return rows.Select(warp => MapWarp(warp, locationName)).ToArray();
    }

    public async Task<IReadOnlyList<WorldDownloadRecord>> GetWorldDownloadsAsync(int locationId, CancellationToken cancellationToken)
    {
        var detail = await GetLocationAsync(locationId, cancellationToken);
        if (detail is null) return [];
        return detail.Warps.Where(warp => warp.WorldDownloadUrl is not null).Select(warp => new WorldDownloadRecord(
                "archive-warp", warp.Id, warp.Name, warp.WorldDownloadDate, warp.Source, "bounded-footprint",
                warp.ArchiveSha256, warp.WorldDownloadUrl!, warp.WorldDownloadMetadataUrl!, detail.CanonicalUrl))
            .Concat(detail.Renders.Where(render => render.WorldDownloadUrl is not null).Select(render => new WorldDownloadRecord(
                "preserved-render-source", render.Id, render.Name, render.WorldDownloadDate, render.Source,
                "preserved-render-source", render.WorldDownloadSha256, render.WorldDownloadUrl!,
                render.WorldDownloadMetadataUrl!, detail.CanonicalUrl))).ToArray();
    }

    public async Task<IReadOnlyList<RenderRecord>> GetRenderMetadataAsync(int locationId, CancellationToken cancellationToken)
    {
        var detail = await GetLocationAsync(locationId, cancellationToken);
        return detail?.Renders ?? [];
    }

    public async Task<DatasetStats> GetDatasetStatsAsync(CancellationToken cancellationToken)
    {
        var locations = await _context.Locations.AsNoTracking().CountAsync(cancellationToken);
        var groups = await _context.Groups.AsNoTracking().CountAsync(cancellationToken);
        var highways = await _context.Highways.AsNoTracking().CountAsync(row => row.Visibility == "Public" && row.ReviewStatus == "Approved", cancellationToken);
        var warps = await _context.Warps.AsNoTracking().CountAsync(cancellationToken);
        var renders = await _context.Renders.AsNoTracking().CountAsync(row => row.IsPublic == 1, cancellationToken);
        var attachments = await _context.Attachments.AsNoTracking().CountAsync(cancellationToken);
        var archiveDownloads = await _context.Warps.AsNoTracking().CountAsync(warp => warp.ArchiveSha256 != null &&
            warp.ArchiveSha256.Length == 64 && warp.Source != null && warp.Source.StartsWith("The Archive automated sync"), cancellationToken);
        var preservedDownloads = await _context.IngestionJobs.AsNoTracking().CountAsync(job => job.Status == "completed" &&
            job.WarpId == null && job.RenderId.HasValue && job.ArchiveSha256 != null && job.ArchiveSha256.Length == 64, cancellationToken);
        return new DatasetStats(DateTime.UtcNow, locations, groups, highways, warps, renders, attachments,
            archiveDownloads + preservedDownloads, "https://atlas.example/dataset.json",
            "https://atlas.example/llms.txt", "http://127.0.0.1:5297/openapi/v1.json",
            "http://127.0.0.1:5297/mcp");
    }

    private async Task<IReadOnlyList<LocationSummary>> SearchLocationsByIdsAsync(
        HashSet<int> ids, int? dimension, int limit, CancellationToken cancellationToken)
    {
        var rows = _context.Locations.AsNoTracking().Where(location => ids.Contains(location.Rowid));
        if (dimension.HasValue) rows = rows.Where(location => location.Dimension == NormalizeDimension(dimension.Value));
        return await rows.OrderBy(location => location.Name).Take(ClampLimit(limit)).Select(location => new LocationSummary(
            location.Rowid, location.Name, DimensionName(location.Dimension), location.X, location.Y, location.Z,
            location.Description == null ? null : Truncate(NormalizePublicText(location.Description)!, 360),
            location.LocationGroups.OrderBy(link => link.Group.Name).Select(link => link.Group.Name).ToArray(),
            location.Warps.Count, location.Renders.Count(render => render.IsPublic == 1), location.Attachments.Count,
            PublicAtlasUrls.Location(location.Rowid), PublicAtlasUrls.LocationInteractive(location.Rowid),
            PublicAtlasUrls.LocationApi(location.Rowid))).ToListAsync(cancellationToken);
    }

    private static WarpRecord MapWarp(Warp warp, string? locationName)
    {
        var downloadable = IsPublicArchiveWarp(warp);
        return new WarpRecord(warp.Id, warp.Name, warp.TimeAdded, warp.WorldDownloadDate, warp.Source,
            warp.ArchiveX, warp.ArchiveY, warp.ArchiveZ, warp.ArchiveSha256,
            downloadable ? PublicAtlasUrls.WorldDownload(
                warp.Id,
                Atlas.ArchiveWarpResolver.IsSinglePlayerConcept(warp.Name)
                    ? $"{locationName ?? "2b2t-location"} singleplayer concept"
                    : locationName, warp.ArchiveSha256) : null,
            downloadable ? PublicAtlasUrls.WorldDownloadMetadata(warp.Id) : null,
            PublicAtlasUrls.WarpApi(warp.Id));
    }

    private static RenderRecord MapRender(Render render, string? locationName, IngestionJob? sourceJob) => new(
        render.Id, render.Name, render.Description, render.Source, DimensionName(render.Dimension), render.Scale,
        render.ArchiveWarpId, render.WorldDownloadDate, render.TilesPath, render.PreviewImagePath,
        render.MinX, render.MinZ, render.MaxXExclusive, render.MaxZExclusive, render.MaxNativeZoom,
        render.CoordinateScheme, render.HasDayNight == 1,
        sourceJob is null ? null : PublicAtlasUrls.RenderWorldDownload(render.Id, locationName, sourceJob.ArchiveSha256),
        sourceJob is null ? null : PublicAtlasUrls.RenderWorldDownloadMetadata(render.Id),
        sourceJob?.ArchiveSha256?.ToLowerInvariant(), PublicAtlasUrls.RenderApi(render.Id));

    private static bool IsPublicArchiveWarp(Warp warp) =>
        warp.ArchiveSha256 is { Length: 64 } sha && sha.All(Uri.IsHexDigit) &&
        warp.Source?.StartsWith("The Archive automated sync", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsPublicRenderSourceJob(IngestionJob job) =>
        job.RenderId.HasValue && job.WarpId is null && job.Status.Equals("completed", StringComparison.OrdinalIgnoreCase) &&
        job.ArchiveSha256 is { Length: 64 } sha && sha.All(Uri.IsHexDigit) && !string.IsNullOrWhiteSpace(job.Source);

    private static IReadOnlyList<CoordinatePoint> ParsePoints(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<int[][]>(json)?.Where(point => point.Length >= 2)
                .Select(point => new CoordinatePoint(point[0], point[1])).ToArray() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task<HashSet<int>> ResolveGroupIdsAsync(string term, CancellationToken cancellationToken)
    {
        var normalized = NormalizeSearch(term);
        var groups = await _context.Groups.AsNoTracking().Select(group => new { group.Id, group.Name }).ToListAsync(cancellationToken);
        return groups.Where(group => NormalizeSearch(group.Name).Contains(normalized, StringComparison.Ordinal) ||
                Atlas.GroupAliases.For(group.Name).Any(alias => NormalizeSearch(alias).Contains(normalized, StringComparison.Ordinal)))
            .Select(group => group.Id).ToHashSet();
    }

    private static int ClampLimit(int limit) => Math.Clamp(limit <= 0 ? 20 : limit, 1, 100);
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length] + "…";
    private static string? NormalizePublicText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        return value.Replace("\\r\\n", "\n", StringComparison.Ordinal)
            .Replace("rnrn", "\n\n", StringComparison.OrdinalIgnoreCase).Trim();
    }
    private static string NormalizeSearch(string? value) => string.Concat((value ?? string.Empty)
        .Where(character => char.IsLetterOrDigit(character))).ToLowerInvariant();
    private static DateTime? ParseDate(string? value) => DateTime.TryParse(value, out var date) ? date : null;
    private static bool TryParseDateOnly(string? value, out DateOnly date) => DateOnly.TryParse(value, out date) ||
        (DateTime.TryParse(value, out var timestamp) && (date = DateOnly.FromDateTime(timestamp)) != default);
    private static int NormalizeDimension(int dimension) => dimension is >= 0 and <= 2
        ? dimension
        : throw new ArgumentOutOfRangeException(nameof(dimension), "dimension must be 0 (Overworld), 1 (Nether), or 2 (End).");
    private static int NormalizeDimension(string dimension) => dimension.Trim().ToLowerInvariant() switch
    {
        "nether" or "1" => 1,
        "end" or "2" => 2,
        _ => 0,
    };
    private static string DimensionName(int dimension) => dimension switch { 1 => "Nether", 2 => "End", _ => "Overworld" };
}

public sealed record LocationSummary(int Id, string Name, string Dimension, int X, int Y, int Z,
    string? DescriptionExcerpt, IReadOnlyList<string> Groups, int WarpCount, int RenderCount, int AttachmentCount,
    string CanonicalUrl, string InteractiveUrl, string ApiUrl);
public sealed record NearbyLocation(int Id, string Name, string Dimension, int X, int Y, int Z, double DistanceBlocks,
    string CanonicalUrl, string InteractiveUrl);
public sealed record GroupLink(int Id, string Name, string Role, string CanonicalUrl, string InteractiveUrl, string ApiUrl);
public sealed record WarpRecord(int Id, string Name, string? TimeAdded, string? WorldDownloadDate, string? Source,
    double? ArchiveX, double? ArchiveY, double? ArchiveZ, string? ArchiveSha256, string? WorldDownloadUrl,
    string? WorldDownloadMetadataUrl, string ApiUrl)
{
    public bool IsSinglePlayerConcept => Atlas.ArchiveWarpResolver.IsSinglePlayerConcept(Name);
}
public sealed record RenderRecord(int Id, string Name, string? Description, string? Source, string Dimension, string Scale,
    int? ArchiveWarpId, string? WorldDownloadDate, string TileUrlTemplate, string? PreviewImageUrl,
    int? MinX, int? MinZ, int? MaxXExclusive, int? MaxZExclusive, int? MaxNativeZoom, string? CoordinateScheme,
    bool HasDayNight, string? WorldDownloadUrl, string? WorldDownloadMetadataUrl, string? WorldDownloadSha256, string ApiUrl)
{
    public bool IsSinglePlayerConcept =>
        Atlas.ArchiveWarpResolver.IsSinglePlayerConcept(Name) || Atlas.ArchiveWarpResolver.IsSinglePlayerConcept(Description);
}
public sealed record AttachmentRecord(int Id, string Name, string? MediaType, string? Caption, string? Attribution,
    string ContentUrl, string? ThumbnailUrl, string? SourceUrl, string ApiUrl);
public sealed record LocationDetail(int Id, string Uuid, string Name, string? Description, string? Tags, string Dimension,
    int X, int Y, int Z, DateTime? DateAddedUtc, DateTime? ModifiedUtc, string? WikiUrl, string? VideoUrl,
    IReadOnlyList<GroupLink> Groups, IReadOnlyList<WarpRecord> Warps, IReadOnlyList<RenderRecord> Renders,
    IReadOnlyList<AttachmentRecord> Attachments, string CanonicalUrl, string InteractiveUrl, string ApiUrl);
public sealed record LocationResearch(LocationDetail Location, IReadOnlyList<LocationSummary> SearchCandidates,
    IReadOnlyList<NearbyLocation> NearbyLocations, string EvidenceNotice);
public sealed record GroupSummary(int Id, string Name, IReadOnlyList<string> Aliases, string Type, string? Status, string? Founded,
    string? DescriptionExcerpt, int BuildCount, int HighwayCount, string CanonicalUrl, string InteractiveUrl, string ApiUrl);
public sealed record GroupBuild(int LocationId, string Name, string Role, string Dimension, int X, int Y, int Z,
    int RenderCount, string CanonicalUrl, string InteractiveUrl, string ApiUrl);
public sealed record HighwayLink(int HighwayId, string Name, string Role, string? Evidence, string Dimension,
    string ApiUrl, string MapUrl);
public sealed record GroupDetail(int Id, string Name, IReadOnlyList<string> Aliases, string Type, string? Description,
    string? Founded, string? Status, string? WikiUrl, string? WebsiteUrl, string? DiscordUrl, string? LogoUrl,
    IReadOnlyList<GroupBuild> Builds, IReadOnlyList<HighwayLink> Highways, string CanonicalUrl, string InteractiveUrl,
    string ApiUrl);
public sealed record HighwaySummary(int Id, string Name, string Slug, string Dimension, string Category, string Status,
    int? LengthBlocks, int Width, IReadOnlyList<string> Groups, string ApiUrl, string MapUrl);
public sealed record CoordinatePoint(int X, int Z);
public sealed record HighwayDetail(int Id, string Name, string Slug, string Dimension, string Category,
    IReadOnlyList<CoordinatePoint> Points, int? RingRadius, int Width, int? Height, int? YLevel, bool Paved,
    string PavingMaterial, bool Walls, bool Enclosed, bool IsRoofHighway, bool Lit, string Status, int? LengthBlocks,
    string? Description, string? WikiUrl, string? VideoUrl, IReadOnlyList<GroupLink> Groups, string ApiUrl, string MapUrl);
public sealed record WorldDownloadRecord(string Kind, int SourceId, string Name, string? CaptureDate, string? Source,
    string Scope, string? Sha256, string DownloadUrl, string MetadataUrl, string LocationCanonicalUrl)
{
    public bool IsSinglePlayerConcept => Atlas.ArchiveWarpResolver.IsSinglePlayerConcept(Name);
}
public sealed record DatasetStats(DateTime GeneratedAtUtc, int Locations, int Groups, int Highways, int Warps,
    int Renders, int Attachments, int WorldDownloads, string DatasetMetadataUrl, string LlmsUrl, string OpenApiUrl,
    string McpEndpoint)
{
    public string NocomDatasetUrl => NocomDataService.PageUrl;
    public string NocomApiUrl => "http://127.0.0.1:5297/api/nocom";
}

#pragma warning restore CS1591
