using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using _2b2tAtlas.Server.Models;

namespace _2b2tAtlas.Server.Services;

/// <summary>Adds reviewed historical media metadata without replacing operator attachments.</summary>
public sealed class HistoricalMediaSeeder
{
    private readonly AtlasContext _context;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<HistoricalMediaSeeder> _logger;

    /// <summary>Initializes the historical-media catalog seeder.</summary>
    public HistoricalMediaSeeder(AtlasContext context, IWebHostEnvironment environment, ILogger<HistoricalMediaSeeder> logger)
    {
        _context = context;
        _environment = environment;
        _logger = logger;
    }

    /// <summary>Adds only missing attachment URLs from the reviewed manifests.</summary>
    public async Task SeedAsync(string? manifestDirectory = null)
    {
        // Production deliberately runs with C:\AtlasExample\Api\data as the content root so SQLite
        // resolves outside the replaceable app directory. Seed manifests are published beside
        // the executable, therefore resolve them from the application base directory.
        var dataPath = manifestDirectory ?? Path.Combine(AppContext.BaseDirectory, "Data");
        var manifestPaths = new[]
        {
            Path.Combine(dataPath, "historical-location-media.json"),
            Path.Combine(dataPath, "wiki-location-media.json"),
            Path.Combine(dataPath, "youtube-location-media.json"),
            Path.Combine(dataPath, "reference-location-media.json"),
        };
        var existingManifests = manifestPaths.Where(File.Exists).ToArray();
        if (existingManifests.Length == 0)
        {
            _logger.LogWarning("Historical media manifests are missing from {Path}.", dataPath);
            return;
        }

        var items = new List<MediaSeed>();
        foreach (var manifestPath in existingManifests)
        {
            await using var stream = File.OpenRead(manifestPath);
            items.AddRange(await JsonSerializer.DeserializeAsync<List<MediaSeed>>(stream,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? []);
        }
        var locations = await _context.Locations.ToListAsync();
        // A tour/history video can cover several locations. Deduplicate within
        // each location, not globally across the entire Atlas.
        var existingPaths = (await _context.Attachments.Select(item => new { item.LocationRowid, item.FilePath }).ToListAsync())
            .Select(item => AttachmentKey(item.LocationRowid, item.FilePath))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var location in locations.Where(item => !string.IsNullOrWhiteSpace(item.VideoUrl)))
            existingPaths.Add(AttachmentKey(location.Rowid, location.VideoUrl));
        var now = DateTime.UtcNow.ToString("o");
        var added = 0;
        foreach (var item in items)
        {
            var location = item.LocationRowid is int locationRowid
                ? locations.FirstOrDefault(candidate => candidate.Rowid == locationRowid)
                : locations.SingleOrDefault(candidate =>
                    string.Equals(candidate.Name, item.LocationName, StringComparison.OrdinalIgnoreCase));
            if (location is not null && !string.Equals(location.Name, item.LocationName, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Historical media item {Path} names {ManifestName}, but location {LocationId} is {DatabaseName}; skipping.",
                    item.Path, item.LocationName, location.Rowid, location.Name);
                continue;
            }
            if (location is null || !existingPaths.Add(AttachmentKey(location.Rowid, item.Path))) continue;
            _context.Attachments.Add(new Attachment
            {
                LocationRowid = location.Rowid,
                FileName = item.FileName,
                FilePath = item.Path,
                MediaType = item.MediaType,
                ThumbnailPath = item.ThumbnailPath,
                SourceUrl = item.SourceUrl,
                Caption = item.Caption,
                Attribution = item.Attribution,
                DateAddedUtc = now,
            });
            location.ModifiedUtc = now;
            added++;
        }
        if (added > 0) await _context.SaveChangesAsync();
        _logger.LogInformation(
            "Historical media seed added {Count} reviewed attachment(s) from {ManifestCount} manifest(s).",
            added, existingManifests.Length);
    }

    private static string AttachmentKey(int locationId, string path)
    {
        // Timestamps and share/embed URLs identify the same video. YouTube IDs
        // are case-sensitive; ordinary media paths retain the old comparison.
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
        {
            string? videoId = null;
            if (uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
                videoId = uri.AbsolutePath.Trim('/');
            else if (new[] { "youtube.com", "www.youtube.com", "m.youtube.com", "www.youtube-nocookie.com" }
                     .Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            {
                var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[0] is "embed" or "shorts" or "live") videoId = parts[1];
                else if (uri.AbsolutePath == "/watch")
                    videoId = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query)["v"].FirstOrDefault();
            }
            if (videoId is not null && System.Text.RegularExpressions.Regex.IsMatch(videoId, @"\A[A-Za-z0-9_-]{11}\z"))
                return $"{locationId}\nyoutube:{videoId}";
        }
        return $"{locationId}\n{path.ToUpperInvariant()}";
    }

    private sealed record MediaSeed(int? LocationRowid, string LocationName, string FileName, string MediaType, string Path,
        string? ThumbnailPath, string? SourceUrl, string? Caption, string? Attribution);
}
