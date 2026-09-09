using System.ComponentModel;
using System.Globalization;
using ModelContextProtocol.Server;

namespace _2b2tAtlas.Server.Services.Mcp;

#pragma warning disable CS1591
[McpServerToolType]
public sealed class NocomMcpTools(NocomDataService data)
{
    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Get Nocom World Pulse historical dataset provenance, per-dimension counts, coverage, tile links and caveats. Observations are not players, visits or ownership; aggregate coverage begins March 2020, not 2018.")]
    public NocomDataset get_nocom_dataset() => data.Dataset;

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Get up to 39 Nocom observation aggregates and tile templates by dimension and overlapping fixed 30-day UTC buckets. Date filters select whole buckets; results are not exact-day counts or player counts.")]
    public IReadOnlyList<NocomPeriod> get_nocom_periods(
        [Description("overworld, nether or end; omit for all dimensions.")] string? dimension = null,
        [Description("Optional inclusive date YYYY-MM-DD; selects overlapping buckets.")] string? from = null,
        [Description("Optional inclusive date YYYY-MM-DD; selects overlapping buckets.")] string? to = null) => data.Periods(dimension, Parse(from), Parse(to));

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Get released historical Nocom highway observation counts by compass direction and fixed 30-day bucket. At most 136 rows per dimension or 17 per direction. Counts reflect scanner bias, not unique players/trips. End has no released highway series.")]
    public IReadOnlyList<NocomHighwayPeriod> get_nocom_highway_activity(
        [Description("overworld or nether; defaults to nether.")] string dimension = "nether",
        [Description("Optional compass name: north, northeast, east, southeast, south, southwest, west, northwest.")] string? direction = null) => data.Highways(dimension, direction);

    private static DateOnly? Parse(string? value) => value is null ? null :
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var result)
            ? result : throw new ArgumentException("Dates must use YYYY-MM-DD.");
}
#pragma warning restore CS1591
