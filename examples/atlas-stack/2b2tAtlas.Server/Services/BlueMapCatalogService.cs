using System.Text.Json;
using Microsoft.Extensions.Options;

namespace _2b2tAtlas.Server.Services;

/// <summary>
/// Reads the immutable BlueMap manifest catalog and selects the newest completed
/// renderer profile for each Atlas render. Bad, partial, and superseded generations
/// are never advertised.
/// </summary>
public sealed class BlueMapCatalogService
{
    private readonly BlueMapOptions _options;
    private readonly object _gate = new();
    private DateTime _expiresUtc = DateTime.MinValue;
    private CatalogSnapshot _snapshot = CatalogSnapshot.Empty;

    /// <summary>Initializes the catalog with its immutable output-root settings.</summary>
    public BlueMapCatalogService(IOptions<BlueMapOptions> options) => _options = options.Value;

    /// <summary>Returns the best validated generation for an Atlas render, when available.</summary>
    public BlueMapGeneration? Find(int renderId)
    {
        EnsureCurrent();
        return _snapshot.ByRender.GetValueOrDefault(renderId);
    }

    /// <summary>
    /// Returns whether a content-addressed generation directory is the currently
    /// advertised, validated generation for one render.
    /// </summary>
    public bool IsAdvertisedGeneration(string generationName)
    {
        if (string.IsNullOrWhiteSpace(generationName)) return false;
        EnsureCurrent();
        return _snapshot.GenerationNames.Contains(generationName);
    }

    /// <summary>Returns bounded aggregate data from the same quality-gated catalog used by public render records.</summary>
    public BlueMapCatalogSummary GetSummary()
    {
        EnsureCurrent();
        return _snapshot.Summary;
    }

    private void EnsureCurrent()
    {
        if (DateTime.UtcNow < _expiresUtc) return;
        lock (_gate)
        {
            if (DateTime.UtcNow < _expiresUtc) return;
            _snapshot = LoadCatalog();
            _expiresUtc = DateTime.UtcNow.AddSeconds(Math.Clamp(_options.CatalogCacheSeconds, 5, 300));
        }
    }

