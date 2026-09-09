namespace Atlas.Enrichment;

/// <summary>
/// The current operational state of the local AI wiki-enrichment engine, shown on the admin page so an
/// operator can see whether a run will do anything before starting one.
/// </summary>
public sealed class EnrichmentStatusDto
{
    /// <summary>Gets or sets the server-owned batch run, including the last completed run.</summary>
    public EnrichmentRunStatusDto Run { get; set; } = new();

    /// <summary>Gets or sets whether the engine is enabled in configuration.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets whether the engine is currently paused by the GPU game-mode lock.</summary>
    public bool Paused { get; set; }

    /// <summary>Gets or sets the model tag used for description drafting and match ranking.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Gets or sets the MediaWiki endpoint the engine reads from.</summary>
    public string WikiApiBase { get; set; } = string.Empty;

    /// <summary>Gets or sets the minimum confidence at which a match is auto-applied.</summary>
    public double AutoApplyMinConfidence { get; set; }

    /// <summary>Gets or sets the coordinate agreement tolerance, in Overworld blocks.</summary>
    public int CoordinateToleranceBlocks { get; set; }

    /// <summary>Gets or sets the number of locations still missing a wiki link or description.</summary>
    public int LocationsMissingMetadata { get; set; }

    /// <summary>Gets or sets the number of explicit wiki group/build candidates not yet linked in Atlas.</summary>
    public int LocationsMissingGroupAttributions { get; set; }

    /// <summary>Gets or sets unrepresented wiki groups that explicitly name at least one Atlas build.</summary>
    public int UnrepresentedGroupCandidates { get; set; }

    /// <summary>Gets or sets the number of AI enrichments currently awaiting review (Applied or Pending).</summary>
    public int PendingReviewCount { get; set; }

    /// <summary>Gets or sets whether a usable revision-pinned group evidence index is loaded.</summary>
    public bool GroupEvidenceAvailable { get; set; }

    /// <summary>Gets or sets when the loaded group evidence index was generated.</summary>
    public DateTime? GroupEvidenceGeneratedUtc { get; set; }

    /// <summary>Gets or sets the number of audited group articles in the evidence index.</summary>
    public int GroupEvidenceArticleCount { get; set; }

    /// <summary>Gets or sets a diagnostic when the group evidence index is missing, stale, or invalid.</summary>
    public string? GroupEvidenceMessage { get; set; }
}

/// <summary>
/// A server-owned enrichment run snapshot. Unlike browser-local button state, this survives page refreshes
/// and is shared by every administrator viewing the site.
/// </summary>
public sealed class EnrichmentRunStatusDto
{
    /// <summary>Gets or sets the unique id of this run, or null before the first run since API startup.</summary>
    public string? RunId { get; set; }

    /// <summary>Gets or sets idle, running, completed, failed, or cancelled.</summary>
    public string State { get; set; } = "idle";

    /// <summary>Gets or sets whether the server is actively processing the run.</summary>
    public bool IsRunning { get; set; }

    /// <summary>Gets or sets whether the run auto-applies sufficiently confident matches.</summary>
    public bool AutoApply { get; set; }

    /// <summary>Gets or sets the operator who started the run.</summary>
    public string? StartedBy { get; set; }

    /// <summary>Gets or sets the UTC start time.</summary>
    public DateTime? StartedUtc { get; set; }

    /// <summary>Gets or sets the UTC time of the latest progress update.</summary>
    public DateTime? UpdatedUtc { get; set; }

    /// <summary>Gets or sets the UTC completion, failure, or cancellation time.</summary>
    public DateTime? CompletedUtc { get; set; }

    /// <summary>Gets or sets the number of eligible locations in the run.</summary>
    public int Total { get; set; }

    /// <summary>Gets or sets the number of locations examined so far.</summary>
    public int Scanned { get; set; }

    /// <summary>Gets or sets the number of locations that produced a usable match.</summary>
    public int Matched { get; set; }

    /// <summary>Gets or sets the number of confident matches auto-applied.</summary>
    public int AutoApplied { get; set; }

    /// <summary>Gets or sets the number of suggestions queued for review.</summary>
    public int Queued { get; set; }

    /// <summary>Gets or sets the number of locations that produced no new suggestion.</summary>
    public int Skipped { get; set; }

    /// <summary>Gets or sets the number of individual locations that failed but did not stop the batch.</summary>
    public int Errors { get; set; }

    /// <summary>Gets or sets the location currently being examined.</summary>
    public int? CurrentLocationId { get; set; }

    /// <summary>Gets or sets the name of the location currently being examined.</summary>
    public string? CurrentLocationName { get; set; }

    /// <summary>Gets or sets a human-readable run result or diagnostic.</summary>
    public string? Message { get; set; }

    /// <summary>Gets or sets progress from zero through one hundred.</summary>
    public double ProgressPercent => Total <= 0
        ? (IsRunning ? 0 : RunId is null ? 0 : 100)
        : Math.Clamp(100d * Scanned / Total, 0, 100);

    /// <summary>Gets or sets elapsed wall time in whole seconds.</summary>
    public int ElapsedSeconds { get; set; }
}

/// <summary>Options for a complete eligible-location enrichment run submitted from the admin page.</summary>
public sealed class EnrichmentRunRequest
{
    /// <summary>Gets or sets whether confident, coordinate-confirmed matches are auto-applied (else queued).</summary>
    public bool AutoApply { get; set; } = true;
}

/// <summary>The outcome of a batch enrichment run, summarizing what the engine did.</summary>
public sealed class EnrichmentRunSummary
{
    /// <summary>Gets or sets whether the run was allowed to start (engine enabled and not paused).</summary>
    public bool Ran { get; set; }

    /// <summary>Gets or sets an explanation when the run did not start.</summary>
    public string? Message { get; set; }

    /// <summary>Gets or sets the number of locations examined.</summary>
    public int Scanned { get; set; }

    /// <summary>Gets or sets the number of locations that produced a usable wiki match.</summary>
    public int Matched { get; set; }

    /// <summary>Gets or sets the number of confident matches that were auto-applied.</summary>
    public int AutoApplied { get; set; }

    /// <summary>Gets or sets the number of weaker matches queued for manual review.</summary>
    public int Queued { get; set; }

    /// <summary>Gets or sets the number of locations skipped (no match or nothing new to add).</summary>
    public int Skipped { get; set; }
}
