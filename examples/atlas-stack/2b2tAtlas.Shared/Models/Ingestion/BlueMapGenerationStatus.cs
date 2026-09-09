namespace Atlas.Ingestion;

/// <summary>Sanitized operational status for Atlas BlueMap 3D derivative generation.</summary>
public sealed class BlueMapGenerationStatusDto
{
    /// <summary>Whether at least a renderer checkpoint or output catalog was readable.</summary>
    public bool Available { get; set; }
    /// <summary>Whether a fresh BlueMap batch is currently running.</summary>
    public bool Active { get; set; }
    /// <summary>Whether a running checkpoint has stopped producing observable activity.</summary>
    public bool Stale { get; set; }
    /// <summary>Compact display state such as Active, Idle, Complete, Stale, or Unavailable.</summary>
    public string Status { get; set; } = "Unavailable";
    /// <summary>Human-readable current generation phase.</summary>
    public string Phase { get; set; } = "BlueMap generation state unavailable";
    /// <summary>Time the current batch began.</summary>
    public DateTimeOffset? StartedUtc { get; set; }
    /// <summary>Last durable renderer checkpoint update.</summary>
    public DateTimeOffset? UpdatedUtc { get; set; }
    /// <summary>Newest checkpoint or renderer-log activity.</summary>
    public DateTimeOffset? LatestActivityUtc { get; set; }
    /// <summary>Time at which the API assembled this snapshot.</summary>
    public DateTimeOffset RefreshedUtc { get; set; }
    /// <summary>Jobs that reached a terminal state in the current snapshot batch.</summary>
    public int BatchCompleted { get; set; }
    /// <summary>Jobs selected when the current snapshot batch began.</summary>
    public int BatchTotal { get; set; }
    /// <summary>Percentage of the current snapshot batch checked.</summary>
    public double BatchPercent { get; set; }
    /// <summary>Distinct renders with a currently advertised, quality-gated derivative.</summary>
    public int ValidatedRenderCount { get; set; }
    /// <summary>Distinct surviving source-backed renders eligible for BlueMap generation.</summary>
    public int EligibleRenderCount { get; set; }
    /// <summary>Eligible renders that do not yet have a validated derivative.</summary>
    public int RemainingRenderCount { get; set; }
    /// <summary>Eligible renders created after the running batch took its selection snapshot.</summary>
    public int PendingAfterSnapshot { get; set; }
    /// <summary>Validated Overworld derivatives.</summary>
    public int OverworldValidated { get; set; }
    /// <summary>Validated Nether derivatives.</summary>
    public int NetherValidated { get; set; }
    /// <summary>Validated End derivatives.</summary>
    public int EndValidated { get; set; }
    /// <summary>Completed generation manifests retained for diagnostics, including superseded profiles.</summary>
    public int DiagnosticGenerationCount { get; set; }
    /// <summary>Failed jobs recorded in the current batch.</summary>
    public int FailedRenderCount { get; set; }
    /// <summary>Manifest-reported bytes retained beneath the BlueMap output root.</summary>
    public long OutputBytes { get; set; }
    /// <summary>Configured maximum bytes available to generated derivatives.</summary>
    public long OutputQuotaBytes { get; set; }
    /// <summary>Free bytes on the output volume.</summary>
    public long OutputDriveFreeBytes { get; set; }
    /// <summary>Minimum renderer profile accepted by the public catalog.</summary>
    public int MinimumProfileVersion { get; set; }
    /// <summary>Current render, when a batch is actively processing one.</summary>
    public BlueMapGenerationCurrentDto? Current { get; set; }
    /// <summary>Whether a continuously discovering coordinator owns the workers.</summary>
    public bool Coordinated { get; set; }
    /// <summary>Sanitized per-worker state; empty for legacy checkpoints.</summary>
    public List<BlueMapGenerationWorkerDto> Workers { get; set; } = [];
    /// <summary>Bounded renderer status message.</summary>
    public string? Message { get; set; }
}

/// <summary>One isolated generation worker, without process IDs or filesystem paths.</summary>
public sealed class BlueMapGenerationWorkerDto
{
    /// <summary>Stable worker slot.</summary>
    public int WorkerId { get; set; }
    /// <summary>Running, idle, or stale.</summary>
    public string State { get; set; } = "idle";
    /// <summary>Current preparation/render/publication stage.</summary>
    public string Stage { get; set; } = "waiting";
    /// <summary>Current render identity.</summary>
    public BlueMapGenerationCurrentDto? Current { get; set; }
    /// <summary>When this job was assigned.</summary>
    public DateTimeOffset? StartedUtc { get; set; }
    /// <summary>Most recent worker checkpoint or log activity.</summary>
    public DateTimeOffset? UpdatedUtc { get; set; }
}

/// <summary>Sanitized identity of the BlueMap job currently being generated.</summary>
public sealed class BlueMapGenerationCurrentDto
{
    /// <summary>Atlas render identifier.</summary>
    public int RenderId { get; set; }
    /// <summary>Owning Atlas location identifier.</summary>
    public int LocationId { get; set; }
    /// <summary>Owning location display name.</summary>
    public string LocationName { get; set; } = string.Empty;
    /// <summary>Native Minecraft dimension.</summary>
    public string Dimension { get; set; } = string.Empty;
}
