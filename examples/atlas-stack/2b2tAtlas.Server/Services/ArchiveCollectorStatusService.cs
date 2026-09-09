using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Atlas.Ingestion;
using Microsoft.Extensions.Options;

namespace _2b2tAtlas.Server.Services;

/// <summary>
/// Reads collector checkpoints and live coverage logs on example host and exposes only a bounded,
/// sanitized snapshot to authenticated administrators.
/// </summary>
public sealed partial class ArchiveCollectorStatusService
{
    private readonly ArchiveCollectorStatusOptions _options;
    private readonly ILogger<ArchiveCollectorStatusService> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private ArchiveCollectorStatusDto? _cached;
    private DateTimeOffset _cacheExpiresUtc;

    /// <summary>Initializes a collector checkpoint reader with bounded caching.</summary>
    public ArchiveCollectorStatusService(
        IOptions<ArchiveCollectorStatusOptions> options,
        ILogger<ArchiveCollectorStatusService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Returns the current sanitized collector snapshot.</summary>
    public async Task<ArchiveCollectorStatusDto> GetAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (_cached is not null && now < _cacheExpiresUtc) return _cached;

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (_cached is not null && now < _cacheExpiresUtc) return _cached;
            _cached = BuildSnapshot(now);
            _cacheExpiresUtc = now.AddSeconds(Math.Clamp(_options.CacheSeconds, 2, 60));
            return _cached;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private ArchiveCollectorStatusDto BuildSnapshot(DateTimeOffset now)
    {
        var runRoot = Path.GetFullPath(_options.RunRoot);
        using var state = ReadJson(Path.Combine(runRoot, "collector-state.json"));
        using var queue = ReadJson(Path.Combine(runRoot, "capture-queue.json"));
        using var parallel = ReadJson(Path.Combine(runRoot, "parallel-collector-status.json"));
        using var finalizer = ReadJson(Path.Combine(runRoot, "final-standard-handoff-status.json"));
        using var rolling = ReadJson(Path.Combine(runRoot, "rolling-handoff", "status.json"));

        if (state is null || queue is null)
        {
            return new ArchiveCollectorStatusDto
            {
                Available = false,
                Status = "Unavailable",
                Phase = "Collector checkpoint files are unavailable",
                RefreshedUtc = now,
                NextScheduledRun = NextWeeklyRun(now),
                ScheduleLabel = "Sundays at 6:00 AM (example host local time)"
            };
        }

        var stateEntries = Elements(state.RootElement, "entries").ToList();
        var queueEntries = Elements(queue.RootElement, "entries").ToList();
        var byWarp = stateEntries
            .Select(entry => (Key: String(entry, "normalizedWarp").ToLowerInvariant(), Entry: entry))
            .Where(item => item.Key.Length > 0)
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Entry, StringComparer.OrdinalIgnoreCase);

        var saved = 0;
        var unavailable = 0;
        foreach (var candidate in queueEntries)
        {
            var key = String(candidate, "normalizedWarp");
            if (!byWarp.TryGetValue(key, out var prior)) continue;
            var status = String(prior, "status");
            if (status.Equals("missing", StringComparison.OrdinalIgnoreCase))
            {
                unavailable++;
            }
            else if (IsFinalCapture(prior))
            {
                saved++;
            }
        }

        var workers = parallel is null
            ? []
            : Elements(parallel.RootElement, "workers")
                .OrderBy(worker => Int(worker, "id"))
                .Take(16)
                .Select(worker => BuildWorker(worker, runRoot))
                .ToList();
        var fastLaneWorkers = workers.Count(worker => worker.Lane is "throughput" or "temporary-long");
        var longRunningWorkers = workers.Count - fastLaneWorkers;
        var deferredToLongRunning = parallel is null ? 0 : Int(parallel.RootElement, "deferredToLongRunning");
        var updatedUtc = parallel is null ? NullableDate(state.RootElement, "updatedUtc") : NullableDate(parallel.RootElement, "updatedUtc");
        var stale = updatedUtc is null || now - updatedUtc.Value > TimeSpan.FromSeconds(Math.Clamp(_options.StaleAfterSeconds, 30, 600));
        var activeWorkers = workers.Count(worker => worker.Outcome == "live");
        var recovering = !stale && activeWorkers == 0 && workers.Any(worker =>
            worker.Outcome is "archive-backend-rejected" or "server-disconnected" or "connecting" or "backoff");
        var checkedCount = saved + unavailable;
        var remaining = Math.Max(0, queueEntries.Count - checkedCount);
        var retryable = stateEntries.Count(entry => String(entry, "status").Equals("retryable", StringComparison.OrdinalIgnoreCase));
        var finalizerStage = finalizer is null ? string.Empty : String(finalizer.RootElement, "stage");
        var rollingStage = rolling is null ? string.Empty : String(rolling.RootElement, "stage");
        var handoffStage = rollingStage is not ("" or "idle") ? rollingStage : finalizerStage;
        if (handoffStage.Length == 0) handoffStage = "not-started";
        var submitted = rolling is null ? 0 : Int(rolling.RootElement, "submitted");
        var active = !stale && activeWorkers > 0;

        return new ArchiveCollectorStatusDto
        {
            Available = true,
            Active = active,
            Stale = stale,
            Status = active ? "Active" : stale ? "Stale" : recovering ? "Recovering" : remaining == 0 ? "Complete" : "Idle",
            Phase = active
                ? $"Parallel adaptive capture | {activeWorkers} active | {fastLaneWorkers} fast / {longRunningWorkers} long"
                : stale ? "Collector status has stopped updating"
                : recovering && workers.Any(worker => worker.Outcome == "archive-backend-rejected")
                    ? "Archive backend unavailable | bounded retries active"
                : recovering ? "Collector connection recovery in progress"
                : remaining == 0 ? "Initial collection pass complete" : "Collector is idle",
            UpdatedUtc = updatedUtc,
            RefreshedUtc = now,
            ActiveWorkers = activeWorkers,
            FastLaneWorkers = fastLaneWorkers,
            LongRunningWorkers = longRunningWorkers,
            DeferredToLongRunning = deferredToLongRunning,
            QueueTotal = queueEntries.Count,
            Checked = checkedCount,
            Saved = saved,
            Unavailable = unavailable,
            Remaining = remaining,
            CatalogDiscovered = stateEntries.Count,
            Retryable = retryable,
            ProgressPercent = queueEntries.Count == 0 ? 0 : Math.Round(checkedCount * 100d / queueEntries.Count, 1),
            HandoffStage = Friendly(handoffStage),
            HandoffSubmitted = submitted,
            NextScheduledRun = NextWeeklyRun(now),
            ScheduleLabel = "Sundays at 6:00 AM (example host local time)",
            Workers = workers
        };
    }

