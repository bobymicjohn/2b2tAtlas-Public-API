using System.ComponentModel.DataAnnotations;
using Atlas.Auth;

namespace Atlas.Auth;

/// <summary>
/// Database statistics model.
/// </summary>
public class DatabaseStats
{
    /// <summary>Gets or sets the total number of stored locations.</summary>
    public int Locations { get; set; }

    /// <summary>Gets or sets the total number of stored warp aliases.</summary>
    public int Warps { get; set; }

    /// <summary>Gets or sets the total number of stored attachment links.</summary>
    public int Attachments { get; set; }

    /// <summary>Gets or sets the total number of stored render records.</summary>
    public int Renders { get; set; }

    /// <summary>Gets or sets the number of locations added during the current month.</summary>
    public int LocationsMonth { get; set; }

    /// <summary>Gets or sets the number of warp aliases added during the current month.</summary>
    public int WarpsMonth { get; set; }

    /// <summary>Gets or sets the number of attachment links added during the current month.</summary>
    public int AttachmentsMonth { get; set; }

    /// <summary>Gets or sets the number of render records added during the current month.</summary>
    public int RendersMonth { get; set; }
}