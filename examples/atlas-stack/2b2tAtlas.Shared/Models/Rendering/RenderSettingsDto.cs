namespace Atlas;

/// <summary>
/// The editable unMINED render-pipeline settings surfaced by the admin "Render Settings" tab. Combines the
/// structured per-dimension render flags (rebuilt into <c>renderer.json</c>) with the raw unMINED colour,
/// biome-tint, and block-style config files that an experienced operator edits directly. The unMINED
/// executable path and its SHA-256 pin are exposed read-only and never written, so the fail-closed render
/// gate cannot be altered from the UI.
/// </summary>
public sealed class RenderSettingsDto
{
    /// <summary>Gets or sets Atlas colour-preserving post-processing for uNmINeD night output.</summary>
    public NightColorGradeOptions NightColorGrade { get; set; } = new();

    /// <summary>Per-dimension structured render options.</summary>
    public List<DimensionRenderOptions> Dimensions { get; set; } = new();

    /// <summary>Raw contents of unMINED's <c>custom.colors.txt</c> (block/map colour overrides).</summary>
    public string ColorsText { get; set; } = string.Empty;

    /// <summary>Raw contents of unMINED's <c>custom.biometints.txt</c> (per-biome grass/foliage/water).</summary>
    public string BiomeTintsText { get; set; } = string.Empty;

    /// <summary>Raw contents of unMINED's <c>custom.blockstyles.txt</c> (per-block styling).</summary>
    public string BlockStylesText { get; set; } = string.Empty;

    /// <summary>Pinned unMINED version string (read-only).</summary>
    public string RendererVersion { get; set; } = string.Empty;

    /// <summary>UnMINED executable path (read-only).</summary>
    public string ExecutablePath { get; set; } = string.Empty;

    /// <summary>Names of the staged sample worlds available for preview rendering.</summary>
    public List<string> SampleWorlds { get; set; } = new();
}

/// <summary>Structured, intuitive render flags for one dimension, mapped to/from unMINED CLI arguments.</summary>
public sealed class DimensionRenderOptions
{
    /// <summary>Dimension key: <c>overworld</c>, <c>nether</c>, or <c>end</c>.</summary>
    public string Dimension { get; set; } = string.Empty;

    /// <summary>Shadow mode: <c>default</c>, <c>false</c>, <c>true</c>, <c>2d</c>, <c>3d</c>, or <c>3do</c>.</summary>
    public string Shadows { get; set; } = "default";

    /// <summary>Whether night lighting is used.</summary>
    public bool Night { get; set; }

    /// <summary>Highest Y coordinate rendered (top slice), or null for the default.</summary>
    public int? TopY { get; set; }

    /// <summary>Lowest Y coordinate rendered (bottom slice), or null for the default.</summary>
    public int? BottomY { get; set; }

    /// <summary>Background colour as <c>#rrggbb</c>, or null for the default.</summary>
    public string? Background { get; set; }

    /// <summary>Output image format (png, jpeg, webp, bmp).</summary>
    public string ImageFormat { get; set; } = "png";
}

/// <summary>The result of a bounded sample preview render used to check settings before publishing.</summary>
public sealed class RenderPreviewResult
{
    /// <summary>Whether the preview render succeeded.</summary>
    public bool Ok { get; set; }

    /// <summary>Gets or sets an explanation when the preview failed or is unavailable.</summary>
    public string? Error { get; set; }

    /// <summary>Rendered PNG as a base64 string, when successful.</summary>
    public string? ImageBase64 { get; set; }

    /// <summary>Night preview PNG as a base64 string.</summary>
    public string? NightImageBase64 { get; set; }

    /// <summary>Rendered image width in pixels.</summary>
    public int WidthPx { get; set; }

    /// <summary>Rendered image height in pixels.</summary>
    public int HeightPx { get; set; }

    /// <summary>Render time in milliseconds.</summary>
    public long ElapsedMs { get; set; }

    /// <summary>Inclusive preview minimum block X coordinate.</summary>
    public int MinX { get; set; }

    /// <summary>Inclusive preview minimum block Z coordinate.</summary>
    public int MinZ { get; set; }

    /// <summary>Exclusive preview maximum block X coordinate.</summary>
    public int MaxXExclusive { get; set; }

    /// <summary>Exclusive preview maximum block Z coordinate.</summary>
    public int MaxZExclusive { get; set; }
}

/// <summary>Result of preflighting and queuing a bulk re-render of every completed location render.</summary>
public sealed class BulkRerenderResult
{
    /// <summary>Number of completed jobs queued for rebuilding.</summary>
    public int QueuedJobs { get; set; }

    /// <summary>Number of distinct content-addressed WDL archives involved.</summary>
    public int DistinctArchives { get; set; }

    /// <summary>Gets or sets an operator-facing summary.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>Current state of the bulk re-render queue.</summary>
public sealed class BulkRerenderStatus
{
    /// <summary>Gets or sets jobs waiting to be claimed.</summary>
    public int Queued { get; set; }

    /// <summary>Gets or sets jobs currently claimed, running, or completing.</summary>
    public int Running { get; set; }

    /// <summary>Gets or sets jobs that failed while marked for re-render.</summary>
    public int Failed { get; set; }
}
