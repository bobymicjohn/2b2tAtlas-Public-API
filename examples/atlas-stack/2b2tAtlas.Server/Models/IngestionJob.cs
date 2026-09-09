namespace _2b2tAtlas.Server.Models;

/// <summary>
/// Persists the server-side state of one world-download ingestion request as it moves
/// from the operator queue through a leased local worker and optional render registration.
/// </summary>
/// <remarks>
/// Worker credentials are never stored in plaintext: only a SHA-256 claim-token digest is
/// retained. Date values are stored as round-trip UTC text because the existing Atlas SQLite
/// schema is managed additively rather than through EF migrations.
/// </remarks>
public class IngestionJob
{
    /// <summary>Gets or sets the database-generated primary key used by audit records and internal joins.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the opaque identifier exposed to administrators and ingestion workers.</summary>
    public string PublicId { get; set; } = string.Empty;

    /// <summary>Gets or sets the original intake archive file name recorded for operator traceability.</summary>
    public string IntakeFileName { get; set; } = string.Empty;

    /// <summary>Gets or sets the original uploader-visible ZIP filename retained for provenance.</summary>
    public string? OriginalFileName { get; set; }

    /// <summary>Gets or sets the unique, URL-safe reservation key for the ingestion request.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Gets or sets the Atlas display name assigned to the resulting location render.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the declared world-download date preserved with the registered render.</summary>
    public string WorldDownloadDate { get; set; } = string.Empty;

    /// <summary>Gets or sets whether a valid inspected level.dat LastPlayed date may replace the fallback date.</summary>
    public int UseArchiveLastPlayed { get; set; } = 1;

    /// <summary>Gets or sets the provenance label identifying where the world download was obtained.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Gets or sets the Atlas render-scale label passed through to the location render record.</summary>
    public string Scale { get; set; } = string.Empty;

    /// <summary>Gets or sets the target render dimension (<c>overworld</c> or <c>end</c>) selected by the operator.</summary>
    public string Dimension { get; set; } = "overworld";

    /// <summary>Gets or sets an optional archive-relative world root selected for a multi-world ZIP.</summary>
    public string? WorldRoot { get; set; }

    /// <summary>Gets or sets whether a location match has been resolved (auto or manual), stored as 1 or 0.</summary>
    public int MatchResolved { get; set; }

    /// <summary>Gets or sets the serialized ranked location suggestions stored while the job awaits a manual match.</summary>
    public string? MatchSuggestionsJson { get; set; }

    /// <summary>Gets or sets whether the requested tile set includes separate day and night variants, stored as 1 or 0.</summary>
    public int DayNight { get; set; }

    /// <summary>
    /// Gets or sets the existing Atlas location to receive the render, or <see langword="null"/>
    /// when successful completion should create a location at the rendered bounds' midpoint.
    /// </summary>
    public int? ExistingLocationId { get; set; }

    /// <summary>Gets or sets the location-render row created atomically when the job completes.</summary>
    public int? RenderId { get; set; }

    /// <summary>Gets or sets the queue lifecycle state used for conditional claims and terminal-state enforcement.</summary>
    public string Status { get; set; } = "queued";

    /// <summary>Gets or sets the worker pipeline stage reported for operator diagnostics.</summary>
    public string? Stage { get; set; }

    /// <summary>Gets or sets the worker or server status message shown to Atlas operators.</summary>
    public string? Message { get; set; }

    /// <summary>Gets or sets the bounded completion percentage reported by the active worker.</summary>
    public int ProgressPercent { get; set; }

    /// <summary>Gets or sets the worker's estimated seconds remaining, cleared for terminal jobs.</summary>
    public int? EtaSeconds { get; set; }

    /// <summary>Gets or sets the worker-verified SHA-256 digest of the immutable intake archive.</summary>
    public string? ArchiveSha256 { get; set; }

    /// <summary>
    /// Gets or sets the SHA-256 digest of the current claim token used to authenticate status
    /// updates from the worker that holds the lease.
    /// </summary>
    public string? ClaimTokenSha256 { get; set; }

    /// <summary>Gets or sets the number of leases issued, including expired claims retried by another worker.</summary>
    public int AttemptCount { get; set; }

    /// <summary>Gets or sets whether this job must rebuild and replace its existing published render.</summary>
    public int RerenderRequested { get; set; }

    /// <summary>Gets or sets an optional inclusive top-Y cutoff applied by the trusted renderer.</summary>
    public int? RenderTopY { get; set; }

    /// <summary>Gets or sets the authenticated Atlas user who queued the job, when it was not created by local intake.</summary>
    public int? RequestedByUserId { get; set; }

    /// <summary>Gets or sets the requester's username snapshot retained for audit readability.</summary>
    public string? RequestedByUsername { get; set; }

    /// <summary>Gets or sets the round-trip UTC timestamp at which the server accepted the request.</summary>
    public string RequestedUtc { get; set; } = DateTime.UtcNow.ToString("o");

    /// <summary>Gets or sets the round-trip UTC timestamp of the most recent successful claim.</summary>
    public string? ClaimedUtc { get; set; }

    /// <summary>Gets or sets the round-trip UTC lease deadline after which another worker may reclaim the job.</summary>
    public string? LeaseExpiresUtc { get; set; }

    /// <summary>Gets or sets the round-trip UTC timestamp of the latest accepted state update.</summary>
    public string? UpdatedUtc { get; set; }

    /// <summary>Gets or sets the round-trip UTC timestamp at which the job entered a terminal state.</summary>
    public string? CompletedUtc { get; set; }

    /// <summary>Gets or sets the serialized bounded world-inspection report supplied by the trusted ingestion worker.</summary>
    public string? InspectionJson { get; set; }

    /// <summary>Gets or sets bounded untrusted Archive/WorldTools provenance evidence captured at intake.</summary>
    public string? ArchiveEvidenceJson { get; set; }

    /// <summary>Gets or sets the single Archive command name inferred for this WDL.</summary>
    public string? ArchiveWarpName { get; set; }

    /// <summary>Gets or sets the live Archive landing X coordinate captured before traversal.</summary>
    public double? ArchiveWarpX { get; set; }

    /// <summary>Gets or sets the live Archive landing Y coordinate captured before traversal.</summary>
    public double? ArchiveWarpY { get; set; }

    /// <summary>Gets or sets the live Archive landing Z coordinate captured before traversal.</summary>
    public double? ArchiveWarpZ { get; set; }

    /// <summary>Gets or sets the reviewable metadata source from which the warp name was inferred.</summary>
    public string? ArchiveWarpSource { get; set; }

    /// <summary>Gets or sets the warp row reused or created when the WDL completes.</summary>
    public int? WarpId { get; set; }

    /// <summary>Gets or sets <c>existing</c>, <c>new</c>, <c>review</c>, or <c>manual-*</c>.</summary>
    public string? MatchDecision { get; set; }

    /// <summary>Gets or sets the bounded confidence associated with the match decision.</summary>
    public double? MatchConfidence { get; set; }

    /// <summary>Gets or sets the concise evidence statement supporting the match decision.</summary>
    public string? MatchReason { get; set; }
}