    private CatalogSnapshot LoadCatalog()
    {
        if (!Directory.Exists(_options.OutputRoot)) return CatalogSnapshot.Empty;

        var candidates = new List<BlueMapGeneration>();
        var diagnosticGenerationCount = 0;
        long outputBytes = 0;
        // Generations are direct children of OutputRoot. Never recursively walk
        // their thousands of model files just to discover one manifest each.
        foreach (var generationRoot in Directory.EnumerateDirectories(_options.OutputRoot))
        {
            var path = Path.Combine(generationRoot, "manifest.json");
            if (!System.IO.File.Exists(path)) continue;

            try
            {
                using var document = JsonDocument.Parse(System.IO.File.ReadAllText(path));
                var root = document.RootElement;
                if (!root.TryGetProperty("Status", out var status) ||
                    !string.Equals(status.GetString(), "complete", StringComparison.OrdinalIgnoreCase) ||
                    !root.TryGetProperty("RenderId", out var renderIdValue)) continue;

                diagnosticGenerationCount++;
                if (root.TryGetProperty("OutputBytes", out var outputBytesValue) && outputBytesValue.TryGetInt64(out var manifestBytes) && manifestBytes > 0)
                    outputBytes += manifestBytes;

                if (Path.GetFileName(generationRoot).Contains(".superseded-", StringComparison.OrdinalIgnoreCase) ||
                    !System.IO.File.Exists(Path.Combine(generationRoot, "web", "index.html"))) continue;

                var renderId = renderIdValue.GetInt32();
                var profile = root.TryGetProperty("RendererProfileVersion", out var profileValue) ? profileValue.GetInt32() : 1;
                if (profile < Math.Max(1, _options.MinimumProfileVersion)) continue;
                // Profile 7 is the first Atlas profile whose source chunks are
                // relit in an isolated derivative and then audited back to the
                // exact original Anvil footprint. Do not advertise a partial or
                // pre-gate profile-7 directory merely because it has an index.
                if (profile >= 7 && !HasValidatedLightingAndPayload(root)) continue;
                var generated = root.TryGetProperty("GeneratedUtc", out var generatedValue) &&
                    DateTime.TryParse(generatedValue.GetString(), out var parsed) ? parsed.ToUniversalTime() : DateTime.MinValue;
                var dimension = root.TryGetProperty("Dimension", out var dimensionValue)
                    ? dimensionValue.GetString()?.Trim().ToLowerInvariant() ?? string.Empty
                    : string.Empty;
                var generationName = Path.GetFileName(generationRoot);
                var relativePath = $"{_options.RequestPath.TrimEnd('/')}/{Uri.EscapeDataString(generationName)}/web/";
                candidates.Add(new BlueMapGeneration(renderId, profile, generated, dimension, generationName, relativePath,
                    $"{_options.PublicOrigin.TrimEnd('/')}{relativePath}"));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (JsonException) { }
        }

        var byRender = candidates.GroupBy(item => item.RenderId).ToDictionary(
            group => group.Key,
            group => group.OrderByDescending(item => item.RendererProfileVersion)
                .ThenByDescending(item => item.GeneratedUtc).First());
        var validated = byRender.Values.ToList();
        var summary = new BlueMapCatalogSummary(
            validated.Count,
            validated.Count(item => item.Dimension == "overworld"),
            validated.Count(item => item.Dimension == "nether"),
            validated.Count(item => item.Dimension == "end"),
            diagnosticGenerationCount,
            outputBytes);
        return new CatalogSnapshot(
            byRender,
            new HashSet<string>(validated.Select(item => item.GenerationName), StringComparer.OrdinalIgnoreCase),
            summary);
    }

    private static bool HasValidatedLightingAndPayload(JsonElement root) =>
        root.TryGetProperty("QualityGate", out var qualityGate) &&
        qualityGate.ValueKind == JsonValueKind.Object &&
        qualityGate.TryGetProperty("Passed", out var qualityPassed) &&
        qualityPassed.ValueKind == JsonValueKind.True &&
        qualityGate.TryGetProperty("LocationStartExact", out var locationStartExact) &&
        locationStartExact.ValueKind == JsonValueKind.True &&
        root.TryGetProperty("RenderingProfile", out var renderingProfile) &&
        renderingProfile.ValueKind == JsonValueKind.Object &&
        renderingProfile.TryGetProperty("Relight", out var relight) &&
        relight.ValueKind == JsonValueKind.Object &&
        relight.TryGetProperty("FootprintAuditExact", out var footprintAudit) &&
        footprintAudit.ValueKind == JsonValueKind.True;

    private sealed record CatalogSnapshot(
        IReadOnlyDictionary<int, BlueMapGeneration> ByRender,
        IReadOnlySet<string> GenerationNames,
        BlueMapCatalogSummary Summary)
    {
        public static CatalogSnapshot Empty { get; } = new(
            new Dictionary<int, BlueMapGeneration>(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            BlueMapCatalogSummary.Empty);
    }
}

/// <summary>Aggregate quality and storage evidence for the immutable BlueMap catalog.</summary>
public sealed record BlueMapCatalogSummary(
    int ValidatedRenderCount,
    int OverworldValidated,
    int NetherValidated,
    int EndValidated,
    int DiagnosticGenerationCount,
    long OutputBytes)
{
    /// <summary>An empty catalog summary.</summary>
    public static BlueMapCatalogSummary Empty { get; } = new(0, 0, 0, 0, 0, 0);
}

/// <summary>Public location of one validated static BlueMap generation.</summary>
public sealed record BlueMapGeneration(
    int RenderId,
    int RendererProfileVersion,
    DateTime GeneratedUtc,
    string Dimension,
    string GenerationName,
    string RelativeUrl,
    string PublicUrl);
