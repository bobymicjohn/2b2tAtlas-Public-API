using System.ComponentModel.DataAnnotations;
using Atlas;

namespace Atlas.Locations;

/// <summary>Represents a named legacy warp associated with an Atlas location.</summary>
public class Warp
{
    /// <summary>
    /// Unique identifier for the warp.
    /// </summary>
    public int Id { get; set; }

    /// <summary>Public API URL for this Archive warp record.</summary>
    public string? ApiUrl { get; set; }

    /// <summary>
    /// Gets or sets the UUID from the original database.
    /// </summary>
    [StringLength(36)]
    public string? WarpUuid { get; set; }

    /// <summary>
    /// Location UUID foreign key.
    /// </summary>
    [StringLength(36)]
    public string? LocationUuidFk { get; set; }

    /// <summary>
    /// Foreign key to the location.
    /// </summary>
    public int? LocationRowid { get; set; }

    /// <summary>
    /// Warp name.
    /// </summary>
    [Required]
    [StringLength(255)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets whether Archive provenance explicitly marks this as a single-player concept build.</summary>
    public bool IsSinglePlayerConcept => ArchiveWarpResolver.IsSinglePlayerConcept(Name);

    /// <summary>
    /// Date and time when the warp was added.
    /// </summary>
    public DateTime TimeAdded { get; set; }

    /// <summary>Immutable WDL digest associated with this Archive warp.</summary>
    public string? ArchiveSha256 { get; set; }

    /// <summary>Public URL of the immutable, bounded Minecraft world ZIP.</summary>
    public string? WorldDownloadUrl { get; set; }

    /// <summary>Public JSON metadata URL for the bounded world download.</summary>
    public string? WorldDownloadMetadataUrl { get; set; }

    /// <summary>Capture scope, currently <c>bounded-footprint</c> for collector WDLs.</summary>
    public string? WorldDownloadScope { get; set; }

    /// <summary>Declared date of this warp's world download.</summary>
    public string? WorldDownloadDate { get; set; }

    /// <summary>Human-readable provenance attribution for this warp.</summary>
    public string? Source { get; set; }

    /// <summary>Live Archive landing X coordinate for this WDL warp.</summary>
    public double? ArchiveX { get; set; }

    /// <summary>Live Archive landing Y coordinate for this WDL warp.</summary>
    public double? ArchiveY { get; set; }

    /// <summary>Live Archive landing Z coordinate for this WDL warp.</summary>
    public double? ArchiveZ { get; set; }

    // Navigation property temporarily commented out to fix build
    // public global::Atlas.Location? Location { get; set; }
}

/// <summary>A public Archive warp record with its owning Atlas location and navigable links.</summary>
public sealed class WarpRecord : Warp
{
    /// <summary>Owning location's display name.</summary>
    public string LocationName { get; set; } = string.Empty;

    /// <summary>Owning location's Minecraft dimension.</summary>
    public Dimension Dimension { get; set; }

    /// <summary>Owning location's canonical entity URL.</summary>
    public string LocationUrl { get; set; } = string.Empty;

    /// <summary>Owning location's public API URL.</summary>
    public string LocationApiUrl { get; set; } = string.Empty;
}

/// <summary>Public metadata for one immutable, partial Minecraft Java world download.</summary>
public sealed class WorldDownloadRecord
{
    /// <summary>Gets or sets the Archive warp identifier.</summary>
    public int? WarpId { get; set; }
    /// <summary>Gets or sets the Archive warp name.</summary>
    public string? WarpName { get; set; }
    /// <summary>Source render identifier for a preserved pre-Archive WDL.</summary>
    public int? RenderId { get; set; }
    /// <summary>Source render name for a preserved pre-Archive WDL.</summary>
    public string? RenderName { get; set; }
    /// <summary>Gets whether this source is a preserved single-player concept rather than a live 2b2t snapshot.</summary>
    public bool IsSinglePlayerConcept =>
        ArchiveWarpResolver.IsSinglePlayerConcept(WarpName) ||
        ArchiveWarpResolver.IsSinglePlayerConcept(RenderName);
    /// <summary>Owning Atlas location identifier.</summary>
    public int LocationId { get; set; }
    /// <summary>Owning Atlas location name.</summary>
    public string LocationName { get; set; } = string.Empty;
    /// <summary>Canonical location entity URL.</summary>
    public string LocationUrl { get; set; } = string.Empty;
    /// <summary>Public location API URL.</summary>
    public string LocationApiUrl { get; set; } = string.Empty;
    /// <summary>Gets or sets this record's public metadata URL.</summary>
    public string MetadataUrl { get; set; } = string.Empty;
    /// <summary>Immutable ZIP download URL.</summary>
    public string DownloadUrl { get; set; } = string.Empty;
    /// <summary>Suggested download file name.</summary>
    public string FileName { get; set; } = string.Empty;
    /// <summary>Gets or sets the ZIP media type.</summary>
    public string ContentType { get; set; } = "application/zip";
    /// <summary>Gets or sets the ZIP size in bytes.</summary>
    public long ByteLength { get; set; }
    /// <summary>Lowercase SHA-256 digest of the ZIP.</summary>
    public string Sha256 { get; set; } = string.Empty;
    /// <summary>Capture scope.</summary>
    public string CaptureType { get; set; } = "bounded-footprint";
    /// <summary>Saved-world format.</summary>
    public string WorldFormat { get; set; } = "Minecraft Java Edition save";
    /// <summary>Save's playability classification.</summary>
    public string Playability { get; set; } = "partial-java-save";
    /// <summary>Whether the ZIP represents a complete world.</summary>
    public bool IsCompleteWorld { get; set; }
    /// <summary>Historical-fidelity warning.</summary>
    public string Warning { get; set; } = string.Empty;
    /// <summary>Retained Minecraft dimension.</summary>
    public string Dimension { get; set; } = string.Empty;
    /// <summary>Number of retained chunks when known.</summary>
    public int? ChunkCount { get; set; }
    /// <summary>Half-open retained block bounds when known.</summary>
    public WorldDownloadBounds? Bounds { get; set; }
    /// <summary>Declared source-world date.</summary>
    public string? WorldDownloadDate { get; set; }
    /// <summary>Capture provenance.</summary>
    public string? Source { get; set; }
    /// <summary>Human-readable preservation attribution.</summary>
    public string Attribution { get; set; } = string.Empty;
}

/// <summary>Half-open native-dimension block bounds retained with a bounded world download.</summary>
public sealed class WorldDownloadBounds
{
    /// <summary>Inclusive minimum block X coordinate.</summary>
    public int MinX { get; set; }
    /// <summary>Inclusive minimum block Z coordinate.</summary>
    public int MinZ { get; set; }
    /// <summary>Exclusive maximum block X coordinate.</summary>
    public int MaxXExclusive { get; set; }
    /// <summary>Exclusive maximum block Z coordinate.</summary>
    public int MaxZExclusive { get; set; }
}
