namespace Atlas;

/// <summary>
/// A per-location base render (a tile pyramid anchored at one base's coordinates) together with the owning
/// location's identity and position. This is the external-facing shape for the <c>/api/renders</c> resource:
/// it carries everything a map or tooling client needs to place and fetch a render's tiles — the tile URL
/// template, coordinate scheme, native zoom depth, block bounds, and the location it belongs to — without a
/// second lookup. These are distinct from the dimension-level primary layers served by <c>/api/maprenders</c>.
/// </summary>
public sealed class LocationRenderDto
{
    /// <summary>Gets or sets the render's unique identifier.</summary>
    public int RenderId { get; set; }

    /// <summary>Gets or sets the public API URL for this render record.</summary>
    public string ApiUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the owning location's row id.</summary>
    public int LocationId { get; set; }

    /// <summary>Gets or sets the owning location's canonical entity URL.</summary>
    public string LocationUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the owning location's public API URL.</summary>
    public string LocationApiUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the owning location's name.</summary>
    public string LocationName { get; set; } = string.Empty;

    /// <summary>Gets or sets the owning location's block X coordinate (in the location's own dimension).</summary>
    public int LocationX { get; set; }

    /// <summary>Gets or sets the owning location's block Z coordinate (in the location's own dimension).</summary>
    public int LocationZ { get; set; }

    /// <summary>Gets or sets the render's dimension (0 Overworld, 1 Nether, 2 End).</summary>
    public int Dimension { get; set; }

    /// <summary>Gets or sets the human-readable dimension name.</summary>
    public string DimensionName { get; set; } = string.Empty;

    /// <summary>Gets or sets the render's display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional render description.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the stable acquisition channel code for this render.</summary>
    public string? Source { get; set; }

    /// <summary>Gets or sets the exact Archive warp row represented by this render, when applicable.</summary>
    public int? ArchiveWarpId { get; set; }

    /// <summary>Gets or sets the public API URL for the represented Archive warp, when applicable.</summary>
    public string? ArchiveWarpApiUrl { get; set; }

    /// <summary>Gets or sets the exact copyable Archive warp name represented by this render.</summary>
    public string? ArchiveWarpName { get; set; }

    /// <summary>Gets whether this is a preserved single-player concept rather than a live 2b2t snapshot.</summary>
    public bool IsSinglePlayerConcept =>
        ArchiveWarpResolver.IsSinglePlayerConcept(ArchiveWarpName) ||
        ArchiveWarpResolver.IsSinglePlayerConcept(Name) ||
        ArchiveWarpResolver.IsSinglePlayerConcept(Description);

    /// <summary>Gets or sets the render scale label (e.g. "256k").</summary>
    public string Scale { get; set; } = string.Empty;

    /// <summary>Gets or sets the XYZ tile URL template (with <c>{z}/{y}/{x}</c> placeholders).</summary>
    public string TileUrlTemplate { get; set; } = string.Empty;

    /// <summary>Gets or sets whether the tile template contains a <c>{dn}</c> day/night token.</summary>
    public bool HasDayNight { get; set; }

    /// <summary>Gets or sets the deepest zoom level backed by native tile files (deeper zooms upscale).</summary>
    public int? MaxNativeZoom { get; set; }

    /// <summary>Gets or sets the coordinate and tile-layout contract used by <see cref="TileUrlTemplate"/>.</summary>
    public string? CoordinateScheme { get; set; }

    /// <summary>Gets or sets the world-download date the render was produced from, when known.</summary>
    public string? WorldDownloadDate { get; set; }

    /// <summary>Gets or sets a public ZIP URL for the preserved source WDL when available.</summary>
    public string? WorldDownloadUrl { get; set; }

    /// <summary>Gets or sets the source WDL metadata URL when available.</summary>
    public string? WorldDownloadMetadataUrl { get; set; }

    /// <summary>Gets or sets the source WDL scope when available.</summary>
    public string? WorldDownloadScope { get; set; }

    /// <summary>Gets or sets the source WDL SHA-256 digest when available.</summary>
    public string? WorldDownloadSha256 { get; set; }

    /// <summary>Gets or sets the source WDL provenance when available.</summary>
    public string? WorldDownloadSource { get; set; }

    /// <summary>Gets or sets the public interactive 3D view URL when a validated BlueMap derivative exists.</summary>
    public string? BlueMapUrl { get; set; }

    /// <summary>Gets or sets the API-relative path for the interactive 3D view.</summary>
    public string? BlueMapPath { get; set; }

    /// <summary>Gets or sets the renderer profile version used by the 3D derivative.</summary>
    public int? BlueMapProfileVersion { get; set; }

    /// <summary>Gets or sets the inclusive minimum rendered block X coordinate.</summary>
    public int? MinX { get; set; }

    /// <summary>Gets or sets the inclusive minimum rendered block Z coordinate.</summary>
    public int? MinZ { get; set; }

    /// <summary>Gets or sets the exclusive maximum rendered block X coordinate.</summary>
    public int? MaxXExclusive { get; set; }

    /// <summary>Gets or sets the exclusive maximum rendered block Z coordinate.</summary>
    public int? MaxZExclusive { get; set; }

    /// <summary>Gets or sets the UTC time the render was added, in round-trip text form.</summary>
    public string? DateAddedUtc { get; set; }
}
