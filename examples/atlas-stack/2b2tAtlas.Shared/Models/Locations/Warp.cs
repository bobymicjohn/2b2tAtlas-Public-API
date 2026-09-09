using System.ComponentModel.DataAnnotations;
using Atlas;

namespace Atlas.Locations;

/// <summary>Represents a named legacy warp associated with an Atlas location.</summary>
public class Warp
{
    /// <summary>
    /// Gets or sets the unique identifier for the warp.
    /// </summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the public API URL for this Archive warp record.</summary>
    public string? ApiUrl { get; set; }

    /// <summary>
    /// Gets or sets the UUID from the original database.
    /// </summary>
    [StringLength(36)]
    public string? WarpUuid { get; set; }

    /// <summary>
    /// Gets or sets the location UUID foreign key.
    /// </summary>
    [StringLength(36)]
    public string? LocationUuidFk { get; set; }

    /// <summary>
    /// Gets or sets the foreign key to the location.
    /// </summary>
    public int? LocationRowid { get; set; }

    /// <summary>
    /// Gets or sets the warp name.
    /// </summary>
    [Required]
    [StringLength(255)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets whether Archive provenance explicitly marks this as a single-player concept build.</summary>
    public bool IsSinglePlayerConcept => ArchiveWarpResolver.IsSinglePlayerConcept(Name);

    /// <summary>
    /// Gets or sets the date and time when the warp was added.
    /// </summary>
    public DateTime TimeAdded { get; set; }

    /// <summary>Gets or sets the immutable WDL digest associated with this Archive warp.</summary>
    public string? ArchiveSha256 { get; set; }

    /// <summary>Gets or sets the public URL of the immutable, bounded Minecraft world ZIP.</summary>
    public string? WorldDownloadUrl { get; set; }

    /// <summary>Gets or sets the public JSON metadata URL for the bounded world download.</summary>
    public string? WorldDownloadMetadataUrl { get; set; }

    /// <summary>Gets or sets the capture scope, currently <c>bounded-footprint</c> for collector WDLs.</summary>
    public string? WorldDownloadScope { get; set; }

    /// <summary>Gets or sets the declared date of this warp's world download.</summary>
    public string? WorldDownloadDate { get; set; }

    /// <summary>Gets or sets the human-readable provenance attribution for this warp.</summary>
    public string? Source { get; set; }

    /// <summary>Gets or sets the live Archive landing X coordinate for this WDL warp.</summary>
    public double? ArchiveX { get; set; }

    /// <summary>Gets or sets the live Archive landing Y coordinate for this WDL warp.</summary>
    public double? ArchiveY { get; set; }

    /// <summary>Gets or sets the live Archive landing Z coordinate for this WDL warp.</summary>
    public double? ArchiveZ { get; set; }

    // Navigation property temporarily commented out to fix build
    // public global::Atlas.Location? Location { get; set; }
}

/// <summary>A public Archive warp record with its owning Atlas location and navigable links.</summary>
public sealed class WarpRecord : Warp
{
    /// <summary>Gets or sets the owning location's display name.</summary>
    public string LocationName { get; set; } = string.Empty;

    /// <summary>Gets or sets the owning location's Minecraft dimension.</summary>
    public Dimension Dimension { get; set; }

    /// <summary>Gets or sets the owning location's canonical entity URL.</summary>
    public string LocationUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the owning location's public API URL.</summary>
    public string LocationApiUrl { get; set; } = string.Empty;
}

/// <summary>Public metadata for one immutable, partial Minecraft Java world download.</summary>
public sealed class WorldDownloadRecord
{
    /// <summary>Gets or sets the Archive warp identifier.</summary>
    public int? WarpId { get; set; }
    /// <summary>Gets or sets the Archive warp name.</summary>
    public string? WarpName { get; set; }
    /// <summary>Gets or sets the source render identifier for a preserved pre-Archive WDL.</summary>
    public int? RenderId { get; set; }
    /// <summary>Gets or sets the source render name for a preserved pre-Archive WDL.</summary>
    public string? RenderName { get; set; }
    /// <summary>Gets whether this source is a preserved single-player concept rather than a live 2b2t snapshot.</summary>
    public bool IsSinglePlayerConcept =>
        ArchiveWarpResolver.IsSinglePlayerConcept(WarpName) ||
        ArchiveWarpResolver.IsSinglePlayerConcept(RenderName);
    /// <summary>Gets or sets the owning Atlas location identifier.</summary>
    public int LocationId { get; set; }
    /// <summary>Gets or sets the owning Atlas location name.</summary>
    public string LocationName { get; set; } = string.Empty;
    /// <summary>Gets or sets the canonical location entity URL.</summary>
    public string LocationUrl { get; set; } = string.Empty;
    /// <summary>Gets or sets the public location API URL.</summary>
    public string LocationApiUrl { get; set; } = string.Empty;
    /// <summary>Gets or sets this record's public metadata URL.</summary>
    public string MetadataUrl { get; set; } = string.Empty;
    /// <summary>Gets or sets the immutable ZIP download URL.</summary>
    public string DownloadUrl { get; set; } = string.Empty;
    /// <summary>Gets or sets the suggested download file name.</summary>
    public string FileName { get; set; } = string.Empty;
    /// <summary>Gets or sets the ZIP media type.</summary>
    public string ContentType { get; set; } = "application/zip";
    /// <summary>Gets or sets the ZIP size in bytes.</summary>
    public long ByteLength { get; set; }
    /// <summary>Gets or sets the lowercase SHA-256 digest of the ZIP.</summary>
    public string Sha256 { get; set; } = string.Empty;
    /// <summary>Gets or sets the capture scope.</summary>
    public string CaptureType { get; set; } = "bounded-footprint";
    /// <summary>Gets or sets the saved-world format.</summary>
    public string WorldFormat { get; set; } = "Minecraft Java Edition save";
    /// <summary>Gets or sets the save's playability classification.</summary>
    public string Playability { get; set; } = "partial-java-save";
    /// <summary>Gets or sets whether the ZIP represents a complete world.</summary>
    public bool IsCompleteWorld { get; set; }
    /// <summary>Gets or sets the historical-fidelity warning.</summary>
    public string Warning { get; set; } = string.Empty;
    /// <summary>Gets or sets the retained Minecraft dimension.</summary>
    public string Dimension { get; set; } = string.Empty;
    /// <summary>Gets or sets the number of retained chunks when known.</summary>
    public int? ChunkCount { get; set; }
    /// <summary>Gets or sets the half-open retained block bounds when known.</summary>
    public WorldDownloadBounds? Bounds { get; set; }
    /// <summary>Gets or sets the declared source-world date.</summary>
    public string? WorldDownloadDate { get; set; }
    /// <summary>Gets or sets the capture provenance.</summary>
    public string? Source { get; set; }
    /// <summary>Gets or sets the human-readable preservation attribution.</summary>
    public string Attribution { get; set; } = string.Empty;
}

/// <summary>Half-open native-dimension block bounds retained with a bounded world download.</summary>
public sealed class WorldDownloadBounds
{
    /// <summary>Gets or sets the inclusive minimum block X coordinate.</summary>
    public int MinX { get; set; }
    /// <summary>Gets or sets the inclusive minimum block Z coordinate.</summary>
    public int MinZ { get; set; }
    /// <summary>Gets or sets the exclusive maximum block X coordinate.</summary>
    public int MaxXExclusive { get; set; }
    /// <summary>Gets or sets the exclusive maximum block Z coordinate.</summary>
    public int MaxZExclusive { get; set; }
}
