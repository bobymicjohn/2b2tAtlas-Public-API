using System.Text.Json;
using System.Text.Json.Serialization;
using Atlas.Ingestor.Security;

namespace Atlas.Ingestor.Worker;

/// <summary>Configures the local ingestion worker, private storage roots, renderer, and API boundary.</summary>
public sealed class WorkerOptions
{
    /// <value>Configuration schema version; currently must be 1.</value>
    public int SchemaVersion { get; set; }
    /// <value>Atlas API base URL; non-loopback endpoints must use HTTPS.</value>
    public string ApiBase { get; set; } = string.Empty;
    /// <value>Name of the environment variable containing the worker API key.</value>
    public string ApiKeyEnvironment { get; set; } = string.Empty;
    /// <value>Existing private directory containing accepted local ZIP files.</value>
    public string IntakeRoot { get; set; } = string.Empty;
    /// <value>Content-addressed landing root used to preserve and serve every source WDL.</value>
    public string ArchiveRoot { get; set; } = string.Empty;
    /// <value>Private content-addressed job workspace.</value>
    public string WorkRoot { get; set; } = string.Empty;
    /// <value>Path to the strict, hash-pinned renderer profile.</value>
    public string RendererProfile { get; set; } = string.Empty;
    /// <value>Private staging root from which verified tiles are atomically published.</value>
    public string PublishRoot { get; set; } = string.Empty;
    /// <value>HTTPS base URL that already serves <see cref="PublishRoot"/>.</value>
    public string PublicTileRoot { get; set; } = string.Empty;
    /// <value>Certified Atlas tile scheme used by automated Overworld jobs.</value>
    public string TileScheme { get; set; } = string.Empty;
    /// <value>Delay between empty claims or recoverable polling errors, in seconds.</value>
    public int PollSeconds { get; set; } = 15;

    /// <summary>Loads strict JSON configuration and validates all URL, root, overlap, scheme, and timing invariants.</summary>
    /// <param name="path">Path to the worker configuration JSON.</param>
    /// <param name="cancellationToken">Token that cancels deserialization.</param>
    /// <returns>The validated worker options.</returns>
    /// <exception cref="InputValidationException">Configuration is empty, unknown, unsupported, insecure, missing, or internally inconsistent.</exception>
    /// <exception cref="JsonException">The configuration is malformed or incompatible.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    /// <remarks>Validation creates missing work and publish directories, but never creates the intake root or renderer profile.</remarks>
    public static async Task<WorkerOptions> LoadAsync(string path, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var options = await JsonSerializer.DeserializeAsync<WorkerOptions>(input, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        }, cancellationToken) ?? throw new InputValidationException("Worker configuration is empty.");
        options.Validate();
        return options;
    }

    /// <value>Absolute normalized intake root.</value>
    public string FullIntakeRoot => Path.GetFullPath(IntakeRoot);
    /// <value>Absolute normalized work root.</value>
    public string FullWorkRoot => Path.GetFullPath(WorkRoot);
    /// <value>Absolute normalized WDL archive root.</value>
    public string FullArchiveRoot => Path.GetFullPath(ArchiveRoot);
    /// <value>Absolute normalized publication root.</value>
    public string FullPublishRoot => Path.GetFullPath(PublishRoot);
    /// <value>Absolute normalized renderer-profile path.</value>
    public string FullRendererProfile => Path.GetFullPath(RendererProfile);

    private void Validate()
    {
        if (SchemaVersion != 1)
            throw new InputValidationException("Worker schemaVersion must be 1.");
        if (!Uri.TryCreate(ApiBase, UriKind.Absolute, out var api) ||
            !api.IsLoopback && api.Scheme != Uri.UriSchemeHttps)
            throw new InputValidationException("Worker apiBase must use HTTPS unless it is loopback.");
        if (string.IsNullOrWhiteSpace(ApiKeyEnvironment) || ApiKeyEnvironment.Length > 100)
            throw new InputValidationException("Worker apiKeyEnvironment is invalid.");
        if (PollSeconds is < 5 or > 300)
            throw new InputValidationException("Worker pollSeconds must be between 5 and 300.");
        if (TileScheme != "atlas-overworld-sparse-v1")
            throw new InputValidationException("The worker requires atlas-overworld-sparse-v1 for location renders.");
        if (!Directory.Exists(FullIntakeRoot))
            throw new InputValidationException("Worker intakeRoot does not exist.");
        if (!Directory.Exists(FullArchiveRoot))
            throw new InputValidationException("Worker archiveRoot does not exist.");
        if (!File.Exists(FullRendererProfile))
            throw new InputValidationException("Worker rendererProfile does not exist.");
        Directory.CreateDirectory(FullWorkRoot);
        Directory.CreateDirectory(FullPublishRoot);
        if (Overlaps(FullIntakeRoot, FullWorkRoot) ||
            Overlaps(FullIntakeRoot, FullPublishRoot) ||
            Overlaps(FullWorkRoot, FullPublishRoot))
            throw new InputValidationException("Worker intake, work, and publish roots must not overlap.");
        if (!Uri.TryCreate(PublicTileRoot.TrimEnd('/') + "/", UriKind.Absolute, out var tileRoot) ||
            tileRoot.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(tileRoot.Query) || !string.IsNullOrEmpty(tileRoot.Fragment))
            throw new InputValidationException("Worker publicTileRoot must be an HTTPS URL without a query or fragment.");
    }

    private static bool Overlaps(string first, string second) =>
        IsWithin(first, second) || IsWithin(second, first);

    private static bool IsWithin(string candidate, string root)
    {
        if (!string.Equals(Path.GetPathRoot(candidate), Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase))
            return false;
        var relative = Path.GetRelativePath(root, candidate);
        return relative == "." ||
            !relative.Equals("..", StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
