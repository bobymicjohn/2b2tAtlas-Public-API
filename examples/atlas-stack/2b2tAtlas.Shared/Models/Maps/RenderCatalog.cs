namespace Atlas;

/// <summary>
/// A combined, external-facing catalog of every render the atlas exposes: the
/// dimension-level <b>primary</b> layers (full tile pyramids the map picker stacks)
/// and the per-location <b>base</b> renders (captures anchored to a single location).
/// Served read-only from <c>GET /api/maprenders/catalog</c> for third-party consumers.
/// </summary>
public sealed class RenderCatalogResponse
{
    /// <summary>Dimension-level full-map renders (the base layers of the map picker).</summary>
    public List<RenderCatalogEntry> Primary { get; set; } = [];

    /// <summary>Per-location base renders (one or more captures anchored to a location).</summary>
    public List<RenderCatalogEntry> Locations { get; set; } = [];
}

/// <summary>One render in the external catalog. Location-only fields are null for primary layers.</summary>
public sealed class RenderCatalogEntry
{
    /// <summary><c>primary</c> for a full-map layer, <c>location</c> for a per-location capture.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Stable identifier: the layer slug for primary, <c>render-{id}</c> for location.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Display name (e.g. "256k (2021)" or "+Z Border 2019-04-20").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>0 = Overworld, 1 = Nether, 2 = End.</summary>
    public int Dimension { get; set; }

    /// <summary>Human scale tag (e.g. "256k", "7k"), when known.</summary>
    public string? Scale { get; set; }

    /// <summary>
    /// Tile URL template with <c>{z}/{y}/{x}</c>, and <c>{dn}</c> (→ <c>day</c>/<c>night</c>)
    /// when <see cref="HasDayNight"/> is set. Null for proxied layers (see <see cref="CoordinateScheme"/>).
    /// </summary>
    public string? TileUrlTemplate { get; set; }

    /// <summary>True when the render has separate day/night tile sets (uses <c>{dn}</c>).</summary>
    public bool HasDayNight { get; set; }

    /// <summary>Deepest native zoom before the client upscales, when the pyramid is shallow.</summary>
    public int? MaxNativeZoom { get; set; }

    /// <summary>Tile contract: <c>atlas-sparse-v1</c>, <c>xyz-v1</c>, <c>atlas-nether-legacy-v1</c>, or <c>place-proxy</c>.</summary>
    public string? CoordinateScheme { get; set; }

    /// <summary>Attribution / provenance (e.g. "2b2t.place").</summary>
    public string? Source { get; set; }

    /// <summary>ISO date of the underlying world download, when known.</summary>
    public string? WorldDownloadDate { get; set; }

    /// <summary>Owning location row id (location renders only).</summary>
    public int? LocationId { get; set; }

    /// <summary>Owning location name (location renders only).</summary>
    public string? LocationName { get; set; }

    /// <summary>Owning location block X (location renders only).</summary>
    public int? LocationX { get; set; }

    /// <summary>Owning location block Z (location renders only).</summary>
    public int? LocationZ { get; set; }

    /// <summary>Inclusive minimum rendered block X (location renders only).</summary>
    public int? MinX { get; set; }

    /// <summary>Inclusive minimum rendered block Z (location renders only).</summary>
    public int? MinZ { get; set; }

    /// <summary>Exclusive maximum rendered block X (location renders only).</summary>
    public int? MaxXExclusive { get; set; }

    /// <summary>Exclusive maximum rendered block Z (location renders only).</summary>
    public int? MaxZExclusive { get; set; }
}
