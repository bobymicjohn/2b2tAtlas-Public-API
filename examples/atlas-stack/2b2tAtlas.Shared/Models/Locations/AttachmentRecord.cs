namespace Atlas.Locations;

/// <summary>A public attachment record with enough location context for independent API consumers.</summary>
public sealed class AttachmentRecord : Attachment
{
    /// <summary>Owning location's display name.</summary>
    public string LocationName { get; set; } = string.Empty;

    /// <summary>Owning location's dimension.</summary>
    public Dimension Dimension { get; set; }

    /// <summary>Owning location's canonical entity URL.</summary>
    public string LocationUrl { get; set; } = string.Empty;

    /// <summary>Owning location's public API URL.</summary>
    public string LocationApiUrl { get; set; } = string.Empty;
}
