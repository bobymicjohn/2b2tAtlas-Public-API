using System.ComponentModel.DataAnnotations;
using Atlas.Locations;

namespace Atlas.Locations;

/// <summary>
/// Represents a render associated with a location.
/// </summary>
public class Render
{
    /// <summary>
    /// Unique identifier for the render.
    /// </summary>
    public int Id { get; set; }

    /// <summary>Public API URL for this render record.</summary>
    public string? ApiUrl { get; set; }

    /// <summary>
    /// Gets or sets the ID of the associated location.
    /// </summary>
    [Required]
    public int LocationRowid { get; set; }

    /// <summary>
    /// Name of the render.
    /// </summary>
    [Required(ErrorMessage = "Render name is required")]
    [StringLength(255, ErrorMessage = "Render name cannot exceed 255 characters")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Description of the render.
    /// </summary>
    [StringLength(1000, ErrorMessage = "Description cannot exceed 1000 characters")]
    public string? Description { get; set; }

    /// <summary>Stable acquisition channel code (for example, archive-collector).</summary>
    public string? Source { get; set; }

    /// <summary>Exact Archive warp whose WDL produced this render.</summary>
    public int? ArchiveWarpId { get; set; }

    /// <summary>Exact Archive warp whose WDL produced this render.</summary>
    public Warp? ArchiveWarp { get; set; }

    /// <summary>Gets whether this is a preserved single-player concept rather than a live 2b2t snapshot.</summary>
    public bool IsSinglePlayerConcept =>
        ArchiveWarp?.IsSinglePlayerConcept == true ||
        ArchiveWarpResolver.IsSinglePlayerConcept(Name) ||
        ArchiveWarpResolver.IsSinglePlayerConcept(Description);

    /// <summary>
    /// Dimension of the render.
    /// </summary>
    [Required]
    public int Dimension { get; set; }

    /// <summary>
    /// Scale of the render.
    /// </summary>
    [Required(ErrorMessage = "Scale is required")]
    [StringLength(50, ErrorMessage = "Scale cannot exceed 50 characters")]
    public string Scale { get; set; } = string.Empty;

    /// <summary>
    /// Path to the render tiles.
    /// </summary>
    [Required(ErrorMessage = "Tiles path is required")]
    [StringLength(500, ErrorMessage = "Tiles path cannot exceed 500 characters")]
    public string TilesPath { get; set; } = string.Empty;

    /// <summary>Whether the tile template contains separate day and night pyramids.</summary>
    public bool HasDayNight { get; set; }

    /// <summary>
    /// Path to the preview image.
    /// </summary>
    [StringLength(500, ErrorMessage = "Preview image path cannot exceed 500 characters")]
    public string? PreviewImagePath { get; set; }

    /// <summary>
    /// World download date.
    /// </summary>
    public string? WorldDownloadDate { get; set; }

    /// <summary>A public download URL for the preserved source WDL when this render is not Archive-warp-backed.</summary>
    public string? WorldDownloadUrl { get; set; }

    /// <summary>Public metadata URL for the preserved source WDL.</summary>
    public string? WorldDownloadMetadataUrl { get; set; }

    /// <summary>Source-world scope, such as <c>preserved-render-source</c>.</summary>
    public string? WorldDownloadScope { get; set; }

    /// <summary>Immutable SHA-256 digest of the preserved source WDL.</summary>
    public string? WorldDownloadSha256 { get; set; }

    /// <summary>Human-readable provenance recorded when the source WDL was ingested.</summary>
    public string? WorldDownloadSource { get; set; }

    /// <summary>Public interactive 3D view URL when a validated BlueMap derivative exists.</summary>
    public string? BlueMapUrl { get; set; }

    /// <summary>Same 3D view as an API-relative path for local and alternate-host clients.</summary>
    public string? BlueMapPath { get; set; }

    /// <summary>Renderer profile version used by the advertised 3D derivative.</summary>
    public int? BlueMapProfileVersion { get; set; }

    /// <summary>Inclusive minimum rendered block X coordinate.</summary>
    public int? MinX { get; set; }

    /// <summary>Inclusive minimum rendered block Z coordinate.</summary>
    public int? MinZ { get; set; }

    /// <summary>Exclusive maximum rendered block X coordinate.</summary>
    public int? MaxXExclusive { get; set; }

    /// <summary>Exclusive maximum rendered block Z coordinate.</summary>
    public int? MaxZExclusive { get; set; }

    /// <summary>Deepest zoom level backed by native tile files.</summary>
    public int? MaxNativeZoom { get; set; }

    /// <summary>Coordinate and tile-layout contract used by <see cref="TilesPath"/>.</summary>
    public string? CoordinateScheme { get; set; }

    /// <summary>
    /// Date and time when the render was added (UTC).
    /// </summary>
    [Required]
    public string DateAddedUtc { get; set; } = string.Empty;

    /// <summary>
    /// List of archive warps associated with this render.
    /// </summary>
    public List<Warp> ArchiveWarps { get; set; } = new List<Warp>();
}
