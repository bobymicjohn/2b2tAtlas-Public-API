using System.ComponentModel.DataAnnotations;
using Atlas.Locations;

namespace Atlas.Locations;

/// <summary>
/// Represents a render associated with a location.
/// </summary>
public class Render
{
    /// <summary>
    /// Gets or sets the unique identifier for the render.
    /// </summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the public API URL for this render record.</summary>
    public string? ApiUrl { get; set; }

    /// <summary>
    /// Gets or sets the ID of the associated location.
    /// </summary>
    [Required]
    public int LocationRowid { get; set; }

    /// <summary>
    /// Gets or sets the name of the render.
    /// </summary>
    [Required(ErrorMessage = "Render name is required")]
    [StringLength(255, ErrorMessage = "Render name cannot exceed 255 characters")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the description of the render.
    /// </summary>
    [StringLength(1000, ErrorMessage = "Description cannot exceed 1000 characters")]
    public string? Description { get; set; }

    /// <summary>Gets or sets the stable acquisition channel code (for example, archive-collector).</summary>
    public string? Source { get; set; }

    /// <summary>Gets or sets the exact Archive warp whose WDL produced this render.</summary>
    public int? ArchiveWarpId { get; set; }

    /// <summary>Gets or sets the exact Archive warp whose WDL produced this render.</summary>
    public Warp? ArchiveWarp { get; set; }

    /// <summary>Gets whether this is a preserved single-player concept rather than a live 2b2t snapshot.</summary>
    public bool IsSinglePlayerConcept =>
        ArchiveWarp?.IsSinglePlayerConcept == true ||
        ArchiveWarpResolver.IsSinglePlayerConcept(Name) ||
        ArchiveWarpResolver.IsSinglePlayerConcept(Description);

    /// <summary>
    /// Gets or sets the dimension of the render.
    /// </summary>
    [Required]
    public int Dimension { get; set; }

    /// <summary>
    /// Gets or sets the scale of the render.
    /// </summary>
    [Required(ErrorMessage = "Scale is required")]
    [StringLength(50, ErrorMessage = "Scale cannot exceed 50 characters")]
    public string Scale { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the path to the render tiles.
    /// </summary>
    [Required(ErrorMessage = "Tiles path is required")]
    [StringLength(500, ErrorMessage = "Tiles path cannot exceed 500 characters")]
    public string TilesPath { get; set; } = string.Empty;

    /// <summary>Gets or sets whether the tile template contains separate day and night pyramids.</summary>
    public bool HasDayNight { get; set; }

    /// <summary>
    /// Gets or sets the path to the preview image.
    /// </summary>
    [StringLength(500, ErrorMessage = "Preview image path cannot exceed 500 characters")]
    public string? PreviewImagePath { get; set; }

    /// <summary>
    /// Gets or sets the world download date.
    /// </summary>
    public string? WorldDownloadDate { get; set; }

    /// <summary>Gets or sets a public download URL for the preserved source WDL when this render is not Archive-warp-backed.</summary>
    public string? WorldDownloadUrl { get; set; }

    /// <summary>Gets or sets the public metadata URL for the preserved source WDL.</summary>
    public string? WorldDownloadMetadataUrl { get; set; }

    /// <summary>Gets or sets the source-world scope, such as <c>preserved-render-source</c>.</summary>
    public string? WorldDownloadScope { get; set; }

    /// <summary>Gets or sets the immutable SHA-256 digest of the preserved source WDL.</summary>
    public string? WorldDownloadSha256 { get; set; }

    /// <summary>Gets or sets the human-readable provenance recorded when the source WDL was ingested.</summary>
    public string? WorldDownloadSource { get; set; }

    /// <summary>Gets or sets the public interactive 3D view URL when a validated BlueMap derivative exists.</summary>
    public string? BlueMapUrl { get; set; }

    /// <summary>Gets or sets the same 3D view as an API-relative path for local and alternate-host clients.</summary>
    public string? BlueMapPath { get; set; }

    /// <summary>Gets or sets the renderer profile version used by the advertised 3D derivative.</summary>
    public int? BlueMapProfileVersion { get; set; }

    /// <summary>Gets or sets the inclusive minimum rendered block X coordinate.</summary>
    public int? MinX { get; set; }

    /// <summary>Gets or sets the inclusive minimum rendered block Z coordinate.</summary>
    public int? MinZ { get; set; }

    /// <summary>Gets or sets the exclusive maximum rendered block X coordinate.</summary>
    public int? MaxXExclusive { get; set; }

    /// <summary>Gets or sets the exclusive maximum rendered block Z coordinate.</summary>
    public int? MaxZExclusive { get; set; }

    /// <summary>Gets or sets the deepest zoom level backed by native tile files.</summary>
    public int? MaxNativeZoom { get; set; }

    /// <summary>Gets or sets the coordinate and tile-layout contract used by <see cref="TilesPath"/>.</summary>
    public string? CoordinateScheme { get; set; }

    /// <summary>
    /// Gets or sets the date and time when the render was added (UTC).
    /// </summary>
    [Required]
    public string DateAddedUtc { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the list of archive warps associated with this render.
    /// </summary>
    public List<Warp> ArchiveWarps { get; set; } = new List<Warp>();
}
