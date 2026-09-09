namespace Atlas.Ingestion;

/// <summary>Sanitized operational status for the example host Archive collector.</summary>
public sealed class ArchiveCollectorStatusDto
{
    /// <summary>Whether the collector checkpoint files were readable.</summary>
    public bool Available { get; set; }
    /// <summary>Whether at least one current worker is live and the supervisor heartbeat is fresh.</summary>
    public bool Active { get; set; }
    /// <summary>Whether the supervisor heartbeat exceeded the configured freshness limit.</summary>
    public bool Stale { get; set; }
    /// <summary>Compact display state such as Active, Idle, Complete, Stale, or Unavailable.</summary>
    public string Status { get; set; } = "Unavailable";
    /// <summary>Human-readable current collector phase.</summary>
    public string Phase { get; set; } = "Collector state unavailable";
    /// <summary>Last collector supervisor heartbeat in UTC.</summary>
    public DateTimeOffset? UpdatedUtc { get; set; }
    /// <summary>Time at which the API assembled this snapshot.</summary>
    public DateTimeOffset RefreshedUtc { get; set; }
    /// <summary>Number of verified live collector workers.</summary>
    public int ActiveWorkers { get; set; }
    /// <summary>Configured workers that use the bounded 30-minute discovery lane.</summary>
    public int FastLaneWorkers { get; set; }
    /// <summary>Configured workers that finish captures deferred by discovery lanes.</summary>
    public int LongRunningWorkers { get; set; }
    /// <summary>Number of durable fast-lane deferrals routed to long-running workers.</summary>
    public int DeferredToLongRunning { get; set; }
    /// <summary>Total number of applicable Archive warps in the current queue.</summary>
    public int QueueTotal { get; set; }
    /// <summary>Number of queue entries resolved as saved or unavailable.</summary>
    public int Checked { get; set; }
    /// <summary>Number of queue entries with final-standard captures.</summary>
    public int Saved { get; set; }
    /// <summary>Number of queue entries confirmed unavailable by The Archive.</summary>
    public int Unavailable { get; set; }
    /// <summary>Number of unresolved queue entries.</summary>
    public int Remaining { get; set; }
    /// <summary>Total records currently known to the collector catalog.</summary>
    public int CatalogDiscovered { get; set; }
    /// <summary>Number of checkpoint entries eligible for a later retry.</summary>
    public int Retryable { get; set; }
    /// <summary>Percentage of the current applicable queue resolved.</summary>
    public double ProgressPercent { get; set; }
    /// <summary>Current rolling production-handoff stage.</summary>
    public string HandoffStage { get; set; } = "Not started";
    /// <summary>Number of captures submitted during the current handoff pass.</summary>
    public int HandoffSubmitted { get; set; }
    /// <summary>Next configured weekly catalog check in example host local time.</summary>
    public DateTimeOffset? NextScheduledRun { get; set; }
    /// <summary>Human-readable recurring schedule.</summary>
    public string ScheduleLabel { get; set; } = "Weekly schedule unavailable";
    /// <summary>Sanitized per-worker progress ordered by worker identifier.</summary>
    public IReadOnlyList<ArchiveCollectorWorkerStatusDto> Workers { get; set; } = [];
}

/// <summary>Sanitized status for one isolated Minecraft collector account.</summary>
public sealed class ArchiveCollectorWorkerStatusDto
{
    /// <summary>Stable one-based worker number.</summary>
    public int Id { get; set; }
    /// <summary>Collector profile label; this is not an authentication credential.</summary>
    public string Profile { get; set; } = string.Empty;
    /// <summary>Fully qualified Minecraft/Fabric launch version used by the current attempt.</summary>
    public string? MinecraftVersion { get; set; }
    /// <summary>Collector scheduling lane: throughput or large-wdl.</summary>
    public string Lane { get; set; } = "large-wdl";
    /// <summary>Maximum adaptive capture runtime for this lane.</summary>
    public int AdaptiveMaximumRuntimeSeconds { get; set; }
    /// <summary>Number of deferred captures routed into this worker.</summary>
    public int DeferredIn { get; set; }
    /// <summary>Number of captures this fast worker deferred to a long lane.</summary>
    public int DeferredOut { get; set; }
    /// <summary>Compact display state for the worker.</summary>
    public string Status { get; set; } = "Offline";
    /// <summary>Stable, sanitized machine-readable outcome for the current launch.</summary>
    public string Outcome { get; set; } = "idle";
    /// <summary>Whether the recorded process is currently alive.</summary>
    public bool Running { get; set; }
    /// <summary>Whether the supervisor reports a bounded restart in progress.</summary>
    public bool Restarting { get; set; }
    /// <summary>Current or most recently assigned Archive warp.</summary>
    public string? Warp { get; set; }
    /// <summary>Human-readable warp label.</summary>
    public string? DisplayName { get; set; }
    /// <summary>Chunks received during the current coverage scan.</summary>
    public int? ChunksReceived { get; set; }
    /// <summary>Requested chunks not yet received during the current scan.</summary>
    public int? MissingChunks { get; set; }
    /// <summary>Current adaptive scan waypoint.</summary>
    public int? Waypoint { get; set; }
    /// <summary>Total adaptive scan waypoints.</summary>
    public int? WaypointTotal { get; set; }
    /// <summary>Total queue entries assigned to the worker shard.</summary>
    public int Assigned { get; set; }
    /// <summary>Queue entries completed by the worker shard.</summary>
    public int Completed { get; set; }
    /// <summary>Bounded supervisor restart count.</summary>
    public int RestartCount { get; set; }
    /// <summary>Last write time of the worker game log.</summary>
    public DateTimeOffset? LogUpdatedUtc { get; set; }
}