    private ArchiveCollectorWorkerStatusDto BuildWorker(JsonElement worker, string runRoot)
    {
        var id = Int(worker, "id");
        var profile = String(worker, "profile");
        var reportedRunning = Bool(worker, "running");
        var restarting = Bool(worker, "restarting");
        var processId = Int(worker, "processId");
        var running = reportedRunning && IsProcessAlive(processId);
        var stdout = String(worker, "stdout");
        var safeStdout = SafeContainedPath(runRoot, stdout);
        var stderr = String(worker, "stderr");
        var safeStderr = SafeContainedPath(runRoot, stderr);
        var minecraftVersion = NullIfEmpty(String(worker, "minecraftVersion"));
        var lane = String(worker, "lane");
        if (lane.Length == 0) lane = "large-wdl";
        var logRoot = id == 5 && minecraftVersion?.Contains("1.21.10", StringComparison.Ordinal) == true
            ? _options.CompatibilityProfileRoot
            : id == 1 || profile.Equals("atlas-owner", StringComparison.OrdinalIgnoreCase)
                ? _options.PrimaryProfileRoot
                : SafeProfilePath(_options.ProfilesRoot, profile);
        var gameLog = logRoot is null ? null : Path.Combine(logRoot, "game", "logs", "latest.log");
        var collectorLog = logRoot is null ? null : NewestCollectorLog(Path.Combine(logRoot, "logs"));
        var launchStartedUtc = LatestCreationTimeUtc(safeStdout, safeStderr);
        var currentCollectorLog = IsCurrentLaunchLog(collectorLog, launchStartedUtc) ? collectorLog : null;
        var currentGameLog = IsCurrentLaunchLog(gameLog, launchStartedUtc) ? gameLog : null;
        // The per-launch collector log is authoritative. The game log can grow past
        // the bounded telemetry tail during a large capture, while recovered wrapper
        // stdout can refer to a process that exited hours earlier.
        var warp = LastTeleportedWarp(currentCollectorLog, readEntireFile: true)
            ?? LastTeleportedWarp(currentGameLog, readEntireFile: true)
            ?? LastWarp(safeStdout)
            ?? NullIfEmpty(String(worker, "currentOrLastWarp"));
        var progress = LastCoverageProgress(currentCollectorLog);
        if (progress.Received is null) progress = LastCoverageProgress(currentGameLog);
        var assigned = Int(worker, "assigned");
        var completed = Int(worker, "completed");
        // A live wrapper can still be waiting for the Archive backend. Calling that
        // state "Active" while every coverage value is null makes a reconnect loop
        // look like an in-progress capture. Coverage telemetry is the authoritative
        // boundary between a connected capture and a merely running launcher.
        var launchOutcome = CurrentLaunchOutcome(currentCollectorLog, safeStdout, safeStderr);
        var outcome = !running && Bool(worker, "refillPending") ? "awaiting-refill"
            : running && progress.Received is not null ? "live"
            : launchOutcome ?? (running ? "connecting"
            : restarting ? "backoff"
            : completed >= assigned && assigned > 0 ? "complete"
            : "idle");
        var status = outcome switch
        {
            "live" => "Active",
            "archive-backend-rejected" => "Archive unavailable",
            "server-disconnected" => "Disconnected",
            "connecting" => "Connecting",
            "backoff" => "Restarting",
            "complete" => "Done",
            "awaiting-refill" => "Awaiting refill",
            _ => "Offline"
        };

        return new ArchiveCollectorWorkerStatusDto
        {
            Id = id,
            Profile = profile,
            MinecraftVersion = minecraftVersion,
            Lane = lane,
            AdaptiveMaximumRuntimeSeconds = Int(worker, "adaptiveMaximumRuntimeSeconds"),
            DeferredIn = Int(worker, "deferredIn"),
            DeferredOut = Int(worker, "deferredOut"),
            Status = status,
            Outcome = outcome,
            Running = running,
            Restarting = restarting,
            Warp = warp,
            DisplayName = warp?.Replace('_', ' '),
            ChunksReceived = progress.Received,
            MissingChunks = progress.Missing,
            Waypoint = progress.Waypoint,
            WaypointTotal = progress.WaypointTotal,
            Assigned = assigned,
            Completed = completed,
            RestartCount = Int(worker, "restartCount"),
            LogUpdatedUtc = currentCollectorLog is not null && File.Exists(currentCollectorLog)
                ? File.GetLastWriteTimeUtc(currentCollectorLog)
                : currentGameLog is not null && File.Exists(currentGameLog) ? File.GetLastWriteTimeUtc(currentGameLog) : null
        };
    }

