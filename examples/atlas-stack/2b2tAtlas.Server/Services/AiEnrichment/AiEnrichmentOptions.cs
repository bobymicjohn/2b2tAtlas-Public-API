namespace _2b2tAtlas.Server.Services.AiEnrichment;

/// <summary>
/// Configuration for the local AI wiki-enrichment engine (bound from the
/// <c>AiEnrichment</c> configuration section). The engine matches Atlas locations to
/// 2b2t wiki pages and drafts descriptions using a loopback Ollama model. It is
/// disabled by default and only ever runs on the trusted server host.
/// </summary>
public sealed class AiEnrichmentOptions
{
    /// <summary>The configuration section name bound to these options.</summary>
    public const string SectionName = "AiEnrichment";

    /// <summary>Gets or sets whether the enrichment engine may call the model and wiki. Off by default.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the loopback Ollama base URL used for generation (e.g. the local qwen model).</summary>
    public string OllamaBaseUrl { get; set; } = "http://127.0.0.1:11434";

    /// <summary>Gets or sets the Ollama model tag used to draft descriptions and rank matches.</summary>
    public string Model { get; set; } = "qwen3.8:27b";

    /// <summary>Gets or sets the MediaWiki <c>api.php</c> endpoint of the approved 2b2t wiki.</summary>
    public string WikiApiBase { get; set; } = "https://2b2t.miraheze.org/w/api.php";

    /// <summary>Gets or sets the human-facing wiki article base URL used to build page links.</summary>
    public string WikiSiteBase { get; set; } = "https://2b2t.wikioasis.org/wiki/";

    /// <summary>Gets or sets the identifying User-Agent sent with every wiki request.</summary>
    public string UserAgent { get; set; } = "2b2tAtlas metadata matcher/1.0 (administrator review only)";

    /// <summary>Gets or sets the per-request timeout, in seconds, for wiki and model calls.</summary>
    public int RequestTimeoutSeconds { get; set; } = 120;

    /// <summary>Gets or sets the maximum length, in characters, of a generated description.</summary>
    public int MaxDescriptionChars { get; set; } = 600;

    /// <summary>Gets or sets the maximum wiki candidate pages considered per location.</summary>
    public int MaxCandidates { get; set; } = 5;

    /// <summary>
    /// Gets or sets the coordinate agreement tolerance in Overworld blocks. A wiki coordinate within
    /// this distance of the location (dimension-projected) is treated as confirming the match.
    /// </summary>
    public int CoordinateToleranceBlocks { get; set; } = 512;

    /// <summary>
    /// Gets or sets the minimum confidence (0..1) at which a suggestion may be auto-applied. Weaker
    /// suggestions are routed to the manual review queue instead.
    /// </summary>
    public double AutoApplyMinConfidence { get; set; } = 0.8;

    /// <summary>
    /// Gets or sets the revision-pinned group/build evidence index produced by the read-only wiki audit.
    /// Relative paths resolve against the API content root.
    /// </summary>
    public string GroupEvidenceIndexPath { get; set; } = "enrichment/2b2t-wiki-group-audit.json";

    /// <summary>Gets or sets how old the group evidence index may be before it is ignored.</summary>
    public int GroupEvidenceMaxAgeDays { get; set; } = 14;

    /// <summary>
    /// Gets or sets wiki article titles known not to represent a distinct organization. These are kept in
    /// audit output but never offered as new Atlas groups.
    /// </summary>
    public string[] GroupDiscoveryDenyList { get; set; } = ["Omega City"];

    /// <summary>
    /// Gets or sets the path to the GPU game-mode lock. When this file exists the engine stands down so
    /// it never competes with gaming or an active render for the GPU.
    /// </summary>
    public string GameModeLockPath { get; set; } = @"C:\AtlasExample\Ops\gpu-game-mode.lock";
}
