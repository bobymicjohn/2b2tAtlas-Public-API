using System.ComponentModel.DataAnnotations;
using Atlas.Validation;

namespace Atlas;

/// <summary>
/// Represents a location in the game world.
/// </summary>
public class Location
{
    /// <summary>
    /// Gets or sets the unique identifier for the location.
    /// </summary>
    public int Rowid { get; set; }

    /// <summary>Gets or sets the canonical crawlable entity URL for this location.</summary>
    public string? CanonicalUrl { get; set; }

    /// <summary>Gets or sets the interactive Atlas detail-page URL for this location.</summary>
    public string? InteractiveUrl { get; set; }

    /// <summary>Gets or sets the public API URL for this location record.</summary>
    public string? ApiUrl { get; set; }

    /// <summary>
    /// Gets or sets the UUID from the original database.
    /// </summary>
    [StringLength(36)]
    public string? LocationUuid { get; set; }

    /// <summary>
    /// Gets or sets the name of the location.
    /// </summary>
    [Required(ErrorMessage = "Name is required")]
    [StringLength(255, ErrorMessage = "Name cannot exceed 255 characters")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the description of the location.
    /// </summary>
    [StringLength(2000, ErrorMessage = "Description cannot exceed 2000 characters")]
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the tags for the location.
    /// </summary>
    [StringLength(1000, ErrorMessage = "Tags cannot exceed 1000 characters")]
    public string? Tags { get; set; }

    /// <summary>
    /// Gets or sets the dimension of the location (0=Overworld, 1=Nether, 2=End).
    /// </summary>
    [Required(ErrorMessage = "Dimension is required")]
    public int Dimension { get; set; }

    /// <summary>
    /// Gets the name of the dimension.
    /// </summary>
    public string DimensionName
    {
        get
        {
            return Dimension switch
            {
                0 => "Overworld",
                1 => "Nether",
                2 => "End",
                _ => "Unknown"
            };
        }
    }

    /// <summary>
    /// Gets or sets the X coordinate of the location.
    /// </summary>
    [Required(ErrorMessage = "X coordinate is required")]
    public int X { get; set; }

    /// <summary>
    /// Gets or sets the Y coordinate of the location.
    /// </summary>
    public int? Y { get; set; }

    /// <summary>
    /// Gets or sets the Z coordinate of the location.
    /// </summary>
    [Required(ErrorMessage = "Z coordinate is required")]
    public int Z { get; set; }

    /// <summary>
    /// Gets or sets the wiki URL for the location.
    /// </summary>
    [StringLength(250)]
    [OptionalHttpUrl(ErrorMessage = "Wiki must be an HTTP or HTTPS URL")]
    public string? Wiki { get; set; }

    /// <summary>
    /// Gets or sets the video URL for the location.
    /// </summary>
    [StringLength(255)]
    [OptionalHttpUrl(ErrorMessage = "Video URL must be an HTTP or HTTPS URL")]
    public string? VideoUrl { get; set; }

    /// <summary>
    /// Gets or sets whether this is an End dimension location.
    /// </summary>
    public bool EndDimension { get; set; }

    /// <summary>
    /// Gets or sets the date and time when the location was added (UTC).
    /// </summary>
    public DateTime DateAddedUtc { get; set; }

    /// <summary>
    /// Gets or sets when this public location record last changed in a way that affects its entity page.
    /// </summary>
    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets the list of warps associated with the location.
    /// </summary>
    public List<Atlas.Locations.Warp> Warps { get; set; } = new List<Atlas.Locations.Warp>();

    /// <summary>
    /// Gets or sets the list of attachments associated with the location.
    /// </summary>
    public List<Atlas.Locations.Attachment> Attachments { get; set; } = new List<Atlas.Locations.Attachment>();

    /// <summary>
    /// Gets or sets the list of renders associated with the location.
    /// </summary>
    public List<Atlas.Locations.Render> Renders { get; set; } = new List<Atlas.Locations.Render>();

    /// <summary>
    /// Gets or sets explicit group attributions for this build. A null value on an update means
    /// that an older client did not send attribution data and existing links should be preserved.
    /// </summary>
    public List<LocationGroupAttribution>? Groups { get; set; }

    /// <summary>
    /// Gets the reviewed group names as a single searchable value for directory clients.
    /// </summary>
    public string GroupNames => string.Join(", ", (Groups ?? [])
        .Select(group => group.GroupName)
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));

    // ==============================================================================
    // COMPUTED PROPERTIES FOR SORTABLE/FILTERABLE COUNTS
    // ==============================================================================

    /// <summary>
    /// Gets the number of warps associated with this location.
    /// This property is sortable and filterable in data grids.
    /// </summary>
    public int WarpCount => Warps?.Count ?? 0;

    /// <summary>
    /// Gets the number of attachments associated with this location.
    /// This property is sortable and filterable in data grids.
    /// </summary>
    public int AttachmentCount => Attachments?.Count ?? 0;

    /// <summary>
    /// Gets the number of renders associated with this location.
    /// This property is sortable and filterable in data grids.
    /// </summary>
    public int RenderCount => Renders?.Count ?? 0;

    /// <summary>
    /// Gets the number of distinct public renders with a validated BlueMap view.
    /// This includes both Archive-warp-backed captures and older/manual preserved WDL renders.
    /// A validated relative path is the same availability signal used by the location-page 3D control.
    /// </summary>
    public int BlueMapReadyRenderCount => Renders?
        .Where(render => !string.IsNullOrWhiteSpace(render.BlueMapPath))
        .Select(render => render.Id)
        .Distinct()
        .Count() ?? 0;

    /// <summary>
    /// Gets the total number of related items (warps + attachments + renders).
    /// This property is sortable and filterable in data grids.
    /// </summary>
    public int TotalRelatedItems => WarpCount + AttachmentCount + RenderCount;

    /// <summary>
    /// Gets a summary string of all related content counts.
    /// Useful for display purposes.
    /// </summary>
    public string RelatedContentSummary
    {
        get
        {
            var parts = new List<string>();
            if (WarpCount > 0) parts.Add($"{WarpCount} warp{(WarpCount == 1 ? "" : "s")}");
            if (AttachmentCount > 0) parts.Add($"{AttachmentCount} file{(AttachmentCount == 1 ? "" : "s")}");
            if (RenderCount > 0) parts.Add($"{RenderCount} render{(RenderCount == 1 ? "" : "s")}");

            return parts.Any() ? string.Join(", ", parts) : "No content";
        }
    }
}
