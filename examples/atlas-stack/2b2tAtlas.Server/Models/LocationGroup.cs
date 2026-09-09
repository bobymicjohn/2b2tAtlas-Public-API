using System.ComponentModel.DataAnnotations.Schema;

namespace _2b2tAtlas.Server.Models;

/// <summary>Many-to-many attribution between a documented location and a group that built it.</summary>
public class LocationGroup
{
    /// <summary>Gets or sets the attributed Atlas location row identifier.</summary>
    public int LocationRowid { get; set; }
    /// <summary>Gets or sets the attributed group identifier.</summary>
    public int GroupId { get; set; }
    /// <summary>Gets or sets the group's role at the location.</summary>
    public string Role { get; set; } = "Builder";
    /// <summary>Gets or sets the UTC timestamp at which the attribution was recorded.</summary>
    public string DateAddedUtc { get; set; } = DateTime.UtcNow.ToString("o");

    /// <summary>Gets or sets the attributed location navigation property.</summary>
    [ForeignKey(nameof(LocationRowid))]
    public virtual Location Location { get; set; } = null!;

    /// <summary>Gets or sets the attributed group navigation property.</summary>
    [ForeignKey(nameof(GroupId))]
    public virtual Group Group { get; set; } = null!;
}
