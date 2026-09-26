using System.ComponentModel.DataAnnotations;
using Atlas.Auth;

namespace Atlas.Auth;

/// <summary>
/// Database statistics model.
/// </summary>
public class DatabaseStats
{
    /// <summary>Total number of stored locations.</summary>
    public int Locations { get; set; }

    /// <summary>Total number of stored warp aliases.</summary>
    public int Warps { get; set; }

    /// <summary>Total number of stored attachment links.</summary>
    public int Attachments { get; set; }

    /// <summary>Total number of stored render records.</summary>
    public int Renders { get; set; }

    /// <summary>Number of locations added during the current month.</summary>
    public int LocationsMonth { get; set; }

    /// <summary>Number of warp aliases added during the current month.</summary>
    public int WarpsMonth { get; set; }

    /// <summary>Number of attachment links added during the current month.</summary>
    public int AttachmentsMonth { get; set; }

    /// <summary>Number of render records added during the current month.</summary>
    public int RendersMonth { get; set; }
}
