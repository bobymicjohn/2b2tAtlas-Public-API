namespace _2b2tAtlas.Server.Services;

/// <summary>Configures the content-addressed WDL archive used for uploads and bulk re-renders.</summary>
public sealed class WdlArchiveOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "WdlArchive";

    /// <summary>Gets or sets the local primary archive root.</summary>
    public string Root { get; set; } = @"E:\AtlasExample\WorldDownloads";
}