    private static string? CurrentLaunchOutcome(params string?[] paths)
    {
        foreach (var path in paths)
        {
            foreach (var line in ReadTailLines(path, 256 * 1024).Reverse())
            {
                if (line.Contains("Unable to connect to archive", StringComparison.OrdinalIgnoreCase))
                    return "archive-backend-rejected";
                if (line.Contains("Client disconnected with reason:", StringComparison.OrdinalIgnoreCase) &&
                    line.Contains("Disconnected", StringComparison.OrdinalIgnoreCase))
                    return "server-disconnected";
            }
        }
        return null;
    }

    private static DateTime? LatestCreationTimeUtc(params string?[] paths)
    {
        DateTime? latest = null;
        foreach (var path in paths)
        {
            if (path is null || !File.Exists(path)) continue;
            var created = File.GetCreationTimeUtc(path);
            if (latest is null || created > latest) latest = created;
        }
        return latest;
    }

    private static bool IsCurrentLaunchLog(string? path, DateTime? launchStartedUtc)
    {
        if (path is null || !File.Exists(path)) return false;
        return launchStartedUtc is null || File.GetCreationTimeUtc(path) >= launchStartedUtc.Value.AddSeconds(-15);
    }

    private JsonDocument? ReadJson(string path)
    {
        if (!File.Exists(path)) return null;
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
                    _logger.LogWarning(exception, "Collector status file could not be read: {FileName}", Path.GetFileName(path));
                else
                    Thread.Sleep(20 * (attempt + 1));
            }
        }
        return null;
    }

    private static IEnumerable<JsonElement> Elements(JsonElement root, string property)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray().ToArray();
        return [];
    }

    private static bool IsFinalCapture(JsonElement entry)
    {
        var status = String(entry, "status");
        if (status is not ("captured" or "ready") || !entry.TryGetProperty("adaptive", out var adaptive) || adaptive.ValueKind != JsonValueKind.Object)
            return false;
        return Int(adaptive, "standardVersion") >= 2 && adaptive.TryGetProperty("componentSelection", out var selection) && selection.ValueKind != JsonValueKind.Null;
    }

    private static string? LastWarp(string? path)
    {
        foreach (var line in ReadTailLines(path, 256 * 1024).Reverse())
        {
            var match = WarpLineRegex().Match(line);
            if (match.Success) return match.Groups[1].Value.Trim();
        }
        return null;
    }

    private static string? LastTeleportedWarp(string? path, bool readEntireFile = false)
    {
        var lines = readEntireFile ? ReadAllLinesShared(path) : ReadTailLines(path, 1024 * 1024);
        foreach (var line in lines.Reverse())
        {
            var plainLine = AnsiEscapeRegex().Replace(line, string.Empty);
            var match = TeleportedWarpRegex().Match(plainLine);
            if (match.Success) return match.Groups[1].Value.Trim();
        }
        return null;
    }

    private static (int? Received, int? Missing, int? Waypoint, int? WaypointTotal) LastCoverageProgress(string? path)
    {
        foreach (var line in ReadTailLines(path, 1024 * 1024).Reverse())
        {
            if (!line.Contains("ATLAS_COVER", StringComparison.Ordinal) || !line.Contains("received=", StringComparison.Ordinal)) continue;
            return (
                MatchInt(ReceivedRegex(), line),
                MatchInt(MissingRegex(), line),
                MatchInt(WaypointRegex(), line, 1),
                MatchInt(WaypointRegex(), line, 2));
        }
        return (null, null, null, null);
    }

    private static IReadOnlyList<string> ReadTailLines(string? path, int maximumBytes)
    {
        if (path is null || !File.Exists(path)) return [];
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
            var length = stream.Length;
            var bytes = (int)Math.Min(length, maximumBytes);
            stream.Seek(-bytes, SeekOrigin.End);
            var buffer = new byte[bytes];
            var offset = 0;
            while (offset < bytes)
            {
                var read = stream.Read(buffer, offset, bytes - offset);
                if (read == 0) break;
                offset += read;
            }
            var text = Encoding.UTF8.GetString(buffer, 0, offset);
            var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            return length > bytes && lines.Length > 1 ? lines[1..] : lines;
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static IReadOnlyList<string> ReadAllLinesShared(string? path)
    {
        if (path is null || !File.Exists(path)) return [];
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line) lines.Add(line);
            return lines;
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static string? NewestCollectorLog(string directory)
    {
        if (!Directory.Exists(directory)) return null;
        try
        {
            return Directory.EnumerateFiles(directory, "collector-*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        if (processId <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static string? SafeContainedPath(string root, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        try
        {
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullCandidate = Path.GetFullPath(candidate);
            return fullCandidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) ? fullCandidate : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? SafeProfilePath(string root, string profile)
    {
        if (!SafeProfileRegex().IsMatch(profile)) return null;
        return SafeContainedPath(root, Path.Combine(root, profile));
    }

    private DateTimeOffset NextWeeklyRun(DateTimeOffset now)
    {
        var localNow = now.ToLocalTime();
        var days = ((int)_options.WeeklyRunDay - (int)localNow.DayOfWeek + 7) % 7;
        var candidate = new DateTimeOffset(localNow.Date.AddDays(days).AddHours(Math.Clamp(_options.WeeklyRunHour, 0, 23)), localNow.Offset);
        if (candidate <= localNow) candidate = candidate.AddDays(7);
        return candidate;
    }

    private static string Friendly(string value) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.Replace('-', ' '));
    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
    private static string String(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty : string.Empty;
    private static int Int(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : 0;
    private static bool Bool(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
    private static DateTimeOffset? NullableDate(JsonElement element, string property) =>
        DateTimeOffset.TryParse(String(element, property), out var result) ? result : null;
    private static int? MatchInt(Regex regex, string input, int group = 1) =>
        regex.Match(input) is { Success: true } match && int.TryParse(match.Groups[group].Value, out var value) ? value : null;

    [GeneratedRegex(@"^WARP\s+(.+?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex WarpLineRegex();
    [GeneratedRegex(@"\[Warps\]\s+You were teleported to '(.+?)'\.", RegexOptions.CultureInvariant)]
    private static partial Regex TeleportedWarpRegex();
    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.CultureInvariant)]
    private static partial Regex AnsiEscapeRegex();
    [GeneratedRegex(@"\breceived=(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex ReceivedRegex();
    [GeneratedRegex(@"\bmissing=(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex MissingRegex();
    [GeneratedRegex(@"\bwaypoint=(\d+)/(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex WaypointRegex();
    [GeneratedRegex(@"^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeProfileRegex();
}
