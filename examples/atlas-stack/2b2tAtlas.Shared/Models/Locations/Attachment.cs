using System.ComponentModel.DataAnnotations;

namespace Atlas.Locations;

/// <summary>
/// Represents a file attachment associated with a location.
/// </summary>
public class Attachment
{
    /// <summary>
    /// Gets or sets the unique identifier for the attachment.
    /// </summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the public API URL for this attachment metadata record.</summary>
    public string? ApiUrl { get; set; }

    /// <summary>
    /// Gets or sets the ID of the associated location.
    /// </summary>
    [Required]
    public int LocationRowid { get; set; }

    /// <summary>
    /// Gets or sets the name of the file.
    /// </summary>
    [Required(ErrorMessage = "File name is required")]
    [StringLength(255, ErrorMessage = "File name cannot exceed 255 characters")]
    public string? FileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the path to the file / URL.
    /// </summary>
    [Required(ErrorMessage = "File path is required")]
    [StringLength(500, ErrorMessage = "File path cannot exceed 500 characters")]
    public string? Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the normalized media kind: Image, Video, Wiki, Link, Timeline, or Article.</summary>
    [StringLength(32)]
    public string? MediaType { get; set; }

    /// <summary>Gets or sets an optional lightweight HTTPS preview image.</summary>
    [StringLength(500)]
    public string? ThumbnailPath { get; set; }

    /// <summary>Gets or sets the original publication page retained for provenance.</summary>
    [StringLength(500)]
    public string? SourceUrl { get; set; }

    /// <summary>Gets or sets a concise public caption.</summary>
    [StringLength(500)]
    public string? Caption { get; set; }

    /// <summary>Gets or sets creator, archive, and license credit.</summary>
    [StringLength(500)]
    public string? Attribution { get; set; }

    /// <summary>
    /// Gets or sets the date and time when the attachment was added (UTC).
    /// </summary>
    public string? DateAddedUtc { get; set; }
}
