using System.Text.Json;
using System.Text.RegularExpressions;
using Atlas.Ingestor.Security;

namespace Atlas.Ingestor.Models;

/// <summary>Defines validated operator metadata and world selection for an ingestion job.</summary>
/// <param name="Slug">Lowercase public slug.</param>
/// <param name="Name">Public display name.</param>
/// <param name="WorldDownloadDate">Attributed world-download date.</param>
/// <param name="Source">Source attribution.</param>
/// <param name="WorldRoot">Optional safe path selecting a world relative to the extraction root.</param>
/// <param name="Dimensions">Selected canonical dimensions, or <see langword="null"/> for automatic selection.</param>
/// <param name="DayNight">Whether the resulting map render supports day/night switching.</param>
/// <param name="Scale">Atlas scale label.</param>
/// <param name="Publish">Whether publication was requested.</param>
public sealed record IngestManifest(
    string Slug,
    string Name,
    DateOnly WorldDownloadDate,
    string Source,
    string? WorldRoot,
    IReadOnlyList<string>? Dimensions,
    bool DayNight,
    string Scale,
    bool Publish)
{
    private static readonly Regex SlugPattern = new(
        "^[a-z0-9](?:[a-z0-9-]{0,52}[a-z0-9])?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex ScalePattern = new(
        "^[1-9][0-9]*(?:\\.[0-9]+)?[km]?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly HashSet<string> AllowedKeys = new(StringComparer.Ordinal)
    {
        "schemaVersion", "slug", "name", "worldDownloadDate", "source",
        "worldRoot", "dimensions", "dayNight", "scale", "publish",
    };

    private static readonly HashSet<string> AllowedDimensions = new(StringComparer.Ordinal)
    {
        "overworld", "nether", "end",
    };

    /// <summary>Loads and strictly validates a schema-version 1 manifest.</summary>
    /// <param name="path">Path to the manifest JSON file.</param>
    /// <param name="cancellationToken">Token that cancels asynchronous file parsing.</param>
    /// <returns>The normalized, validated manifest.</returns>
    /// <exception cref="InputValidationException">The document shape, key set, value type, value range, date, dimension, or relative path is invalid.</exception>
    /// <exception cref="JsonException">The file is not valid JSON within the configured depth limit.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    /// <remarks>Unknown keys are rejected so misspelled security- or publication-relevant settings cannot be ignored.</remarks>
    public static async Task<IngestManifest> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var document = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 16,
        }, cancellationToken);

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InputValidationException("Manifest root must be a JSON object.");

        foreach (var property in root.EnumerateObject())
        {
            if (!AllowedKeys.Contains(property.Name))
                throw new InputValidationException($"Unknown manifest key: {property.Name}");
        }

        if (ReadInt(root, "schemaVersion") != 1)
            throw new InputValidationException("Manifest schemaVersion must be 1.");

        var slug = ReadString(root, "slug").Trim().ToLowerInvariant();
        if (!SlugPattern.IsMatch(slug))
            throw new InputValidationException("Slug must be 1-54 lowercase letters, numbers, or hyphens.");

        var name = ReadString(root, "name").Trim();
        var source = ReadString(root, "source").Trim();
        if (name.Length is < 1 or > 90)
            throw new InputValidationException("Name must be 1-90 characters so dimension labels remain within API limits.");
        if (source.Length is < 1 or > 200)
            throw new InputValidationException("Source must be 1-200 characters.");

        if (!DateOnly.TryParseExact(ReadString(root, "worldDownloadDate"), "yyyy-MM-dd", out var date))
            throw new InputValidationException("worldDownloadDate must use YYYY-MM-DD.");
        if (date > DateOnly.FromDateTime(DateTime.UtcNow))
            throw new InputValidationException("worldDownloadDate cannot be in the future.");

        var scale = ReadString(root, "scale").Trim().ToLowerInvariant();
        if (!ScalePattern.IsMatch(scale))
            throw new InputValidationException("Scale must look like 5k, 256k, or 1m.");

        var worldRoot = ReadOptionalString(root, "worldRoot")?.Replace('\\', '/').Trim('/');
        if (worldRoot is not null && !IsSafeRelativePath(worldRoot))
            throw new InputValidationException("worldRoot must be a safe relative path.");

        IReadOnlyList<string>? dimensions = null;
        if (root.TryGetProperty("dimensions", out var dimensionElement))
        {
            if (dimensionElement.ValueKind == JsonValueKind.String && dimensionElement.GetString() == "auto")
            {
                dimensions = null;
            }
            else if (dimensionElement.ValueKind == JsonValueKind.Array)
            {
                var values = dimensionElement.EnumerateArray()
                    .Select(value => value.ValueKind == JsonValueKind.String ? value.GetString()! : string.Empty)
                    .Select(value => value.ToLowerInvariant())
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (values.Length == 0 || values.Any(value => !AllowedDimensions.Contains(value)))
                    throw new InputValidationException("Dimensions must contain only overworld, nether, and end.");
                dimensions = values;
            }
            else
            {
                throw new InputValidationException("Dimensions must be 'auto' or an array.");
            }
        }

        return new IngestManifest(
            slug,
            name,
            date,
            source,
            worldRoot,
            dimensions,
            ReadBool(root, "dayNight", true),
            scale,
            ReadBool(root, "publish", false));
    }

    private static bool IsSafeRelativePath(string value) =>
        value.Length > 0 &&
        !Path.IsPathRooted(value) &&
        !value.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(part => part is "." or "..");

    private static string ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new InputValidationException($"Manifest {name} must be a string.");

    private static string? ReadOptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : throw new InputValidationException($"Manifest {name} must be a string.")
            : null;

    private static int ReadInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var result)
            ? result
            : throw new InputValidationException($"Manifest {name} must be an integer.");

    private static bool ReadBool(JsonElement root, string name, bool defaultValue) =>
        !root.TryGetProperty(name, out var value)
            ? defaultValue
            : value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : throw new InputValidationException($"Manifest {name} must be a boolean.");
}
