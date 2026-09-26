using System.ComponentModel.DataAnnotations.Schema;

namespace _2b2tAtlas.Server.Models;

/// <summary>Many-to-many attribution between a highway and a group that built or maintained it.</summary>
public class HighwayGroup
{
    /// <summary>Attributed highway identifier.</summary>
    public int HighwayId { get; set; }

    /// <summary>Attributed group identifier.</summary>
    public int GroupId { get; set; }

    /// <summary>Group's documented role on the route.</summary>
    public string Role { get; set; } = "Contributor";

    /// <summary>A concise source note for the attribution.</summary>
    public string? Evidence { get; set; }

    /// <summary>Gets or sets the UTC time at which the attribution was recorded.</summary>
    public string DateAddedUtc { get; set; } = DateTime.UtcNow.ToString("o");

    /// <summary>Attributed highway navigation property.</summary>
    [ForeignKey(nameof(HighwayId))]
    public virtual Highway Highway { get; set; } = null!;

    /// <summary>Attributed group navigation property.</summary>
    [ForeignKey(nameof(GroupId))]
    public virtual Group Group { get; set; } = null!;
}
