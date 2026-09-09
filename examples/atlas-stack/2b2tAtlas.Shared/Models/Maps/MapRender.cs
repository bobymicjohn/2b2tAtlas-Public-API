namespace Atlas;

/// <summary>
/// A dimension-level world render (a full tile pyramid), as opposed to a
/// per-location base render. These are what the map's render picker stacks
/// (e.g. "256k (2021)", "1M² (2026)"). The ingest pipeline auto-registers these
/// so new world downloads appear as map layers without any manual admin step
/// (GAMEPLAN §6b / Phase 1).
/// </summary>
public class MapRenderDto
{
    /// <summary>Gets or sets the render's persistent identifier.</summary>
    public int Id { get; set; }

    /// <summary>Stable key for idempotent upsert by the ingestor (e.g. "overworld-1m-2026").</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Label shown in the render picker.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>0 = Overworld, 1 = Nether, 2 = End (matches the DB + client DIM_INDEX).</summary>
    public int Dimension { get; set; }

    /// <summary>Human scale tag (e.g. "1M", "256k").</summary>
    public string Scale { get; set; } = string.Empty;

    /// <summary>
    /// Tile URL template containing <c>{z}/{y}/{x}</c>, and optionally a <c>{dn}</c>
    /// token replaced with <c>day</c>/<c>night</c> when <see cref="HasDayNight"/> is set.
    /// </summary>
    public string UrlTemplate { get; set; } = string.Empty;

    /// <summary>True if the render has separate day/night tile sets (uses <c>{dn}</c>).</summary>
    public bool HasDayNight { get; set; }

    /// <summary>Deepest native zoom if the pyramid is shallower than the map max (else null).</summary>
    public int? MaxNativeZoom { get; set; }

    /// <summary>ISO date of the underlying world download (drives the time slider).</summary>
    public string? WorldDownloadDate { get; set; }

    /// <summary>Attribution / provenance (e.g. "2b2t.place 1M²").</summary>
    public string? Source { get; set; }

    /// <summary>Ordering within a dimension's picker (lower = earlier).</summary>
    public int SortOrder { get; set; }

    /// <summary>Only published renders are returned on the public endpoint.</summary>
    public bool IsPublished { get; set; }

    /// <summary>Gets or sets the UTC time at which the render was registered.</summary>
    public DateTime DateAddedUtc { get; set; }
}
