namespace _2b2tAtlas.Server.Services;

/// <summary>
/// Filesystem locations the render-settings admin surface reads and writes. The API runs on the same host as
/// the ingestion worker, so it edits the same <c>renderer.json</c> and unMINED config files the worker renders
/// with. The unMINED config directory and executable are derived from <c>renderer.json</c> at runtime.
/// </summary>
public sealed class RenderSettingsOptions
{
    /// <summary>The configuration section bound to these options.</summary>
    public const string SectionName = "RenderSettings";

    /// <summary>Gets or sets the path to the worker's renderer profile JSON.</summary>
    public string RendererJsonPath { get; set; } = @"C:\AtlasExample\Ingest\config\renderer.json";

    /// <summary>Gets or sets the root folder holding named sample worlds (each a subfolder with level.dat) for previews.</summary>
    public string SampleWorldsRoot { get; set; } = @"C:\AtlasExample\Ingest\sample-worlds";

    /// <summary>Gets or sets the zoom level used for the sample preview render.</summary>
    public int PreviewZoom { get; set; }

    /// <summary>Gets or sets the maximum time, in seconds, a preview render may run.</summary>
    public int PreviewTimeoutSeconds { get; set; } = 90;

    /// <summary>Gets or sets the maximum size, in bytes, accepted for each raw config file.</summary>
    public int MaxConfigFileBytes { get; set; } = 262_144;
}
