using System.Globalization;
using System.Text.Json;

namespace _2b2tAtlas.Server.Services;

#pragma warning disable CS1591 // Typed public data contracts are described in API/MCP documentation.

/// <summary>Bounded, read-only historical aggregates. Never queries raw hits or decodes counts from PNG colors.</summary>
public sealed class NocomDataService
{
    public const string TileBase = "https://tiles.atlas.example/AtlasTiles/Nocom/v1/";
    public const string SourceUrl = "https://github.com/nerdsinspace/nocom-explanation/blob/main/torrent.md";
    public const string PageUrl = "https://atlas.example/nocom/";
    private static readonly string[] Caveats =
    [
        "Counts are positive loaded-chunk observations, not unique players, visits, exact player positions or ownership evidence.",
        "Scanner priorities and repeated probes affect density. Missing observations do not establish inactivity.",
        "Buckets are fixed 30-day UTC intervals, not calendar months. The final bucket extends beyond the July 15, 2021 patch; it does not imply observations continued afterward.",
        "The exploit's 2018-2021 history is broader than these published 2020-2021 aggregates. End coverage starts in February 2021.",
        "Coordinates are native Minecraft coordinates, not latitude/longitude. Source chunks cover 16 by 16 blocks; Nether projection is never applied implicitly.",
        "Raw SQL rows, individual tracks, player associations, signs and block histories are not served by these aggregate endpoints."
    ];
    private readonly NocomPeriod[] _periods;
    private readonly NocomHighwayPeriod[] _highways;
    public NocomDataset Dataset { get; }

    public NocomDataService() : this(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "nocom-world-pulse-manifest.json")),
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "nocom-highway-observations.json"))) { }

    public NocomDataService(string manifestJson, string highwayJson)
    {
        using var manifest = JsonDocument.Parse(manifestJson);
        using var highway = JsonDocument.Parse(highwayJson);
        var root = manifest.RootElement;
        _periods = root.GetProperty("frames").EnumerateArray().Select(frame =>
        {
            var dimension = Dimension(frame.GetProperty("dimension").GetString()!);
            var start = frame.GetProperty("periodStartUtc").GetDateTimeOffset();
            var key = frame.GetProperty("key").GetString()!;
            if (key != start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) throw new InvalidDataException("Nocom period key mismatch.");
            return new NocomPeriod(key, dimension, AtlasDimension(dimension), SourceDimension(dimension), start, start.AddDays(30),
                frame.GetProperty("rows").GetInt64(), frame.GetProperty("observations").GetInt64(),
                new NocomBlockBounds(frame.GetProperty("minChunkX").GetInt64() * 16, frame.GetProperty("minChunkZ").GetInt64() * 16,
                    (frame.GetProperty("maxChunkX").GetInt64() + 1) * 16 - 1, (frame.GetProperty("maxChunkZ").GetInt64() + 1) * 16 - 1),
                TileBase + $"monthly/{key}/{dimension}/{{z}}/{{y}}/{{x}}.png",
                frame.GetProperty("minNativeUrlZoom").GetInt32(), frame.GetProperty("maxNativeUrlZoom").GetInt32());
        }).OrderBy(p => p.PeriodStartUtc).ThenBy(p => p.AtlasDimension).ToArray();
        if (_periods.Length != 39 || _periods.Sum(p => p.GroupedRows) != root.GetProperty("rowCount").GetInt64()
            || _periods.Any(p => p.GroupedRows < 0 || p.Observations < p.GroupedRows)
            || _periods.DistinctBy(p => (p.Dimension, p.Key)).Count() != _periods.Length)
            throw new InvalidDataException("Unexpected Nocom release inventory.");

        _highways = highway.RootElement.GetProperty("rows").EnumerateArray().Select(row =>
        {
            var sourceDimension = row.GetProperty("sourceDimension").GetInt32();
            var dimension = sourceDimension switch { -1 => "nether", 0 => "overworld", _ => throw new InvalidDataException("Unsupported highway dimension.") };
            var dx = row.GetProperty("directionX").GetInt32(); var dz = row.GetProperty("directionZ").GetInt32();
            var direction = (dx, dz) switch { (0,-1) => "north", (1,-1) => "northeast", (1,0) => "east", (1,1) => "southeast",
                (0,1) => "south", (-1,1) => "southwest", (-1,0) => "west", (-1,-1) => "northwest", _ => throw new InvalidDataException("Invalid highway direction.") };
            var start = DateTimeOffset.FromUnixTimeMilliseconds(row.GetProperty("bucket").GetInt64() * 2_592_000_000L);
            var observations = row.GetProperty("observations").GetInt64();
            if (observations < 0 || !_periods.Any(p => p.Dimension == dimension && p.PeriodStartUtc == start))
                throw new InvalidDataException("Highway release period does not match World Pulse.");
            return new NocomHighwayPeriod(dimension, AtlasDimension(dimension), direction, dx, dz, start, start.AddDays(30), observations);
        }).OrderBy(p => p.PeriodStartUtc).ThenBy(p => p.Dimension).ThenBy(p => p.Direction).ToArray();
        if (_highways.Length != 272 || _highways.DistinctBy(p => (p.Dimension, p.Direction, p.PeriodStartUtc)).Count() != 272)
            throw new InvalidDataException("Unexpected highway release inventory.");

        var summaries = root.GetProperty("totals").EnumerateArray().Select(total =>
        {
            var dimension = Dimension(total.GetProperty("dimension").GetString()!);
            var periods = _periods.Where(p => p.Dimension == dimension).ToArray();
            return new NocomDimension(dimension, AtlasDimension(dimension), SourceDimension(dimension), periods.Length,
                periods.Sum(p => p.GroupedRows), periods.Sum(p => p.Observations), periods.Min(p => p.PeriodStartUtc),
                periods.Max(p => p.PeriodEndExclusiveUtc), TileBase + $"total/{dimension}/{{z}}/{{y}}/{{x}}.png",
                total.GetProperty("minNativeUrlZoom").GetInt32(), total.GetProperty("maxNativeUrlZoom").GetInt32());
        }).OrderBy(p => p.AtlasDimension).ToArray();
        var source = root.GetProperty("source");
        Dataset = new NocomDataset("nocom-world-pulse-v1", "Nocom World Pulse historical observations", PageUrl, SourceUrl,
            TileBase + "manifest.json", "http://127.0.0.1:5297/api/nocom/periods", "http://127.0.0.1:5297/api/nocom/highways",
            root.GetProperty("generatedAtUtc").GetDateTimeOffset(), new DateOnly(2021,7,15),
            _periods.Sum(p => p.GroupedRows), _periods.Sum(p => p.Observations), _periods.Length, summaries,
            source.GetProperty("sha256").GetString()!, source.GetProperty("serverScope").GetString()!,
            highway.RootElement.GetProperty("sha256").GetString()!, highway.RootElement.GetProperty("sourceScope").GetString()!,
            "Original Nocom release credited to Nerds Inc and contributors; upstream release terms remain separate from Atlas-authored catalog metadata.", Caveats);
    }

    public IReadOnlyList<NocomPeriod> Periods(string? dimension = null, DateOnly? from = null, DateOnly? to = null)
    {
        var normalized = dimension is null ? null : Dimension(dimension);
        if (from > to) throw new ArgumentException("from must not be after to.");
        return _periods.Where(p => (normalized is null || p.Dimension == normalized)
            && (!from.HasValue || DateOnly.FromDateTime(p.PeriodEndExclusiveUtc.UtcDateTime) > from)
            && (!to.HasValue || DateOnly.FromDateTime(p.PeriodStartUtc.UtcDateTime) <= to)).ToArray();
    }

    public IReadOnlyList<NocomHighwayPeriod> Highways(string dimension = "nether", string? direction = null)
    {
        var normalized = Dimension(dimension);
        if (normalized == "end") return [];
        var dir = direction?.Trim().ToLowerInvariant();
        if (dir is not null && !_highways.Any(p => p.Direction == dir)) throw new ArgumentException("Unknown compass direction; use north, northeast, east, southeast, south, southwest, west or northwest.");
        return _highways.Where(p => p.Dimension == normalized && (dir is null || p.Direction == dir)).ToArray();
    }

    private static string Dimension(string value) => value.Trim().ToLowerInvariant() switch
    { "overworld" => "overworld", "nether" => "nether", "end" => "end", _ => throw new ArgumentException("dimension must be overworld, nether or end.") };
    private static int AtlasDimension(string value) => value == "overworld" ? 0 : value == "nether" ? 1 : 2;
    private static int SourceDimension(string value) => value == "overworld" ? 0 : value == "nether" ? -1 : 1;
}

