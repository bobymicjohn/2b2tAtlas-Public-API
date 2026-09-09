namespace Atlas.Locations;

/// <summary>A public attachment record with enough location context for independent API consumers.</summary>
public sealed class AttachmentRecord : Attachment
{
    /// <summary>Gets or sets the owning location's display name.</summary>
    public string LocationName { get; set; } = string.Empty;

    /// <summary>Gets or sets the owning location's dimension.</summary>
    public Dimension Dimension { get; set; }

    /// <summary>Gets or sets the owning location's canonical entity URL.</summary>
    public string LocationUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the owning location's public API URL.</summary>
    public string LocationApiUrl { get; set; } = string.Empty;
}
