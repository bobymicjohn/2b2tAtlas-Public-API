namespace _2b2tAtlas.Server.Models;

/// <summary>
/// Dimension-level world render (a full tile pyramid) auto-registered by the
/// ingest pipeline (GAMEPLAN §6b / Phase 1). Distinct from the per-location
/// <see cref="Render"/>. The physical table is created by <c>SchemaUpgrader</c>.
/// </summary>
public class MapRender
{
    /// <summary>Gets or sets the database-generated world-render identifier.</summary>
    public int Id { get; set; }

    /// <summary>Stable key for idempotent upsert (e.g. "overworld-1m-2026").</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Gets or sets the display name shown in the dimension layer picker.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>0 = Overworld, 1 = Nether, 2 = End.</summary>
    public int Dimension { get; set; }

    /// <summary>Gets or sets the Atlas scale label describing the world footprint represented by the pyramid.</summary>
    public string Scale { get; set; } = string.Empty;

    /// <summary>Tile URL template with {z}/{y}/{x} and optional {dn}.</summary>
    public string UrlTemplate { get; set; } = string.Empty;

    /// <summary>Gets or sets whether <c>{dn}</c> may select separate day and night pyramids, stored as 1 or 0.</summary>
    public int HasDayNight { get; set; }

    /// <summary>Gets or sets the deepest generated XYZ zoom before the map client must upscale tiles.</summary>
    public int? MaxNativeZoom { get; set; }

    /// <summary>Gets or sets the declared date of the source world download.</summary>
    public string? WorldDownloadDate { get; set; }

    /// <summary>Gets or sets the public provenance label for the world download or render.</summary>
    public string? Source { get; set; }

    /// <summary>Gets or sets the relative order within a dimension's layer picker.</summary>
    public int SortOrder { get; set; }

    /// <summary>Gets or sets whether anonymous map clients may discover the render, stored as 1 or 0.</summary>
    public int IsPublished { get; set; }

    /// <summary>Gets or sets the UTC registration timestamp in round-trip text form.</summary>
    public string DateAddedUtc { get; set; } = DateTime.UtcNow.ToString("o");
}