public sealed record NocomBlockBounds(long MinX, long MinZ, long MaxXInclusive, long MaxZInclusive);
public sealed record NocomPeriod(string Key, string Dimension, int AtlasDimension, int SourceDimension,
    DateTimeOffset PeriodStartUtc, DateTimeOffset PeriodEndExclusiveUtc, long GroupedRows, long Observations,
    NocomBlockBounds ObservedExtentBlocks, string TileUrlTemplate, int MinNativeUrlZoom, int MaxNativeUrlZoom);
public sealed record NocomDimension(string Dimension, int AtlasDimension, int SourceDimension, int PeriodCount,
    long GroupedRows, long Observations, DateTimeOffset FirstBucketStartUtc, DateTimeOffset LastBucketEndExclusiveUtc,
    string TotalTileUrlTemplate, int MinNativeUrlZoom, int MaxNativeUrlZoom);
public sealed record NocomHighwayPeriod(string Dimension, int AtlasDimension, string Direction, int DirectionX, int DirectionZ,
    DateTimeOffset PeriodStartUtc, DateTimeOffset PeriodEndExclusiveUtc, long Observations);
public sealed record NocomDataset(string Id, string Name, string Url, string SourceUrl, string ManifestUrl, string PeriodsApiUrl,
    string HighwayActivityApiUrl, DateTimeOffset TilesGeneratedAtUtc, DateOnly ExploitPatchedDate, long GroupedRows,
    long Observations, int PeriodLayerCount, IReadOnlyList<NocomDimension> Dimensions, string GroupedSourceSha256,
    string GroupedServerScope, string HighwaySourceSha256, string HighwaySourceScope, string Attribution, IReadOnlyList<string> Caveats);

#pragma warning restore CS1591
