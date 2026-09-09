namespace _2b2tAtlas.Server.Services;

/// <summary>Configuration for immutable per-render BlueMap derivatives.</summary>
public sealed class BlueMapOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "BlueMap";

    /// <summary>Filesystem root containing content-addressed render generations.</summary>
    public string OutputRoot { get; set; } = @"F:\AtlasExample\AtlasBlueMap\location-renders";

    /// <summary>Public API path that serves the static BlueMap generations.</summary>
    public string RequestPath { get; set; } = "/bluemap";

    /// <summary>Canonical public origin used in API records.</summary>
    public string PublicOrigin { get; set; } = "http://127.0.0.1:5297";

    /// <summary>How long the manifest catalog is retained between filesystem scans.</summary>
    public int CatalogCacheSeconds { get; set; } = 30;

    /// <summary>Durable checkpoint written atomically by the BlueMap batch renderer.</summary>
    public string StatusPath { get; set; } = @"C:\AtlasExample\Ingest\bluemap\location-render-status.json";

    /// <summary>Renderer logs whose write times prove activity during a long individual render.</summary>
    public string[] ActivityLogPaths { get; set; } =
    [
        @"C:\AtlasExample\Ingest\bluemap\full-batch-stdout.log",
        @"C:\AtlasExample\Ingest\bluemap\full-batch-stderr.log"
    ];

    /// <summary>Age after which a nominally running batch is reported as stale.</summary>
    public int StatusStaleAfterMinutes { get; set; } = 30;

    /// <summary>
    /// Oldest renderer profile that is safe to advertise. This lets a broken
    /// immutable generation be withdrawn without deleting its forensic output.
    /// </summary>
    public int MinimumProfileVersion { get; set; } = 7;
}
