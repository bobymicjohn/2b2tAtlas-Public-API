using System.Text.Json;
using Atlas.Ingestion;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using _2b2tAtlas.Server.Models;

namespace _2b2tAtlas.Server.Services;

/// <summary>
/// Combines the BlueMap batch checkpoint, renderer activity, quality-gated catalog,
/// source-backed database inventory, and output-volume capacity for administrators.
/// </summary>
public sealed class BlueMapGenerationStatusService
{
    private readonly AtlasContext _context;
    private readonly BlueMapCatalogService _catalog;
    private readonly BlueMapOptions _options;
    private readonly ILogger<BlueMapGenerationStatusService> _logger;

    /// <summary>Initializes the read-only BlueMap status projection.</summary>
    public BlueMapGenerationStatusService(
        AtlasContext context,
        BlueMapCatalogService catalog,
        IOptions<BlueMapOptions> options,
        ILogger<BlueMapGenerationStatusService> logger)
    {
        _context = context;
        _catalog = catalog;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Returns a sanitized status snapshot without exposing host paths or source hashes.</summary>
    public async Task<BlueMapGenerationStatusDto> GetAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var catalog = _catalog.GetSummary();
        using var checkpoint = ReadJson(_options.StatusPath);
        var eligible = await EligibleRenderCount(cancellationToken);
        var remaining = Math.Max(0, eligible - catalog.ValidatedRenderCount);
        var outputFreeBytes = OutputDriveFreeBytes(_options.OutputRoot);

        if (checkpoint is null)
        {
            var catalogAvailable = Directory.Exists(_options.OutputRoot);
            return new BlueMapGenerationStatusDto
            {
                Available = catalogAvailable,
                Status = catalogAvailable ? "Idle" : "Unavailable",
                Phase = catalogAvailable ? "Validated catalog available; renderer checkpoint unavailable" : "BlueMap checkpoint and output catalog are unavailable",
                RefreshedUtc = now,
                ValidatedRenderCount = catalog.ValidatedRenderCount,
                EligibleRenderCount = eligible,
                RemainingRenderCount = remaining,
                OverworldValidated = catalog.OverworldValidated,
                NetherValidated = catalog.NetherValidated,
                EndValidated = catalog.EndValidated,
                DiagnosticGenerationCount = catalog.DiagnosticGenerationCount,
                OutputBytes = catalog.OutputBytes,
                OutputDriveFreeBytes = outputFreeBytes,
                MinimumProfileVersion = _options.MinimumProfileVersion
            };
        }

        var root = checkpoint.RootElement;
        var state = String(root, "State").ToLowerInvariant();
        var startedUtc = NullableDate(root, "StartedUtc");
        var updatedUtc = NullableDate(root, "UpdatedUtc");
        var latestActivityUtc = LatestActivity(updatedUtc, _options.StatusPath, _options.ActivityLogPaths);
        var stale = state == "running" && (latestActivityUtc is null ||
            now - latestActivityUtc.Value > TimeSpan.FromMinutes(Math.Clamp(_options.StatusStaleAfterMinutes, 5, 240)));
        var active = state == "running" && !stale;
        var batchCompleted = Int(root, "Completed");
        var batchTotal = Int(root, "Total");
        var failed = Elements(root, "Results").Count(result =>
            String(result, "State").Equals("failed", StringComparison.OrdinalIgnoreCase));
        var current = Current(root);
        var coordinated = root.TryGetProperty("Coordinated", out var coordinatedValue) && coordinatedValue.ValueKind == JsonValueKind.True;
        var workers = Elements(root, "Workers").Take(3).Select(worker => new BlueMapGenerationWorkerDto
        {
            WorkerId = Math.Clamp(Int(worker, "WorkerId"), 1, 3),
            State = stale ? "stale" : Bounded(String(worker, "State"), 24) ?? "idle",
            Stage = Bounded(String(worker, "Stage"), 40) ?? "waiting",
            Current = Current(worker),
            StartedUtc = NullableDate(worker, "StartedUtc"),
            UpdatedUtc = NullableDate(worker, "UpdatedUtc")
        }).ToList();
        var status = active ? "Active"
            : stale ? "Stale"
            : state == "completed-with-errors" ? "Completed with errors"
            : remaining == 0 ? "Complete"
            : "Idle";
        var phase = active && current is not null
            ? $"Rendering {FriendlyDimension(current.Dimension)} derivative | {batchCompleted:N0} / {batchTotal:N0} checked"
            : active ? $"BlueMap batch running | {batchCompleted:N0} / {batchTotal:N0} checked"
            : stale ? "BlueMap renderer activity has stopped updating"
            : status == "Complete" ? "Every eligible source-backed render has a validated 3D derivative"
            : state == "completed-with-errors" ? "Batch finished with failures; watchdog will retry missing derivatives"
            : "BlueMap renderer is between resumable passes";
        if (coordinated && active)
            phase = $"Coordinated BlueMap generation | {workers.Count(worker => worker.State == "running")} / {workers.Count} workers active | new renders discovered every minute";
        if (coordinated && state == "draining")
        {
            status = "Draining";
            phase = "Finishing assigned renders; new work is paused";
        }

        return new BlueMapGenerationStatusDto
        {
            Available = true,
            Active = active,
            Stale = stale,
            Status = status,
            Phase = phase,
            StartedUtc = startedUtc,
            UpdatedUtc = updatedUtc,
            LatestActivityUtc = latestActivityUtc,
            RefreshedUtc = now,
            BatchCompleted = batchCompleted,
            BatchTotal = batchTotal,
            BatchPercent = batchTotal == 0 ? 0 : Math.Round(batchCompleted * 100d / batchTotal, 1),
            ValidatedRenderCount = catalog.ValidatedRenderCount,
            EligibleRenderCount = eligible,
            RemainingRenderCount = remaining,
            PendingAfterSnapshot = active ? Math.Max(0, eligible - batchTotal) : 0,
            OverworldValidated = catalog.OverworldValidated,
            NetherValidated = catalog.NetherValidated,
            EndValidated = catalog.EndValidated,
            DiagnosticGenerationCount = catalog.DiagnosticGenerationCount,
            FailedRenderCount = failed,
            OutputBytes = catalog.OutputBytes,
            OutputQuotaBytes = Long(root, "OutputQuotaBytes"),
            OutputDriveFreeBytes = outputFreeBytes,
            MinimumProfileVersion = _options.MinimumProfileVersion,
            Current = current,
            Coordinated = coordinated,
            Workers = workers,
            Message = Bounded(String(root, "Message"), 300)
        };
    }

    private async Task<int> EligibleRenderCount(CancellationToken cancellationToken)
    {
        try
        {
            return await (
                from job in _context.IngestionJobs.AsNoTracking()
                join render in _context.Renders.AsNoTracking() on job.RenderId equals (int?)render.Id
                join location in _context.Locations.AsNoTracking() on render.LocationRowid equals location.Rowid
                where job.Status == "completed" && job.ArchiveSha256 != null
                select render.Id).Distinct().CountAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            _logger.LogWarning(exception, "Could not count BlueMap-eligible Atlas renders");
            return 0;
        }
    }

    private JsonDocument? ReadJson(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
                return JsonDocument.Parse(stream);
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                if (attempt == 3)
                    _logger.LogWarning(exception, "BlueMap checkpoint could not be read");
                else
                    Thread.Sleep(20 * (attempt + 1));
            }
        }
        return null;
    }

    private static BlueMapGenerationCurrentDto? Current(JsonElement root)
    {
        if (!root.TryGetProperty("Current", out var current) || current.ValueKind != JsonValueKind.Object) return null;
        return new BlueMapGenerationCurrentDto
        {
            RenderId = Int(current, "RenderId"),
            LocationId = Int(current, "LocationId"),
            LocationName = Bounded(String(current, "LocationName"), 160) ?? string.Empty,
            Dimension = Bounded(String(current, "Dimension"), 24)?.ToLowerInvariant() ?? string.Empty
        };
    }

    private static DateTimeOffset? LatestActivity(DateTimeOffset? updatedUtc, string statusPath, IEnumerable<string> configuredLogPaths)
    {
        var latest = updatedUtc;
        var logPaths = configuredLogPaths.Where(path => !string.IsNullOrWhiteSpace(path)).ToList();
        try
        {
            var statusRoot = Path.GetDirectoryName(Path.GetFullPath(statusPath));
            if (!string.IsNullOrWhiteSpace(statusRoot) && Directory.Exists(statusRoot))
                logPaths.AddRange(Directory.EnumerateFiles(statusRoot, "full-batch*.log", SearchOption.TopDirectoryOnly));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { }

        foreach (var path in logPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(path)) continue;
                var write = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
                if (latest is null || write > latest) latest = write;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        return latest;
    }

    private static long OutputDriveFreeBytes(string outputRoot)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(outputRoot));
            return string.IsNullOrWhiteSpace(root) ? 0 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { return 0; }
    }

    private static IEnumerable<JsonElement> Elements(JsonElement root, string property) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().ToArray()
            : [];
    private static string String(JsonElement root, string property) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    private static int Int(JsonElement root, string property) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var value) && value.TryGetInt32(out var parsed) ? parsed : 0;
    private static long Long(JsonElement root, string property) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var value) && value.TryGetInt64(out var parsed) ? parsed : 0;
    private static DateTimeOffset? NullableDate(JsonElement root, string property) =>
        DateTimeOffset.TryParse(String(root, property), out var parsed) ? parsed.ToUniversalTime() : null;
    private static string FriendlyDimension(string dimension) => dimension switch
    {
        "nether" => "Nether",
        "end" => "End",
        _ => "Overworld"
    };
    private static string? Bounded(string value, int maximumLength) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, maximumLength)];
}
